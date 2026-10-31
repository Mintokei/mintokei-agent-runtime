# Standalone runtime memory service

This is the deployable host for the runtime memory packages. It does not call or reference the
Memory Playground API. Mintokei keeps its SQLite application database; memory originals,
provenance, states, traces and jobs live in this service's PostgreSQL database.

## Local process

Requires .NET 10, PostgreSQL 17 with pgvector/pg_trgm, Ollama with `nomic-embed-text`, and
Codex CLI (tested 0.165.0) signed in on the service account. Run `codex login` first.
Only the backend accesses subscription credentials. OpenAI API and Codex App Server are
explicit alternative providers; failures never silently switch providers.

Set these server variables (PowerShell: `$env:NAME="value"`; Bash: `export NAME="value"`):

```text
ConnectionStrings__Memory=Host=localhost;Port=55432;Database=memory;Username=memory;Password=<database-password>
MemoryService__Token=<random-server-secret>
MemoryService__SourceSystem=mintokei
MemoryService__OwnerId=local
MemoryService__Namespace=live
Memory__Root=/absolute/path/to/persistent-memory-data
Memory__EmbeddingUrl=http://localhost:11435
ASPNETCORE_URLS=http://127.0.0.1:5052
```

```bash
dotnet run --project src/Mintokei.Memory.Service
```

Prompts and JSON schemas are embedded in the PostgreSQL package and materialized on first
start. Existing versions are preserved. You do not need a Playground checkout or working directory.
Keep `Memory__Root` and PostgreSQL persistent. Model subprocesses receive read-only reasoning
requests in isolated directories; background extraction cannot enable workspace-write/full access.

For a **new database**, seed operation settings through configuration, for example:

```text
Memory__Configuration__Operations__extraction__Provider=codex_app_server
Memory__Configuration__Operations__extraction__Model=<model-supported-by-your-account>
Memory__Configuration__Operations__extraction__ReasoningEffort=low
Memory__Configuration__CapturePayloads=true
```

Other operation keys are `reference-detection`, `reference-resolution`, `reconciliation`,
`state-update`, and `summarization`. Use appsettings JSON if your shell cannot export variable
names containing hyphens. Default provider is Codex CLI using its configured default model.
For `openai_api`, set `OPENAI_API_KEY` on the backend only. Full prompt/payload tracing defaults
to off in this host. Explicit initial configuration is persisted once: restart does not overwrite
stored settings or historical prompt versions. To edit existing settings, use Playground's settings
screen attached to the same database, or update the `default` Settings record administratively.
Playground is optional for operation, useful for inspection.

## Docker Desktop / Linux

From this directory set `MEMORY_POSTGRES_PASSWORD` and `MEMORY_SERVICE_TOKEN` in your environment
(or a local uncommitted `.env`). The example is Linux x64, including Docker Desktop/WSL2.

```bash
docker compose up -d postgres ollama
docker compose exec ollama ollama pull nomic-embed-text
docker compose build memory-service
docker compose run --rm memory-service codex -c 'cli_auth_credentials_store="file"' login --device-auth
docker compose up -d memory-service
```

The container runs as uid 10001. Credentials and runtime files live in the private `memory-data`
volume, not the image. Database and embedding ports are private. The HTTP service binds to host
loopback; for multiple machines use authenticated private networking/TLS. Do not mount a developer
workspace or Docker socket into the extraction service.

## Connect Mintokei

On the **Mintokei API**:

```text
Memory__Enabled=true
Memory__BaseUrl=http://127.0.0.1:5052/
Memory__SourceSystem=mintokei
Memory__Token=<same-random-server-secret>
```

For a containerized Mintokei API, use the memory service's private network DNS name instead of
loopback. The feed and normal workspace tasks use the same backend. Completed turns go through
Mintokei's durable outbox; the runtime stores originals, queues extraction, validates evidence,
reconciles memories/state, and queues semantic indexing. Search includes pending original messages.
No extra foreground planner is introduced by this packaging change.

## API and operations

`X-Memory-Token` is required on all `/api/memory/v1` endpoints:

- `POST /turns`: immutable completed-turn envelope; duplicate delivery is safe, changed delivery conflicts.
- `GET /turns/{sourceTurnId}`: extraction status, attempts and checkpoint (not embedding completion).
- `POST /search`: hybrid memory/history search with original ranking/evidence.
- `POST /sources`: original authorized evidence and source-app IDs.

`GET /health` reports database readiness only. Check job status/traces for provider/indexing health.
One token is bound to one owner/namespace/source-system configured by the host. Clients cannot
choose another owner. This is **single-owner deployment**, not a multi-tenant auth system.
Multiple workers use PostgreSQL leases and advisory locks; extraction remains causal per conversation.
Three failed attempts leave a failed job visible for investigation. Retry failed jobs administratively
(or through an attached Playground); later turns are held behind failed earlier extraction.

Existing Playground databases remain compatible: point this service at the same database and
persistent root to retain memory IDs, evidence and prompt versions. Back up before switching.
The Playground UI can instead run separately using these exact runtime packages against that same
store; SignalR notifications are local to each host, so reload its inspector to see external worker updates.

Tests: `dotnet test tests/Mintokei.Memory.Tests`. Native standalone and product regression checks
are documented in `docs/memory-service-migration.md`.
