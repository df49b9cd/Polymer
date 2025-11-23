# HTTP Transport Guidance

OmniRelay’s HTTP transport mirrors YARPC-Go’s semantics but leaves several
operational decisions to the host. This guide captures the defaults and the
additional configuration you should apply before exposing an HTTP inbound in
production.

## TLS requirements

`HttpInbound` refuses to bind `https://` URLs unless you supply a server
certificate. When bootstrapping manually, pass `HttpServerTlsOptions` with a
populated `Certificate`:

```csharp
var inbound = HttpInbound.TryCreate(
    new[] { new Uri("https://0.0.0.0:8443") },
    serverTlsOptions: new HttpServerTlsOptions
    {
        Certificate = LoadCertificate(),
        ClientCertificateMode = ClientCertificateMode.NoCertificate,
        CheckCertificateRevocation = true
    }).ValueOrThrow();
```

Configuration hosts wire the same structure via the `inbounds.http[].tls`
section:

```jsonc
"inbounds": {
  "http": [
    {
      "urls": [ "https://0.0.0.0:8443" ],
      "tls": {
        "certificatePath": "certs/server.pfx",
        "certificatePassword": "change-me",
        "clientCertificateMode": "NoCertificate",
        "checkCertificateRevocation": true
      }
    }
  ]
}
```

Omitting a certificate while declaring an HTTPS URL results in a startup
failure. Plan to keep the private key offline or load it from a secret store,
and rotate certificates proactively. If the service needs mutual TLS, set
`ClientCertificateMode` to `RequireCertificate` and supply validation logic in
ASP.NET pipeline hooks.

## Keep the inbound behind a gateway

HTTP transports target service-to-service RPC. OmniRelay does not ship CORS,
rate limiting, authentication, or request-shaping middleware in the box.
Expose the inbound through a gateway or reverse proxy that terminates TLS,
applies authnz, and enforces quotas before the request reaches OmniRelay.

When you run behind proxies like NGINX, add the usual forwarding headers
(`X-Forwarded-For`, `X-Forwarded-Proto`) and disable proxy buffering for SSE
endpoints (`proxy_buffering off;`). OmniRelay sets `X-Accel-Buffering: no`
automatically, but the upstream proxy must honour it.

## Runtime limits and backpressure

The HTTP inbound exposes Kestrel limits and transport-specific guards through
`inbounds.http[].runtime`. Configure these to match the traffic profile and the
capacity of downstream dependencies:

| Setting | Purpose |
| --- | --- |
| `maxRequestBodySize` | Caps the ASP.NET request body length (bytes). |
| `maxInMemoryDecodeBytes` | Stops unary bodies from being buffered entirely in memory. |
| `maxRequestLineSize` / `maxRequestHeadersTotalSize` | Harden request-line and header totals against abuse. |
| `keepAliveTimeout` | Overrides Kestrel’s idle connection timeout. |
| `requestHeadersTimeout` | Limits how long clients can take to finish sending headers. |
| `serverStreamMaxMessageBytes` | Rejects server-sent event frames above the threshold. |
| `serverStreamWriteTimeout` | Aborts SSE responses when writes stall (slow or dead clients). |
| `duplexMaxFrameBytes` | Caps WebSocket frame payloads for duplex streams. |
| `duplexWriteTimeout` | Cancels WebSocket sends that cannot drain in time. |

All sizes are integers (bytes); timeouts accept standard `TimeSpan` strings
(`"00:00:05"`), ISO 8601 durations, or millisecond integers.

Example configuration:

```jsonc
"inbounds": {
  "http": [
    {
      "urls": [ "http://0.0.0.0:8080" ],
      "runtime": {
        "maxRequestBodySize": 8388608,
        "maxInMemoryDecodeBytes": 1048576,
        "maxRequestLineSize": 16384,
        "maxRequestHeadersTotalSize": 32768,
        "keepAliveTimeout": "00:02:00",
        "requestHeadersTimeout": "00:00:15",
        "serverStreamMaxMessageBytes": 65536,
        "serverStreamWriteTimeout": "00:00:10",
        "duplexMaxFrameBytes": 262144,
        "duplexWriteTimeout": "00:00:05"
      }
    }
  ]
}
```

`serverStreamMaxMessageBytes` and `duplexMaxFrameBytes` protect the dispatcher
from unbounded payloads and surface `RESOURCE_EXHAUSTED` to the client. The
write timeouts ensure slow consumers are cancelled rather than buffering
indefinitely in `PipeWriter` or WebSocket queues. Tune the limits alongside the
gateway/proxy buffer policies referenced above.

HTTP/3 listeners inherit the same guard rails. OmniRelay mirrors
`maxRequestHeadersTotalSize` into `KestrelServerOptions.Limits.Http3.MaxRequestHeaderFieldSize`
and, unless you override `runtime.http3.idleTimeout`, reuses the general
`keepAliveTimeout` as the QUIC idle timeout. When the current MsQuic shim cannot
honour those tunables, the startup log emits a structured warning
(`http inbound: transport=http protocol=http3 ...`) so you can fall back to the
platform defaults.

## HTTP/3

OmniRelay ships HTTP/3 support behind an explicit feature flag. Enabling it
adds `HttpProtocols.Http3` alongside the existing HTTP/1.1 + HTTP/2 listeners
and emits `Alt-Svc` headers so capable clients can upgrade.

Requirements:

- The inbound must bind `https://` URLs and load a certificate that can
  negotiate TLS 1.3. Certificates missing a private key or pinned to TLS 1.2
  will fail startup when HTTP/3 is requested.
- The host OS must expose MsQuic. Windows Server 2022 / Windows 11 ship it by
  default; Linux nodes must preload `libmsquic` 2.2 or newer. Unsupported
  platforms (macOS, older Windows builds) now throw before the listener starts.

Enable the transport per listener via configuration:

```jsonc
"inbounds": {
  "http": [
    {
      "urls": [ "https://0.0.0.0:8443" ],
      "runtime": {
        "enableHttp3": true,
        "http3": {
          "enableAltSvc": true,
          "idleTimeout": "00:01:00",
          "keepAliveInterval": "00:00:20",
          "maxBidirectionalStreams": 128,
          "maxUnidirectionalStreams": 32
        }
      },
      "tls": {
        "certificatePath": "certs/server.pfx",
        "certificatePassword": "change-me"
      }
    }
  ]
}
```

Key behaviours:

- `enableHttp3` defaults to `false`. When `true`, OmniRelay validates the
  certificate, verifies TLS 1.3 support, and refuses to bind plain HTTP
  endpoints.
- `http3.enableAltSvc` controls whether Kestrel emits `Alt-Svc`; it is enabled
  by default so HTTP/1.1 and HTTP/2 clients learn about the HTTP/3 endpoint.
- `http3.maxBidirectionalStreams` / `http3.maxUnidirectionalStreams` map
  directly to MsQuic stream limits and are applied during listener startup.
- `http3.idleTimeout` configures the connection idle timeout enforced by MsQuic.
  Values below 30 seconds tend to evict long-polling callers; we recommend
  60-120 seconds for HTTP workloads that expect bursty traffic. If you omit the
  setting, OmniRelay falls back to `runtime.keepAliveTimeout` so HTTP/1.1, HTTP/2
  and HTTP/3 share the same idle policy. MsQuic implementations that do not
  expose this knob trigger a startup warning with
  `transport=http protocol=http3` in the log context.
- `http3.keepAliveInterval` sends MsQuic pings for otherwise idle connections.
  Start with 20-30 seconds when you run behind load balancers that recycle idle
  UDP flows and avoid values below 10 seconds unless downstream requires them.

OmniRelay applies these options to both HTTP and gRPC inbounds. If the running
framework rejects a setting (for example, due to an outdated MsQuic shim),
startup succeeds but the dispatcher logs a warning so operators can fall back
to the platform defaults.

Remember that HTTP/3 still falls back to HTTP/2/1.1 when clients or middleboxes
block UDP 443. Keep your existing HTTP/2 observability in place and monitor the
startup logs for any HTTP/3 prerequisites that fail validation.

Every response now publishes the negotiated protocol via the `Rpc-Protocol`
header alongside the existing `Rpc-Transport` metadata. Use the pair to align
metrics across HTTP versions and to confirm downgrade scenarios continue to
return the standard JSON error envelope and `Retry-After` semantics.

### Related HTTP/3 docs

- [HTTP/3 Developer Guide](./http3-developer-guide.md) — prerequisites, enabling locally and in staging/production, and troubleshooting.
- [HTTP/3 / QUIC Troubleshooting FAQ](./http3-faq.md) — common issues (ALPN, alt-svc, UDP/443, macOS, curl) and fixes.

### Runbook: Graceful shutdown with HTTP/3

When a node begins draining (for example, during a rolling deployment), OmniRelay
waits for in-flight work to complete and rejects new HTTP/3 requests with the
same `Retry-After: 1` semantics used for HTTP/1.1 and HTTP/2. Use the following
checks when validating a drain:

1. Issue a baseline request before the drain:

  ```bash
   curl --http3 -i https://omnirelay.example.test/rpc -X POST \
     -H 'X-YARPC-Procedure: health::ping'
   ```

   Expect `200 OK` and `HTTP/3` in the status line.
2. Trigger your normal drain mechanism (for example, signal the host to stop or
   remove the instance from the load balancer) and immediately probe again:

  ```bash
   curl --http3 -i https://omnirelay.example.test/rpc -X POST \
     -H 'X-YARPC-Procedure: health::ping'
   ```

   During the drain window the response switches to `HTTP/1.1 503 Service Unavailable`
   or `HTTP/3 503`, and includes `Retry-After: 1`. Existing requests continue to
   completion.
3. gRPC listeners exhibit the same behaviour. Using the OmniRelay CLI:

  ```bash
   omnirelay request grpc health::ping \
     --addresses https://omnirelay.example.test:9090 \
     --grpc-http3
   ```

   While draining, the CLI reports `StatusCode.Unavailable` and prints the
   `retry-after: 1` trailer.

Operators should monitor application logs for the `server shutting down` warning
that accompanies drained gRPC calls and watch readiness probes flip to
`503 Service Unavailable` until the dispatcher finishes all in-flight work.

> **Observability:** `/omnirelay/introspect`, `/healthz`, and `/readyz` respond
> identically over HTTP/1.1, HTTP/2, and HTTP/3; no additional configuration is
> required for HTTP/3 clients. Testing has not uncovered any protocol-specific
> limitations for these endpoints.

### WebSockets in HTTP/3 deployments

Duplex procedures on the HTTP transport still use classic WebSockets. When you
enable HTTP/3, OmniRelay upgrades the listener to QUIC for unary and streaming
RPCs (SSE/POST), but the WebSocket handshake remains on HTTP/1.1 (`ws://` or
`wss://`). This keeps existing clients compatible while QUIC-aware services can
choose alternatives:

- Prefer gRPC duplex streaming when both sides need QUIC semantics.
- For HTTP workloads, consider redesigning long-lived chats to use server/client
  streams (SSE + POST) when the WebSocket fallback is not desirable.
- If you continue using WebSockets, no extra configuration is required; OmniRelay
  automatically builds the correct `ws://`/`wss://` URI even when the inbound
  is advertising HTTP/3.

Set conservative limits when QUIC is enabled:

- `runtime.serverStreamMaxMessageBytes`: 524288 keeps server-stream frames small
  enough to respect MsQuic’s initial flow-control window.
- `runtime.duplexMaxFrameBytes`: 16384 matches the new default frame size used by
  OmniRelay’s WebSocket bridge and avoids overwhelming intermediaries that mirror
  QUIC buffer behaviour.
- Add alerts that trigger when `StreamCallContext.CompletionStatus` reports
  `DeadlineExceeded` more than a handful of times per minute; it is the first
  signal that downstream clients cannot drain responses fast enough.

## Server-sent events

Server-stream RPCs use SSE with hardened defaults:

- Clients must send `Accept: text/event-stream`. Requests without the header
  receive `406 Not Acceptable`.
- Responses include `Cache-Control: no-cache`, `Connection: keep-alive`, and
  `X-Accel-Buffering: no` to keep proxy buffers from queuing events.
- Non-text encodings are base64-encoded and tagged with `encoding: base64`
  in the event body.
- Transport failures return a JSON error payload (same shape as unary
  responses) instead of partial SSE frames.

Document these behaviours for downstream clients so they can negotiate the
correct headers and parse base64 frames when consuming binary payloads.

## Tracing

The HTTP transport does not emit OpenTelemetry spans on its own. Add
`RpcTracingMiddleware` to any inbound or outbound pipeline that should record
spans:

```csharp
options.UnaryInboundMiddleware.Add(new RpcTracingMiddleware());
options.StreamInboundMiddleware.Add(new RpcTracingMiddleware());
options.UnaryOutboundMiddleware.Add(new RpcTracingMiddleware());
```

`RpcTracingMiddleware` creates an `Activity` per RPC, flowing headers through
`Rpc-Trace-*` metadata. Pair it with `AddOmniRelayDispatcherFromConfiguration(...).AddOpenTelemetry`
configuration so the runtime exports HTTP metrics and spans that share the same
`Resource` attributes.
