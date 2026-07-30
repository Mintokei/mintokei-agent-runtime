# Samples

Every sample here builds and runs. What differs is how much of the world each one needs first, which is the
main thing this page is for — a sample that needs Docker and credentials fails in the same unhelpful way as
one that is simply misconfigured, so it is worth knowing which you are looking at before you start.

## Which one do I want?

| Sample | Shows | Needs | One command |
|---|---|---|---|
| [`LocalAgentMinimal`](LocalAgentMinimal) | one agent CLI, one prompt | an installed, authenticated CLI | — |
| [`ControlPlaneLocal`](ControlPlaneLocal) | several local sessions, tracked by key | an installed, authenticated CLI | — |
| [`SandboxSessionMinimal`](SandboxSessionMinimal) | the sandbox session lifecycle | **nothing** (fakes the runtime + backend) | — |
| [`SandboxPoolMinimal`](SandboxPoolMinimal) | warm pool: top-up, recycle, reap | **nothing** (fakes the runtime) | — |
| [`SandboxRunnerHostMinimal`](SandboxRunnerHostMinimal) | a real agent turn in a real container | Docker, image, credentials, reachability | [`run-sandbox-runner-host-sample.sh`](../scripts/run-sandbox-runner-host-sample.sh) |
| [`SandboxLifecycleExplicit`](SandboxLifecycleExplicit) | the same, every step written out | Docker, image, credentials, reachability | [`run-sandbox-lifecycle-sample.sh`](../scripts/run-sandbox-lifecycle-sample.sh) |
| [`SharedSandboxMinimal`](SharedSandboxMinimal) | two sessions in ONE sandbox, and the refusal | Docker, image, credentials, reachability | [`run-shared-sandbox-sample.sh`](../scripts/run-shared-sandbox-sample.sh) |
| [`RemoteRunnerMinimal`](RemoteRunnerMinimal) | a host that accepts a remote runner | a worker (the script starts a local one) | [`run-remote-runner-sample.sh`](../scripts/run-remote-runner-sample.sh) |
| [`RemoteSandboxMinimal`](RemoteSandboxMinimal) | a sandbox on a *remote worker* | worker + Docker + image + reachability | [`run-remote-sandbox-sample.sh`](../scripts/run-remote-sandbox-sample.sh) |
| [`BrokerSandboxMinimal`](BrokerSandboxMinimal) | broker egress: no secret in the box | worker, broker image, **real `https://` backend** | — (see its README) |

New to this? Start with `LocalAgentMinimal`, then `SandboxSessionMinimal` (no infrastructure at all), then
`SandboxRunnerHostMinimal` for the real thing.

Looking for a combination no sample covers — Kubernetes, `proxy` egress, a persistent workspace?
[`docs/usage-options.md`](../docs/usage-options.md) documents every choice and how the runtime behaves under
it, and says plainly which ones have no sample here.

## What "reachability" means

The samples that launch a container are the ones people get stuck on, and almost always for one of two
reasons. Both produce the *same* message — *"the sandbox exited before its agent runner could connect"* — and
neither is a problem with the sample:

1. **The host is bound to `127.0.0.1`.** The sandbox dials back on `host.docker.internal`, which resolves to
   the Docker bridge gateway. A loopback listener is unreachable from inside a container, and you get
   `Connection refused`. Every sample here binds `0.0.0.0` for exactly this reason — don't "fix" it back.
2. **A firewall is dropping container→host traffic.** ufw and most cloud images deny all inbound by default.
   The symptom is different — a ~100s *timeout*, because a DROP sends no RST — and the fix is to let the
   bridge in:

   ```bash
   sudo ufw allow from 172.17.0.0/16 to any port <port>,<port+1> proto tcp
   ```

   Remove it afterwards with the same command using `delete allow`.

`scripts/sample-preflight.sh` checks both from a real container before the sample runs, so you get the fix
instead of the symptom. The `run-*-sample.sh` scripts all use it.

## Credentials

The container runs the agent CLI, so it needs the CLI's credentials. Point `Sandbox__ClaudeConfigHostDir` /
`Sandbox__ClaudeConfigJsonHostFile` at host paths (the scripts default them to `$HOME/.claude` and
`$HOME/.claude.json`).

Root-owned `0600` credentials are fine and normal: the runtime stages a **uid-readable copy** before mounting,
because the sandbox runs as a non-root uid. You do not need to loosen permissions on your own credentials —
and shouldn't.

A real turn also needs a **repo**, because the session starts in `/repos/<name>`, which only exists once one
has been cloned. Without it the runner still enrolls and the session still dispatches, but the CLI has no
valid working directory.

## Workers are not necessarily other machines

A worker is just the runner process dialing out, so it can run on the same box. That is what
`run-remote-runner-sample.sh` and `run-remote-sandbox-sample.sh` do: mint a token, start a throwaway runner
against a temp data dir, wait for it to connect, then drive the demo. The distributed code path is the real
one — only the address is loopback.
