#!/bin/sh
# git credential helper for broker-egress sandboxes: fetch the credential from the per-session broker's
# git-credential mint endpoint on demand, so the token is NEVER seeded into the sandbox (no ~/.git-credentials,
# no key on disk). git invokes this as `git-credential-broker <get|store|erase>` with key=value lines on stdin.
#
# Install (done by the sandbox entrypoint in broker mode):
#   git config --global credential.helper /usr/local/bin/git-credential-broker
#   export MINTOKEI_BROKER_CRED_URL=http://<broker-host>:<mint-port>/git-credential
set -eu

# Only serve 'get'; 'store'/'erase' are no-ops (nothing is persisted in the sandbox).
[ "${1:-}" = "get" ] || exit 0
[ -n "${MINTOKEI_BROKER_CRED_URL:-}" ] || exit 0

host=
while IFS= read -r line; do
  [ -z "$line" ] && break
  case "$line" in
    host=*) host=${line#host=} ;;
  esac
done
[ -n "$host" ] || exit 0

# The broker returns git's credential format directly (username=.. / password=..); relay it to git verbatim.
if command -v curl >/dev/null 2>&1; then
  curl -fsS "${MINTOKEI_BROKER_CRED_URL}?host=${host}" 2>/dev/null || true
elif command -v wget >/dev/null 2>&1; then
  wget -qO- "${MINTOKEI_BROKER_CRED_URL}?host=${host}" 2>/dev/null || true
fi
