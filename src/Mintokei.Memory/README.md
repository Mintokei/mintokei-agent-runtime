# In-process memory

Mintokei uses memory through dependency injection, with no memory HTTP server or Playground dependency.

| Package | Responsibility |
|---|---|
| `Mintokei.Memory` | Storage-free contracts, evidence validation, state rules |
| `Mintokei.Memory.Postgres` | Ingestion/search, extraction, references, provenance, PostgreSQL/pgvector, embeddings and versioned assets |
| `Mintokei.Memory.Hosting` | Initialization and durable background processing inside the host |

```csharp
services.AddPostgresMemory(connectionString);
services.Configure<MemoryServiceOptions>(options => options.SourceSystem = "mintokei");
services.AddSingleton<IMemoryReasoningRunner, MyHostReasoningRunner>();
services.AddMemoryProcessing();

await memory.StoreTurnAsync(completedTurn, cancellationToken);
var result = await memory.SearchAsync(new MemorySearch("Harbor project"), cancellationToken);
var evidence = await memory.LoadEvidenceAsync(new MemoryEvidenceRequest(sourceIds), cancellationToken);
```

`IMemoryReasoningRunner` is a host callback, not an AI provider implementation. Runtime supplies a
versioned system prompt, untrusted data prompt, JSON schema and timeout. The host returns the answer
and available execution metadata. Runtime validates the schema and evidence before persisting facts.
Mintokei implements the callback with its existing `IOneShotAgentRunner`, selected by Library agent
or AI Utility Tool settings. There are no memory-specific API credentials, CLI processes or transports.

Set `Memory:Root` to a persistent directory. The hosted initializer sets up PostgreSQL and bundled
prompt/schema assets before the worker starts. Ingestion saves originals and queues extraction
atomically without an inline LLM call. A host with a separate application database needs a durable
outbox. Provenance and checkpoints survive restarts.

Debug payload capture defaults to false; timing/status and available selected-tool metadata remain.
One-shot runners don't expose all effective settings or usage, so those fields remain unavailable.
See [host configuration and limitations](../../docs/memory-service-migration.md).
