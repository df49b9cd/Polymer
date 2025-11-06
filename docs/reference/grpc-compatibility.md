# gRPC HTTP/3 Compatibility

Status of HTTP/3 (QUIC) support across common gRPC client SDKs, reviewed November 2025. Verify against upstream documentation before production use.

| Language | Library | HTTP/3 status | Notes |
| --- | --- | --- | --- |
| .NET | Grpc.Net.Client | Supported | Configure `Version=3.0` + `VersionPolicy=RequestVersionOrHigher` or use a delegating handler. OmniRelay outbounds expose `enableHttp3` and version policy via config. See [HttpClient HTTP/3](https://learn.microsoft.com/dotnet/core/extensions/httpclient-http3) and [gRPC HTTP/3](https://learn.microsoft.com/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3). |
| Java | grpc-java (Netty) | Experimental / evolving | Netty HTTP/3 is progressing, but gRPC over HTTP/3 is not broadly GA in grpc-java. Track issue [grpc/grpc-java#8897](https://github.com/grpc/grpc-java/issues/8897). Prefer HTTP/2 in production. |
| Go | grpc-go | Experimental / not GA | Official grpc-go targets HTTP/2. Community experiments exist; consider ConnectRPC with quic-go for HTTP/3 trials. Track issue [grpc/grpc-go#5186](https://github.com/grpc/grpc-go/issues/5186). |
| Python | grpcio (C-core) | Not GA | No generally available HTTP/3 support as of late 2025; defaults to HTTP/2. |
| Node/TS | @grpc/grpc-js | Not GA | HTTP/2 default; HTTP/3 not generally available. |

Recommendations

- Default to HTTP/2 for cross-language consumers. Enable HTTP/3 for .NET clients where QUIC prerequisites are met, and retain downgrade paths.
- Validate fallback behavior (HTTP/3 → HTTP/2) in automated tests and monitor fallback rates in production.
- Track upstream status:
  - gRPC meta: HTTP/3 tracker [grpc/grpc#19126](https://github.com/grpc/grpc/issues/19126)
  - grpc-dotnet configuration: [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3)
