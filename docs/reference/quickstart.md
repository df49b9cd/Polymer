# OmniRelay Quickstart

This quickstart walks through the `samples/Quickstart.Server` host, which demonstrates HTTP unary and oneway endpoints alongside gRPC unary and server-streaming calls. The sample also shows how to plug in middleware and configure a peer chooser for gRPC outbounds.

## Prerequisites

- .NET SDK 10.0.0 RC2 (installed automatically via `global.json`).
- Optional: [`grpcurl`](https://github.com/fullstorydev/grpcurl) for ad-hoc gRPC requests.

## 1. Start the sample service

```bash
dotnet run --project samples/Quickstart.Server
```

The process hosts HTTP on `http://127.0.0.1:8080` and gRPC on `http://127.0.0.1:9090`. Console logging middleware reports every inbound call so you can see the pipelines in action.

## 2. Call the HTTP unary endpoint

Use `curl` to POST JSON to the `hello::greet` procedure. The HTTP transport expects the procedure name in the `Rpc-Procedure` header.

```bash
curl -s \
  -H "Rpc-Procedure: hello::greet" \
  -H "Content-Type: application/json" \
  --data '{"name":"Codex"}' \
  http://127.0.0.1:8080/yarpc/v1/hello
```

Sample response:

```json
{
  "message": "Hello Codex!",
  "transport": "http",
  "issuedAt": "2024-09-30T18:15:04.1924829+00:00"
}
```

## 3. Fire an HTTP oneway call

Oneway procedures still use HTTP POST, returning `202 Accepted` immediately.

```bash
curl -i \
  -H "Rpc-Procedure: telemetry::publish" \
  -H "Content-Type: application/json" \
  --data '{"level":"info","area":"orders","message":"created"}' \
  http://127.0.0.1:8080/yarpc/v1/telemetry
```

The console prints the decoded event, and the response keeps the connection lightweight (no body, only the ack headers).

## 4. gRPC unary request

Invoke the same greeting procedure over gRPC. `grpcurl` automatically maps JSON into the request payload; use the service name `samples.quickstart` and procedure `hello::greet`.

```bash
grpcurl -plaintext \
  -d '{"name":"Codex"}' \
  -H 'rpc-encoding: json' \
  127.0.0.1:9090 samples.quickstart/hello::greet
```

You should receive the JSON response and see the middleware logs indicating a gRPC transport.

## 5. gRPC server streaming

The weather stream returns a configurable number of updates. Set `count` and `intervalSeconds` in the request payload.

```bash
grpcurl -plaintext \
  -d '{"location":"seattle","count":3,"intervalSeconds":1}' \
  -H 'rpc-encoding: json' \
  127.0.0.1:9090 samples.quickstart/weather::stream
```

`grpcurl` prints each message as it arrives; the server stops after sending the requested number of updates. Cancelling the command triggers stream disposal via the middleware.

## 6. Inspect middleware and peer chooser configuration

- Middleware logging is provided by `ConsoleLoggingMiddleware` and added to the unary, oneway, and streaming inbound stacks (`samples/Quickstart.Server/Program.cs:77`).
- The sample registers both HTTP and gRPC inbounds plus placeholder outbounds. The gRPC outbound uses `FewestPendingPeerChooser` to demonstrate custom load balancing (`samples/Quickstart.Server/Program.cs:58`).
- Procedures show JSON codecs across transports: unary (`hello::greet`), oneway (`telemetry::publish`), and streaming (`weather::stream`) in `samples/Quickstart.Server/Program.cs:93`.

These code paths are a good starting point for experimenting with middleware ordering, codecs, and outbound routing before wiring the runtime into a production host.

## 7. Next steps: ResourceLease mesh features

Once you are comfortable with transports and middleware, explore the ResourceLease mesh stack:

- Read `docs/architecture/omnirelay-rpc-mesh.md` for the end-to-end architecture (replication, diagnostics, failure drills).
- Dive into `docs/reference/distributed-task-leasing.md` to wire `ResourceLeaseDispatcherComponent`, durable replicators (SQLite, gRPC, object storage), and deterministic replays.
- Review `docs/reference/diagnostics.md#resourcelease-mesh-instruments` to export the new lease depth, peer health, and replication lag metrics into your telemetry backend.

Those docs walk through sharding helpers, backpressure listeners, control-plane endpoints, and automation scripts that help you run OmniRelay as a resilient, peer-aware RPC mesh.
