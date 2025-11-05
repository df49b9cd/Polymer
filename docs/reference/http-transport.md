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
var inbound = new HttpInbound(
    new[] { "https://0.0.0.0:8443" },
    serverTlsOptions: new HttpServerTlsOptions
    {
        Certificate = LoadCertificate(),
        ClientCertificateMode = ClientCertificateMode.NoCertificate,
        CheckCertificateRevocation = true
    });
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
`Rpc-Trace-*` metadata. Pair it with `AddOmniRelayDispatcher().AddOpenTelemetry`
configuration so the runtime exports HTTP metrics and spans that share the same
`Resource` attributes.

