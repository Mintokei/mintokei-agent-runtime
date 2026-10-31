# Memory runtime

`Mintokei.Memory` contains DB-free contracts, source identity mapping, validation, state deltas,
context packing and the authenticated `ISharedMemory` HTTP client. It has no product or EF dependency.
Task/workspace identifiers in export envelopes are opaque source metadata, not product entities.

Optional implementations are separate projects:

| Project | Responsibility |
|---|---|
| `Mintokei.Memory.Postgres` | Raw history, extraction, reconciliation, references, pgvector/Ollama, Mem0 adapter, provenance, traces and bundled prompts/schemas |
| `Mintokei.Memory.Providers` | Codex CLI, Codex App Server and explicit OpenAI API routing |
| `Mintokei.Memory.Hosting` | Authenticated memory endpoints, durable background worker, host notification hooks |
| `Mintokei.Memory.Service` | Standalone deployment host |

Use `AddPostgresMemory`, `AddMemoryProviders`, `AddMemoryProcessing` and `MapSharedMemory`
when embedding in another .NET host. Initialize the database and embedded assets with
`DatabaseSetup.InitializeAsync` before accepting work. Hosts can replace `ILlmProvider`,
`IEmbeddingProvider`, `IMemoryEvents`, `IMemoryJobObserver`, `IArtifactStore` and `IMemoryStore`.
The core runtime does not require these optional persistence/hosting dependencies.

Mintokei normally calls the standalone service using `HttpSharedMemory`; Playground embeds the same
implementation and supplies its SignalR/replay hooks. There is no extraction dependency on a
running Playground server. See [deployment instructions](../../samples/SharedMemoryService/README.md).

Callers freeze completed-turn envelopes and retry durably. An acknowledgement means originals
were ingested, not extraction/indexing completion. Human and agent sender identities stay distinct.
Historical content is data, evidence is validated, and provider failures never change billing mode.
