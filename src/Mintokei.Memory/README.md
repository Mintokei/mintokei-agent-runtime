# In-process memory

Mintokei consumes memory through dependency injection. There is no memory HTTP client, server,
separate deployment, or dependency on Memory Playground.

| Package | Responsibility |
|---|---|
| `Mintokei.Memory` | DB-free contracts, evidence validation, state rules and context helpers |
| `Mintokei.Memory.Postgres` | `IMemoryService` implementation, extraction, references, provenance, PostgreSQL/pgvector, local embeddings and bundled assets |
| `Mintokei.Memory.AgentEngine` | `ILlmProvider` adapter over the existing `AgentSessionFactory`, `CodexBackend` and host `ICommandLineRunnerFactory` |
| `Mintokei.Memory.Hosting` | Initialization and durable background job processing inside the application host |

```csharp
// The application already registers its ICommandLineRunnerFactory and logging.
services.AddPostgresMemory(connectionString);
services.Configure<MemoryServiceOptions>(options => options.SourceSystem = "mintokei");
services.Configure<MemoryEngineOptions>(options => options.RuntimeHome = "/persistent/memory/codex");
services.AddMemoryAgentEngine();
services.AddMemoryProcessing();

// Inject IMemoryService into application services or agent tools.
await memory.StoreTurnAsync(completedTurn, cancellationToken);
var result = await memory.SearchAsync(new MemorySearch("Harbor project"), cancellationToken);
var evidence = await memory.LoadEvidenceAsync(new MemoryEvidenceRequest(sourceIds), cancellationToken);
```

Set `Memory:Root` on the host configuration to a persistent data directory. The hosted initializer
creates/initializes the database and bundled prompt/schema files before starting the worker.
`StoreTurnAsync` saves originals and queues extraction atomically; it does not run an LLM inline.
Source identities and exact evidence survive restarts. The caller remains responsible for a durable
outbox if its application data and memory use separate databases.

The default memory provider is `agent_engine`. The first adapter supports local Codex App Server
through the existing engine, with a fresh ephemeral session per operation, strict output schema,
read-only permissions, no MCP/web/shell tools, and an isolated login/profile directory. It uses the
host's existing local runner, not a second process launcher. Main user sessions and workspace/task
entities are never created or resumed for extraction. Other providers can implement `ILlmProvider`;
legacy `codex_cli` / `openai_api` settings fail explicitly rather than silently switching execution.

Engine events are recorded as normalized events. Usage/effective settings not exposed by the engine
remain unavailable; they are not estimated. See [host setup](../../docs/memory-service-migration.md).
