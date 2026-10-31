# In-process memory integration

Memory is a library hosted inside Mintokei's API. The Playground keeps its independent implementation.
The initial standalone-service design has been replaced; there is no memory URL, HTTP token, Docker
image or separate server to deploy. PostgreSQL/pgvector and Ollama remain supporting services.

## Configuration on Mintokei

```text
Memory__Enabled=true
ConnectionStrings__Memory=Host=localhost;Port=55432;Database=mintokei_memory;Username=<user>;Password=<password>
Memory__Root=/absolute/persistent/memory-directory
Memory__SourceSystem=mintokei
Memory__OwnerId=local
Memory__Namespace=live
Memory__EmbeddingUrl=http://localhost:11435
Memory__EmbeddingModel=nomic-embed-text
Memory__EmbeddingDimensions=768
```

Choose **Settings → AI & utilities → Memory agent** in Mintokei. Select a Library agent to reuse
its tool, model and reasoning effort. The default **Use AI Utility Tool** inherits the utility tool
and its CLI defaults. The existing **Runner Machine** setting chooses local or remote execution.
Use the selected tool's normal sign-in on that machine. Memory has no API keys, login copies,
Codex home, transport settings or paid-provider fallback of its own.

The host adapter dispatches to Mintokei's existing keyed `IOneShotAgentRunner`. Codex, Claude,
Copilot and OpenCode use their existing one-shot implementations; a tool without a registered
one-shot runner fails visibly. Selecting an agent does not inject its coding system prompt or
skills into extraction: memory owns the extraction prompt and JSON contract. These calls disable
tools, use an empty temporary local directory and validate JSON against the schema before persistence.
Remote Codex receives its schema inline because the API's temporary schema file isn't on the runner.
This is tool restriction, not a disposable OS sandbox. Use the host's runner isolation for that.

Optional first-start memory settings:

```json
{
  "Memory": {
    "Configuration": {
      "CapturePayloads": false,
      "Operations": {
        "extraction": {
          "PromptVersion": "v2",
          "SchemaVersion": "v1",
          "TimeoutSeconds": 120,
          "Enabled": true
        }
      }
    }
  }
}
```

Operation settings describe the memory pipeline only. Model/tool selection belongs to Mintokei.
Other operation keys are `reference-detection`, `reference-resolution`, `reconciliation`,
`state-update` and `summarization`. Settings/prompts are seeded once and persisted. Old provider,
model and effort fields in stored operation settings are ignored; select the Library agent in Mintokei.
Full prompt/payload capture is opt-in and defaults to false. Original IDs and stored history survive.
Unused Playground chat, context-answer and experiment contracts/assets are no longer shipped.
Existing database columns/tables remain compatible; this change does not delete recorded history.

## Application use

```csharp
services.AddPostgresMemory(connectionString);
services.Configure<MemoryServiceOptions>(configuration.GetSection("Memory"));
services.AddSingleton<IMemoryReasoningRunner, MyHostReasoningRunner>();
services.AddMemoryProcessing();
```

The host implements `IMemoryReasoningRunner.RunAsync(request, cancellationToken)` by forwarding the
system prompt, data prompt, schema and timeout to its existing agent abstraction, returning
`MemoryReasoningResult`. No engine/provider package is required by memory. Runtime owns prompt
rendering, schema/evidence validation, reconciliation, durable jobs, storage and tracing. Mintokei's
`MemoryReasoningRunner` resolves Library settings and calls `IOneShotAgentRunner`; it doesn't launch
another CLI or manage credentials itself.

Configure `MemoryServiceOptions` from trusted host settings, not user/model input. Inject `IMemoryService`:

- `StoreTurnAsync`: validate the frozen envelope, persist original messages, queue extraction, return a receipt.
- `SearchAsync`: search indexed memory and authorized original history.
- `LoadEvidenceAsync`: load exact sources with native application identity metadata.

`MemoryProcessingWorker` runs inside the API host, leasing PostgreSQL jobs and processing each
conversation in order. Extraction validates structured output/evidence, reconciles facts, applies
state deltas and queues semantic indexing. Durable checkpoints/leases make retries safe.
Mintokei's SQLite turn outbox retries the in-process write to PostgreSQL independently of chat completion.
No extraction call blocks the user-facing answer. No new foreground AI planning is added.

Memory disabled is the default; it requires no PostgreSQL service or an agent sign-in. With memory enabled,
initial database availability is required at host startup; later failures leave pending jobs/outbox rows.
A receipt means originals were accepted, not that extraction or indexing has finished.

## Boundaries

One authorized owner/namespace per installation. Original task data stays in SQLite; derived memory
and a source copy stay in PostgreSQL. No automatic backfill or deletion/rewind retraction yet.
One-shot calls return their answer and selected settings; unavailable usage/model observations remain null.
Prompt/schema files ship as embedded assets and are materialized without overwriting existing versions.
There is no dependency on Playground for configuration, storage or execution.

## Tests

`dotnet test tests/Mintokei.Memory.Tests` covers pure rules and the host reasoning contract.
Set `MEMORY_TEST_POSTGRES` to an isolated PostgreSQL/pgvector test connection to run direct DI storage,
idempotency and ownership checks. These tests never require a memory HTTP server.
Real Mintokei extraction and recall are tested separately with a signed-in Codex account.

## Explicit retrieval scope

`MemorySearchScope.AllAuthorizedHistory` is the default. Use `CurrentTask` to restrict retrieval,
and supply the native application task ID (the service maps it to its stored conversation ID):

```csharp
var matches = await memory.SearchAsync(new MemorySearch(
    "project database", Scope: MemorySearchScope.CurrentTask, CurrentTaskId: taskId), ct);
var evidence = await memory.LoadEvidenceAsync(new MemoryEvidenceRequest(
    sourceIds, MemorySearchScope.CurrentTask, taskId), ct);
```

The host chooses the permitted scope; tool/model arguments may only narrow it. These DI methods trust
that host-selected boundary and always enforce the configured owner/namespace. Missing current-task IDs
and unknown enum values are rejected. Current-task lexical/vector retrieval includes only memories whose
available evidence is entirely within that task; mixed-source summaries are conservatively excluded.
Messages, neighboring evidence, goals and artifacts use the same boundary.

`CompletedMemoryTurn.SearchScope` freezes the extraction boundary into the job. Workers pass it through
reference resolution, evidence validation and extraction context without matching English phrases.
Legacy turn payloads default to all authorized history and retain their original retry hashes. A narrower
`MemoryConfiguration.SearchScope` can further restrict background processing. Mintokei uses its
`Memory:SearchScope` host setting for initial context, both memory tools and completed-turn envelopes.
Choosing a scope does not add an LLM call, erase already-supplied session history, or constrain unrelated
task-management tools. Authenticated human source browsing remains separate from agent lookup scope.
