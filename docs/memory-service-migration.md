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

Run Codex login as the API service account. The adapter defaults to `$CODEX_HOME` or that account's
`.codex` directory. Override with `Memory:Engine:CodexLoginHome`. Only authentication/model-cache
files are copied to a private reasoning profile; personal MCP/hook/project instructions are not copied.
The API's existing runner factory launches Codex through `AgentSessionFactory` / `CodexBackend`.
Reasoning currently runs locally; remote reasoning and other engine backends are not yet supported.
Main agent execution, including remote user tasks, remains unchanged.

Optional first-start settings in appsettings JSON:

```json
{
  "Memory": {
    "Configuration": {
      "CapturePayloads": false,
      "Operations": {
        "extraction": {
          "Provider": "agent_engine",
          "Model": null,
          "ReasoningEffort": "low",
          "ExecutionMode": "read_only"
        }
      }
    }
  }
}
```

When no model is configured, Codex selects its default model. Other operation keys include
`reference-detection`, `reference-resolution`, `reconciliation`, `state-update`, and `summarization`.
Settings are seeded once and persisted; restart does not overwrite historical settings/prompts.
For an existing memory database, explicitly update the `default` Settings JSON before switching
legacy providers to `agent_engine`. Unsupported providers fail visibly; there is no paid API fallback.
Full prompt/payload capture is opt-in. Existing message/memory IDs and database mappings are preserved.

## Application use

Register `AddPostgresMemory`, `AddMemoryAgentEngine` and `AddMemoryProcessing`. These reuse the host's
`ICommandLineRunnerFactory`; the memory packages do not register an alternative runner. Configure
`MemoryServiceOptions` from trusted host settings, not user/model input. Inject `IMemoryService`:

- `StoreTurnAsync`: validate the frozen envelope, persist original messages, queue extraction, return a receipt.
- `SearchAsync`: search indexed memory and authorized original history.
- `LoadEvidenceAsync`: load exact sources with native application identity metadata.

`MemoryProcessingWorker` runs inside the API host, leasing PostgreSQL jobs and processing each
conversation in order. Extraction validates structured output/evidence, reconciles facts, applies
state deltas and queues semantic indexing. Durable checkpoints/leases make retries safe.
Mintokei's SQLite turn outbox retries the in-process write to PostgreSQL independently of chat completion.
No extraction call blocks the user-facing answer. No new foreground AI planning is added.

Memory disabled is the default; it requires no PostgreSQL service or Codex login. With memory enabled,
initial database availability is required at host startup; later failures leave pending jobs/outbox rows.
A receipt means originals were accepted, not that extraction or indexing has finished.

## Boundaries

One authorized owner/namespace per installation. Original task data stays in SQLite; derived memory
and a source copy stay in PostgreSQL. No automatic backfill or deletion/rewind retraction yet.
The engine exposes normalized events; unavailable usage/model observations remain null.
Prompt/schema files ship as embedded assets and are materialized without overwriting existing versions.
There is no dependency on Playground for configuration, storage or execution.

## Tests

`dotnet test tests/Mintokei.Memory.Tests` covers pure rules and scripted engine execution.
Set `MEMORY_TEST_POSTGRES` to an isolated PostgreSQL/pgvector test connection to run direct DI storage,
idempotency and ownership checks. These tests never require a memory HTTP server.
Real Mintokei extraction and recall are tested separately with a signed-in Codex account.

Validation on 2026-10-31: the runtime suite passed **944 tests**, with **15 environment-specific
tests skipped**; all 60 memory tests ran, including the isolated PostgreSQL check. A real Mintokei
API instance hosted extraction through the existing engine, persisted the public Mariner release
name Juniper-58 and a local embedding, then a fresh task recalled it using both memory tools.
The evidence and original task link survived API restart. No memory HTTP server, URL or token was
configured, and production was not redeployed. One earlier synthetic codeword fixture produced
a valid empty extraction; extraction quality remains evidence-dependent.
