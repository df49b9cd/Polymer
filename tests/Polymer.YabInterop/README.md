# Polymer ↔️ yab Interop Harness

This sample spins up a minimal Polymer HTTP server and exercises it with [`yab`](https://github.com/yarpc/yab), Uber's YARPC CLI. It provides a lightweight sanity check that Polymer responds to YARPC-style HTTP calls issued by yab.

## Prerequisites

1. Install `.NET` 8/10 SDK (already required for Polymer).
2. Install `yab` (requires Go):

   ```bash
   go install go.uber.org/yarpc/yab@latest
   export PATH="$PATH:$(go env GOPATH)/bin"
   ```

## Run the demo

```bash
bash tests/Polymer.YabInterop/run-yab.sh
```

The script will:

1. Build the Polymer solution.
2. Launch a simple Polymer echo service.
3. Invoke the service with `yab`.
4. Print the response and shut the server down.

You can customise the run via environment variables:

- `MODE` – `http`, `grpc`, or `both` (default `http`).
- `PORT` – HTTP port to bind (default `8080`).
- `GRPC_PORT` – gRPC port to bind (default `9090`).
- `DURATION` – Seconds to keep the server alive (default `10`).
- `REQUEST_PAYLOAD` – JSON payload sent via `yab` (default `{"message":"hello from yab"}`).
- `CALLER` – Caller name advertised to the server (default `yab-demo`).

Examples:

```bash
# HTTP request only
PORT=9090 bash tests/Polymer.YabInterop/run-yab.sh

# gRPC request using the bundled proto
MODE=grpc GRPC_PORT=7070 bash tests/Polymer.YabInterop/run-yab.sh

# Exercise both in a single run
MODE=both REQUEST_PAYLOAD='{"message":"multimode"}' bash tests/Polymer.YabInterop/run-yab.sh
```

Expected output for the gRPC path resembles:

```
Polymer gRPC echo server listening on 127.0.0.1:7070
Issuing yab request (grpc)...
... (yab response payload)
yab invocation complete
```

## Project layout

- `tests/Polymer.YabInterop/Program.cs` – Minimal Polymer HTTP echo service.
- `tests/Polymer.YabInterop/run-yab.sh` – Helper script to launch the server and issue a `yab` request.

Use this harness as a starting point for richer interop tests or integration into CI. The gRPC mode emits protobuf messages defined in `Protos/echo.proto`.
