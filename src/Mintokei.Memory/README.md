# Shared memory contracts

`Mintokei.Memory` is a provider-neutral runtime library for completed-turn export and evidence-based retrieval. It has no database, web UI, workspace, or Codex dependency. Hosts supply an authenticated HttpClient to `HttpSharedMemory` or implement `ISharedMemory` themselves.

- `CompletedMemoryTurn`: stable application task/turn/message identities and original messages, including outcome and sender.
- `MemoryIdentity`: stable, source-system-specific identifiers for independent host storage.
- `MemoryInstructions`: common instructions and a delimited untrusted-context envelope.
- `ISharedMemory`: ingest a completed turn, search knowledge, and load original evidence.

The caller must freeze an export before retrying, use durable retries, authorize the owner partition outside model arguments, and keep credentials on the server. An ingestion acknowledgement is not proof that extraction or embedding indexing has finished. Agent-originated messages must retain their sender identity.

V1 HTTP implementation targets the Memory Playground `/api/memory/v1` server-to-server bridge. Configure `X-Memory-Token` on the HttpClient and set its BaseAddress. This is an actual service dependency: extraction and PostgreSQL/pgvector storage currently run in that host, not inside this library.
