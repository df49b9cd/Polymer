#!/usr/bin/env bash
set -euo pipefail

MODE="${MODE:-http}"
PORT="${PORT:-8080}"
GRPC_PORT="${GRPC_PORT:-9090}"
DURATION="${DURATION:-10}"
REQUEST_PAYLOAD="${REQUEST_PAYLOAD:-{\"message\":\"hello from yab\"}}"
CALLER="${CALLER:-yab-demo}"

if ! command -v yab >/dev/null 2>&1; then
  echo "yab executable not found in PATH. Install it via 'go install go.uber.org/yarpc/yab@latest'." >&2
  exit 1
fi

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PROJECT_DIR=$(cd "$SCRIPT_DIR/../../" && pwd)

pushd "$PROJECT_DIR" >/dev/null

dotnet build --nologo >/dev/null

DOTNET_ARGS=("--duration" "$DURATION")

case "$MODE" in
  http)
    DOTNET_ARGS=("--port" "$PORT" "--duration" "$DURATION")
    ;;
  grpc)
    DOTNET_ARGS=("--no-http" "--grpc-port" "$GRPC_PORT" "--duration" "$DURATION")
    ;;
  both)
    DOTNET_ARGS=("--port" "$PORT" "--grpc-port" "$GRPC_PORT" "--duration" "$DURATION")
    ;;
  *)
    echo "Unknown MODE '$MODE'. Supported values: http, grpc, both." >&2
    exit 1
    ;;
esac

dotnet run --project tests/Polymer.YabInterop/Polymer.YabInterop.csproj -- "${DOTNET_ARGS[@]}" &
SERVER_PID=$!
trap 'kill $SERVER_PID >/dev/null 2>&1 || true' EXIT

sleep 2

echo "Issuing yab request (${MODE})..."

if [[ "$MODE" == "http" || "$MODE" == "both" ]]; then
  yab --http --peer "http://127.0.0.1:${PORT}" --service echo --procedure echo::ping --encoding json --request "$REQUEST_PAYLOAD" --caller "$CALLER" --timeout 2s
fi

if [[ "$MODE" == "grpc" || "$MODE" == "both" ]]; then
  PROTO_PATH="$SCRIPT_DIR/Protos"
  yab --grpc \
      --peer "127.0.0.1:${GRPC_PORT}" \
      --caller "$CALLER" \
      --service echo.EchoService \
      --procedure Ping \
      --proto-file "$PROTO_PATH/echo.proto" \
      --request "$REQUEST_PAYLOAD" \
      --timeout 2s
fi

echo "yab invocation complete"

wait $SERVER_PID 2>/dev/null || true

popd >/dev/null
