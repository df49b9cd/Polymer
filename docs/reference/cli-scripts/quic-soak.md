# QUIC Soak and Performance Harness

This harness uses OmniRelay CLI and Linux `tc netem` to simulate QUIC conditions (packet loss, latency, burst) and run HTTP/3 and gRPC-over-HTTP/3 soaks.


## Prerequisites

- Linux host with sudo permissions
- `tc` (iproute2)
- OmniRelay CLI built and available on PATH (`omnirelay`)
- Target OmniRelay server listening on HTTPS with HTTP/3 enabled

## Quick start

```bash
export TARGET_URL=https://127.0.0.1:8443
export DURATION=300s
export RPS=200
export LOSS=1%
export DELAY=20ms
export BURST=0
export IFACE=lo

bash samples/DistributedDemo/cli-scripts/quic-soak.sh
```

The script runs two phases:
1. HTTP/3 unary RPCs
2. gRPC unary RPCs over HTTP/3 (`--grpc-http3`)

Both honor `DURATION` and `RPS`. Netem is applied and cleaned up automatically.

## Scenarios


## Metrics and dashboards


## Safety


