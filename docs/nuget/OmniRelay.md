# OmniRelay

OmniRelay is the .NET implementation of Uber's YARPC runtime. It provides a unified dispatcher that routes RPC traffic across HTTP and gRPC transports while layering codecs, middleware, and peer management strategies.

## Highlights

- Single dispatcher surface for unary, oneway, and streaming RPCs.
- Built-in codecs for JSON, protobuf, and raw payloads.
- Middleware for tracing, metrics, logging, retries, deadlines, and rate limiting.
- Peer choosers (round-robin, fewest-pending, two-random-choice) with circuit breaking.
- Health and introspection endpoints plus OpenTelemetry integrations.

## Documentation

- Quickstart: `docs/reference/quickstart.md`
- Middleware guide: `docs/reference/middleware.md`
- Error handling: `docs/reference/errors.md`
- Streaming support: `docs/reference/streaming.md`

## Getting Started

```bash
dotnet add package OmniRelay
```

See the repository README for a complete host setup, configuration samples, and CLI tooling.

