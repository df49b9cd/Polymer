# HTTP/3 / QUIC Troubleshooting FAQ

This FAQ aggregates common issues and fixes when enabling HTTP/3 with OmniRelay.

See also:

- Kestrel HTTP/3: [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/http3)
- HttpClient HTTP/3: [learn.microsoft.com](https://learn.microsoft.com/dotnet/core/extensions/httpclient-http3)
- QUIC platform dependencies: [learn.microsoft.com](https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies)
- gRPC HTTP/3: [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3)

## Why don’t clients upgrade to HTTP/3?

- Check that responses include `Alt-Svc`. OmniRelay enables it when HTTP/3 is on; upstream proxies must not strip it.
- Confirm UDP/443 is open end-to-end. Many firewalls allow TCP/443 but block UDP/443 by default.
- Ensure TLS 1.3 is negotiated and the server certificate supports modern ciphers (`TLS_AES_*`, `TLS_CHACHA20_*`).
- Some clients won’t use HTTP/3 for self-signed certificates. Browsers typically require a trusted CA for HTTP/3 upgrades.

## macOS: How do I run HTTP/3 locally?

- Install MsQuic via Homebrew: `brew install libmsquic`.
- Export the dynamic library path before running:
  - `export DYLD_FALLBACK_LIBRARY_PATH=$DYLD_FALLBACK_LIBRARY_PATH:$(brew --prefix)/lib`
- Prefer `HttpClient` or CLI for testing. Browsers usually won’t upgrade to HTTP/3 with self-signed dev certs.

## Linux: How do I install MsQuic?

- Use the distro package manager:
  - apt: `sudo apt-get install libmsquic`
  - dnf: `sudo dnf install libmsquic`
  - yum: `sudo yum install libmsquic`
  - zypper: `sudo zypper install libmsquic`
  - Alpine: `apk add libmsquic` (edge/community may be required)
- Ensure OpenSSL and libnuma dependencies are present. .NET 7+ requires libmsquic 2.2+.

## How do I force clients to try HTTP/3 but still be compatible?

- Use `RequestVersionOrHigher` and `Version=3.0` (OmniRelay outbounds set this when `enableHttp3` is true).
- For gRPC, see the .NET handler example in Microsoft’s docs; OmniRelay’s `GrpcOutbound` applies the same policy by configuration.

## What causes ALPN mismatch errors?

- The server must advertise `h3` ALPN for QUIC. In OmniRelay, this happens automatically when HTTP/3 is enabled.
- Ensure HTTPS endpoints are configured; HTTP/3 requires TLS.

## How do I verify QUIC traffic at the network layer?

- Linux/macOS
  - List UDP listeners: `ss -u -lpn`
  - Observe QUIC flows: `tcpdump -i any udp port 443 -vv`
- Windows
  - Check excluded UDP port ranges: `netsh interface ipv4 show excludedportrange protocol=udp`
  - Verify listener status in app logs and Windows Event Viewer if MsQuic logs are enabled.

## We see fallbacks from HTTP/3 to HTTP/2—normal?

- Yes, clients may downgrade when alt-svc isn’t present yet, QUIC is blocked, or a middlebox terminates HTTP/3. Track fallback rate and alert when it exceeds your threshold.

## Do WebSockets work over HTTP/3?

- Classic WebSockets remain on HTTP/1.1. OmniRelay keeps duplex HTTP streaming via WebSockets for compatibility. Prefer gRPC duplex streaming or HTTP server/client streams when you want QUIC semantics.

## curl says `--http3` not supported

- Your `curl` build likely lacks HTTP/3 support. Homebrew `curl` typically includes ngtcp2/quiche on macOS. Verify with `curl -V` and look for `HTTP3`.

## gRPC: Which languages support HTTP/3?

- .NET supports gRPC over HTTP/3 today. Other official clients (Go, Java, Python, Node) largely default to HTTP/2 and may treat HTTP/3 as experimental. See `docs/reference/grpc-compatibility.md`.
