#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
BANNED_FILE="$ROOT/eng/banned-apis.txt"

if [[ ! -f "$BANNED_FILE" ]]; then
  echo "banned api list missing: $BANNED_FILE" >&2
  exit 1
fi

fail=0
while IFS= read -r pattern; do
  [[ -z "$pattern" || "$pattern" =~ ^# ]] && continue
  if rg --hidden --glob '!bin/**' --glob '!obj/**' --glob '!artifacts/**' --glob '!.git/**' --glob '!eng/banned-apis.txt' --regexp "$pattern" "$ROOT/src" "$ROOT/tests" >/tmp/banned-hit.txt 2>/dev/null; then
    echo "BANNED API FOUND: $pattern" >&2
    cat /tmp/banned-hit.txt >&2
    fail=1
  fi
done < "$BANNED_FILE"
rm -f /tmp/banned-hit.txt

exit $fail
