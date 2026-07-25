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
# NOTE: the sandbox image ships node (for the agent CLIs) but NOT curl/wget, so node is the reliable fetcher —
# without this fallback the helper returned nothing and every broker-mode PRIVATE clone failed with
# "could not read Username". Prefer curl/wget if present; else use node's http module.
url="${MINTOKEI_BROKER_CRED_URL}?host=${host}"
if command -v curl >/dev/null 2>&1; then
  curl -fsS "$url" 2>/dev/null || true
elif command -v wget >/dev/null 2>&1; then
  wget -qO- "$url" 2>/dev/null || true
elif command -v node >/dev/null 2>&1; then
  node -e 'require("http").get(process.argv[1],function(r){if(r.statusCode!==200){r.resume();return}var d="";r.on("data",function(c){d+=c});r.on("end",function(){process.stdout.write(d)})}).on("error",function(){})' "$url" 2>/dev/null || true
fi
