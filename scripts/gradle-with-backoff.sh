#!/usr/bin/env bash
# Runs a Gradle build in the current directory (if it has ./gradlew) or apps/android, retrying ONLY when a repository answers HTTP 429 (rate limit),
# with exponential backoff (60s, 120s, 240s). Other failures (compile errors, tests) fail immediately.
# Usage: scripts/gradle-with-backoff.sh assembleDebug [more tasks...]
set -uo pipefail
[ -x ./gradlew ] || cd "$(dirname "$0")/../apps/android"
delay=60
for attempt in 1 2 3 4; do
  log=$(mktemp)
  ./gradlew "$@" --no-daemon 2>&1 | tee "$log"
  status=${PIPESTATUS[0]}
  if [ "$status" -eq 0 ]; then rm -f "$log"; exit 0; fi
  if ! grep -q "status code 429" "$log"; then rm -f "$log"; exit "$status"; fi
  rm -f "$log"
  [ "$attempt" -eq 4 ] && break
  echo "Repository rate limit (HTTP 429). Waiting ${delay}s before attempt $((attempt + 1))..." >&2
  sleep "$delay"; delay=$((delay * 2))
done
echo "Giving up after repeated HTTP 429 responses." >&2
exit 1
