# gRPC HTTP/3 Compatibility

This page summarizes known HTTP/3 (QUIC) support across common gRPC client SDKs as of 2025. Always verify against upstream documentation before adopting in production.

- .NET (Grpc.Net.Client): Supported. Configure per Microsoft guidance to use HTTP/3 (Version 3.0 and VersionPolicy). OmniRelay enables ALPN, optional keep-alives, and can request HTTP/3 per-call. See:
  - Use HTTP/3 with HttpClient: [docs](https://learn.microsoft.com/en-us/dotnet/core/extensions/httpclient-http3)
  - Troubleshoot gRPC on .NET – Configure client to use HTTP/3: [docs](https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3)
- Java (grpc-java): HTTP/2 is the default transport. HTTP/3 support depends on Netty’s HTTP/3 implementation and is still evolving. Treat HTTP/3 as experimental until officially documented by grpc-java. Fallback to HTTP/2 for production.
- Go (grpc-go): HTTP/2 is the default. gRPC over HTTP/3 (QUIC) is not generally available; community experiments exist (e.g., ConnectRPC with quic-go). Prefer HTTP/2 in production.
- Python (grpcio): Uses the C-core; HTTP/2 is the default. No generally available HTTP/3 support at this time.
- Node/TypeScript: The gRPC native and gRPC-JS clients primarily target HTTP/2 today. HTTP/3 support is not generally available yet.

Recommendations:

- Default to HTTP/2 for cross-language clients. Enable HTTP/3 for .NET workloads where client and server environments meet QUIC requirements, and retain a downgrade path.
- Document and test fallback behavior. When HTTP/3 handshakes fail, clients should automatically continue over HTTP/2.
- Track upstream issues for current status:
  - gRPC: Support HTTP/3 (tracker): [github.com/grpc/grpc#19126](https://github.com/grpc/grpc/issues/19126)
  - grpc-dotnet HTTP/3 docs: [learn.microsoft.com](https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3)
