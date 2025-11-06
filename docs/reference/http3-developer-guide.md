# HTTP/3 Developer Guide

This guide explains how to enable and validate HTTP/3 (QUIC) locally and in staging/production with OmniRelay. It highlights OS prerequisites, OmniRelay configuration, and common troubleshooting steps.

Important references:

- Use HTTP/3 with Kestrel: [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/fundamentals/servers/kestrel/http3)
- Use HTTP/3 with HttpClient: [learn.microsoft.com](https://learn.microsoft.com/dotnet/core/extensions/httpclient-http3)
- QUIC in .NET (platform dependencies): [learn.microsoft.com](https://learn.microsoft.com/dotnet/fundamentals/networking/quic/quic-overview#platform-dependencies)
- gRPC client HTTP/3 configuration: [learn.microsoft.com](https://learn.microsoft.com/aspnet/core/grpc/troubleshoot#configure-grpc-client-to-use-http3)

## 1) Prerequisites by OS

HTTP/3 requires QUIC with TLS 1.3 and a compatible MsQuic runtime.

- Windows
  - Windows 11 or Windows Server 2022 (or later).
  - MsQuic ships with .NET on Windows; no extra installation required.
- Linux
  - Install libmsquic 2.2+ via your package manager (examples):
    - apt: `sudo apt-get install libmsquic`
    - dnf: `sudo dnf install libmsquic`
    - yum: `sudo yum install libmsquic`
    - zypper: `sudo zypper install libmsquic`
    - Alpine: `apk add libmsquic` (edge/community may be required on older releases)
  - Ensure OpenSSL 3.x (or distro default) and libnuma are present if required by the package.
- macOS (local development only)
  - Partial/limited support; not part of Microsoft’s official testing matrix.
  - Install MsQuic via Homebrew: `brew install libmsquic`.
  - Set the dynamic library path before running your app:
    - zsh: `export DYLD_FALLBACK_LIBRARY_PATH=$DYLD_FALLBACK_LIBRARY_PATH:$(brew --prefix)/lib`
  - Browsers generally won’t allow HTTP/3 with self-signed dev certs. Use `HttpClient`, CLI, or a trusted cert for browser testing.

Certificates

- HTTP/3 requires HTTPS and TLS 1.3-capable certificates. Use a certificate with a private key and modern cipher suites (TLS_AES_*, TLS_CHACHA20_*).
- Browsers do not accept self-signed certificates for HTTP/3 upgrades; use a locally trusted CA or limit to CLI/`HttpClient` testing.

Network

- Ensure UDP/443 (and any custom HTTPS ports) is allowed wherever TCP/443 is allowed.
- Middleboxes must pass through QUIC/UDP and preserve `Alt-Svc` headers.

## 2) Enabling HTTP/3 on OmniRelay inbounds (HTTP + gRPC)

OmniRelay exposes an explicit feature flag. Add `runtime.enableHttp3: true` per listener, configure TLS, and optionally tune MsQuic options.

Example (HTTP inbound):

```jsonc
{
  "polymer": {
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
  }
}
```

Example (gRPC inbound):

```jsonc
{
  "polymer": {
    "inbounds": {
      "grpc": [
        {
          "urls": [ "https://0.0.0.0:9091" ],
          "runtime": {
            "enableHttp3": true,
            "keepAlivePingDelay": "00:01:00",
            "keepAlivePingTimeout": "00:00:20",
            "http3": {
              "idleTimeout": "00:02:00",
              "maxBidirectionalStreams": 256
            }
          },
          "tls": {
            "certificatePath": "certs/server.pfx",
            "certificatePassword": "change-me"
          }
        }
      ]
    }
  }
}
```

Notes

- OmniRelay sets `HttpProtocols.Http1AndHttp2AndHttp3` when enabled, and validates TLS 1.3 at startup.
- `http3.enableAltSvc` controls advertisement; leave it on unless an upstream device injects its own `Alt-Svc`.
- If MsQuic options can’t be applied on the host, OmniRelay warns at startup but continues with platform defaults.

## 3) Enabling HTTP/3 for clients (outbounds)

gRPC outbound (GrpcOutbound):

```jsonc
{
  "polymer": {
    "outbounds": {
      "ledger": {
        "unary": {
          "grpc": [
            {
              "addresses": [ "https://ledger.internal:9091" ],
              "remoteService": "ledger",
              "runtime": {
                "enableHttp3": true,
                "requestVersion": "3.0",
                "versionPolicy": "request-version-or-higher",
                "keepAlivePingDelay": "00:00:45",
                "keepAlivePingTimeout": "00:00:10"
              }
            }
          ]
        }
      }
    }
  }
}
```

HTTP outbound (HttpOutbound):

```jsonc
{
  "polymer": {
    "outbounds": {
      "audit": {
        "oneway": {
          "http": [
            {
              "url": "https://audit.internal:8443/yarpc/v1/audit::record",
              "runtime": {
                "enableHttp3": true,
                "requestVersion": "3.0",
                "versionPolicy": "request-version-or-higher"
              }
            }
          ]
        }
      }
    }
  }
}
```

Notes

- Use `request-version-or-higher` so calls negotiate down to HTTP/2/1.1 when QUIC isn’t available.
- Outbound HTTPS is required when `enableHttp3` is true; the configuration binder rejects non-HTTPS URIs.

## 4) Local testing workflows

- CLI
  - HTTP: `omnirelay bench http --url https://127.0.0.1:8443 --procedure echo::ping --http3`
  - gRPC: `omnirelay bench grpc --address https://127.0.0.1:9091 --service echo --procedure Ping --grpc-http3`
- HttpClient
  - Ensure requests set `Version=3.0` and `VersionPolicy=RequestVersionOrHigher` if you write direct client code.
- curl
  - `curl --http3` requires a build with HTTP/3 (ngtcp2/quiche). Homebrew `curl` on macOS typically supports this.
  - Browsers won’t upgrade to HTTP/3 with self-signed certs; prefer CLI or trusted certs.

## 5) Staging/Production checklist

- Networking
  - Open UDP/443 alongside TCP/443; confirm security groups and firewalls.
  - Verify load balancers/ingress/CDNs pass QUIC end-to-end or explicitly terminate HTTP/3 at the edge.
- TLS
  - Deploy TLS 1.3-capable certs and ciphers; rotate legacy certs.
  - Validate SNI and ALPN policies don’t block `h3`.
- Observability
  - Track per-protocol request counts, handshake RTT, fallback rates.
  - Alert when fallback from HTTP/3 to HTTP/2/1.1 exceeds a threshold.
- Rollout
  - Canary with `alt-svc` verification and UDP reachability checks.
  - Maintain a quick rollback switch: set `runtime.enableHttp3=false` on listeners and outbounds.

## 6) Troubleshooting quick reference

- No upgrade to HTTP/3 (stuck on HTTP/2)
  - Check `Alt-Svc` present on responses; ensure upstream proxy does not strip it.
  - Confirm UDP/443 open and not blocked by firewalls or NAT.
  - Validate TLS 1.3 and modern ciphers on the server certificate.
- Handshake failures / QUIC disabled
  - Windows: ensure Server 2022/Windows 11+.
  - Linux: `libmsquic` 2.2+ installed; confirm with `ldd`/`apk info`/`dpkg -l`.
  - macOS: `brew install libmsquic` and set `DYLD_FALLBACK_LIBRARY_PATH` before running.
- ALPN mismatch
  - Server must advertise `h3` ALPN; OmniRelay sets this automatically when HTTP/3 is enabled.
  - For clients, prefer `RequestVersionOrHigher` and `Version=3.0` to avoid exact-version failures.
- Port reuse / excluded ranges (Windows)
  - Use `netsh interface ipv4 show excludedportrange protocol=udp` to inspect conflicts.
- Basic packet checks (Linux/macOS)
  - `ss -u -lpn` to list UDP listeners; `tcpdump -i any udp port 443 -vv` to observe QUIC traffic.

For deeper triage, see `docs/reference/quic-observability.md` and the HTTP transport/gRPC troubleshooting docs linked above.
