# YARPCore CLI Tool

`yarpcore` is a .NET global tool that helps Polymer operators validate configuration, inspect a running dispatcher, and issue ad-hoc RPCs over HTTP or gRPC. It mirrors the ergonomics of `yab` while staying aligned with Polymer's transport metadata and codec stack.

## Features

- `yarpcore config validate` — load layered `appsettings*.json` files and ensure the dispatcher can be constructed.
- `yarpcore introspect` — fetch `/polymer/introspect` and print either a compact summary or the raw JSON snapshot.
- `yarpcore request` — issue unary calls over HTTP or gRPC, with profiles for JSON and protobuf payloads.
- `yarpcore benchmark` — drive concurrent HTTP or gRPC requests and report latency/throughput stats (YAB-style).
- `yarpcore script run` — replay automation scripts (JSON) that combine requests, delays, and introspection probes.

## Quick start

```bash
# Install from a local package feed
# (publish with `dotnet pack src/YARPCore.Cli/YARPCore.Cli.csproj -c Release -o artifacts/cli`)
dotnet tool install --global YARPCore.Cli --add-source artifacts/cli

# Validate config and execute a smoke test call
yarpcore config validate --config appsettings.json
yarpcore request \
  --transport http \
  --url http://127.0.0.1:8080/yarpc/v1 \
  --service echo \
  --procedure echo::ping \
  --profile json:pretty \
  --body '{"message":"hello"}'
```

For protobuf services, supply a descriptor set and let the CLI translate JSON request bodies on the fly:

```bash
yarpcore request \
  --transport grpc \
  --address http://127.0.0.1:9090 \
  --service echo \
  --procedure Ping \
  --profile protobuf:echo.EchoRequest \
  --proto-file descriptors/echo.protoset \
  --body '{"message":"hello from CLI"}'
```

Run a quick load test with the benchmark command:

```bash
yarpcore benchmark \
  --transport http \
  --url http://127.0.0.1:8080/yarpc/v1 \
  --service echo \
  --procedure echo::ping \
  --profile json:pretty \
  --body '{"message":"load-test"}' \
  --concurrency 20 \
  --requests 500 \
  --warmup 5s
```

See `docs/reference/cli.md` for automation recipes, script samples, and CI integration tips.
