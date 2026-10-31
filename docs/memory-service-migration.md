# Memory engine extraction into runtime — 2026-10-31

## Ownership and compatibility

The actual engine moved from Playground into runtime packages. The core library has no EF,
ASP.NET or product dependencies. PostgreSQL persistence, optional LLM adapters, HTTP hosting
and the executable are separate projects. Product repositories consume runtime via submodule.
Moved C# types use runtime namespaces, file-scoped namespaces and separate files; runtime DI
extensions compose the engine. Hosts supply notification and replay callbacks instead of importing UI types.

Playground now references the runtime packages and retains only chat/UI/replay-specific orchestration.
No second implementation of extraction, evidence validation, semantic storage or memory jobs remains.
Original database mappings, memory/source IDs, configuration JSON and immutable prompt versions are
preserved. A legacy seed advisory-lock key is intentionally retained for safe coexistence.
Codex CLI, App Server and explicit OpenAI API support are retained; prompts/context are unchanged.

The standalone service does not reference a Playground project or call a Playground endpoint.
Single-owner token authorization is configured by the host, not chosen by a model. Full prompt/payload
capture is opt-in. PostgreSQL claims, renewable leases, per-conversation locks and ownership-checked
completion allow recovery without silently repeating persistent facts.

## Verification

- Runtime solution: **974 passed, 15 skipped**, no failures. Includes **90 memory tests** for evidence,
  temporal context, schemas, scripted provider protocols, assets and DB-free core boundaries.
- Playground after relocating tests: **46 passed, 19 opt-in integration tests skipped**. Remaining
  coverage includes chat, parallel execution, interaction handling, context routing and coding policies.
- Standalone native runtime process on an isolated database, real Codex CLI 0.165.0,
  `gpt-6-luna` / low effort: extracted the test fact “Harbor workshop codename = Willow-73”.
  Raw source evidence, state/checkpoint, one confirmed memory and a 768-dimensional nomic-embed-text
  embedding persisted. Extraction and sync jobs both completed on their first attempt.
- Measured extraction pipeline: **5.51 seconds**; LLM extraction **4.11 seconds**; semantic sync
  **152 ms**. This is one correctness smoke test, not a latency comparison.
- Missing-token request rejected; identical envelope accepted as duplicate; changed envelope under
  the same turn ID rejected. Search and original-source retrieval returned the expected fact.
- Restarted the standalone process and retrieved the same persisted memory/evidence.

Production services/databases were not replaced during these checks. Validation records are synthetic.
The migration adds no foreground model calls. The Docker recipe and live product checks are recorded below.

## Deployment

See [the standalone service guide](../samples/SharedMemoryService/README.md). Mintokei's default
memory URL is now port 5052. Existing configured URLs remain explicit overrides. Deploy the service,
set the shared server token, and point Mintokei at it. Playground may optionally connect to the same
store for debugging; it is not required to ingest, extract, search or serve evidence.

Known inherited limits remain: one owner per deployment; no automatic backfill of old completed tasks;
rewind/delete in the source app does not yet retract previously exported facts; optional live-provider,
Kubernetes and Windows-only tests require their respective environments. PostgreSQL schema evolution
still uses the existing initialization strategy; this migration does not change the stored schema.

## Container check

The service image builds successfully with Codex CLI pinned to 0.165.0. The non-root host
starts from the image with bundled assets and the existing validation database. Codex extraction
was verified in the native host; container validation checks deployment and authorization.
