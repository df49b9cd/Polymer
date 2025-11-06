#!/usr/bin/env bash
set -euo pipefail

# QUIC soak/perf harness using OmniRelay CLI and Linux tc netem for packet loss / latency injection.
# Requires: Linux with sudo, tc, bash; OmniRelay CLI built and on PATH (omnirelay)

TARGET_URL=${TARGET_URL:-"https://127.0.0.1:8443"}
DURATION=${DURATION:-"300s"}
RPS=${RPS:-"200"}
LOSS=${LOSS:-"1%"}
DELAY=${DELAY:-"20ms"}
BURST=${BURST:-"0"}
IFACE=${IFACE:-"lo"}

cleanup() {
  echo "Cleaning up tc netem..."
  sudo tc qdisc del dev "$IFACE" root || true
}
trap cleanup EXIT

echo "Applying netem: loss=$LOSS delay=$DELAY burst=$BURST iface=$IFACE"
sudo tc qdisc add dev "$IFACE" root netem loss $LOSS delay $DELAY $BURST

# HTTP/3 soak
echo "Running HTTP/3 soak: $DURATION at $RPS rps"
omnirelay bench http \
  --url "$TARGET_URL" \
  --http3 \
  --duration "$DURATION" \
  --rps "$RPS" \
  --procedure ping \
  --encoding protobuf || true

# gRPC over HTTP/3 soak
echo "Running gRPC HTTP/3 soak: $DURATION at $RPS rps"
omnirelay bench grpc \
  --address "$TARGET_URL" \
  --grpc-http3 \
  --duration "$DURATION" \
  --rps "$RPS" \
  --service test \
  --procedure ping \
  --encoding protobuf || true

cleanup
