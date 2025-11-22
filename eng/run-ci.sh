#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "${SCRIPT_DIR}/.." && pwd)"

usage() {
  cat <<'USAGE'
Usage: eng/run-ci.sh [--arch <x64|arm64>] [docker build args...]

Options:
  --arch <value>    Force the Docker platform (x64 -> linux/amd64, arm64 -> linux/arm64).
  -h, --help        Show this help text.

Any additional arguments are forwarded to `docker build`.
USAGE
}

to_platform() {
  local input="$1"
  local lowered
  lowered="$(printf '%s' "$input" | tr '[:upper:]' '[:lower:]')"
  case "$lowered" in
    x64|amd64|x86_64)
      echo "linux/amd64"
      ;;
    arm64|aarch64)
      echo "linux/arm64"
      ;;
    *)
      echo ""
      return 1
      ;;
  esac
}

detect_host_platform() {
  case "$(uname -m)" in
    x86_64|amd64)
      echo "linux/amd64"
      ;;
    arm64|aarch64)
      echo "linux/arm64"
      ;;
    *)
      echo ""
      ;;
  esac
}

ARCH_OVERRIDE=""
FORWARDED_ARGS=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --arch)
      if [[ $# -lt 2 ]]; then
        echo "Missing value for --arch" >&2
        exit 1
      fi
      ARCH_OVERRIDE="$2"
      shift 2
      ;;
    --arch=*)
      ARCH_OVERRIDE="${1#*=}"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      FORWARDED_ARGS+=("$1")
      shift
      ;;
  esac
done

PLATFORM=""
if [[ -n "$ARCH_OVERRIDE" ]]; then
  PLATFORM="$(to_platform "$ARCH_OVERRIDE")" || {
    echo "Unsupported architecture: $ARCH_OVERRIDE" >&2
    exit 1
  }
else
  PLATFORM="$(detect_host_platform)"
fi

CMD=(docker build --file "${REPO_ROOT}/Dockerfile.ci" --target ci --progress=plain)
if [[ -n "$PLATFORM" ]]; then
  CMD+=(--platform "$PLATFORM")
fi
if (( ${#FORWARDED_ARGS[@]} )); then
  CMD+=("${FORWARDED_ARGS[@]}")
fi
CMD+=("${REPO_ROOT}")

exec "${CMD[@]}"
