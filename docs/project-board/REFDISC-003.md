# REFDISC-003 - Unified TLS & Certificate Management

## Goal
Expose the dispatcher’s TLS option types, certificate loaders, and policy enforcement as standalone services so every control-plane component (gossip, leadership, discovery) consumes the same mTLS configuration and rotation flow.

## Scope
- Promote `GrpcClientTlsOptions`, `GrpcServerTlsOptions`, and certificate provider logic into a shared `TransportTlsManager`.
- Provide DI registrations that source settings from configuration (`mesh:gossip:tls`, dispatcher TLS, etc.) and hydrate both client/server consumers.
- Ensure callbacks for certificate validation, revocation checks, and thumbprint allowlists are centralized and reusable.
- Publish guidance on how control-plane services reference the shared TLS manager instead of implementing their own `MeshGossipCertificateProvider`.

## Requirements
1. **Single source of truth** - TLS cert loading, reload intervals, and disposal must live in one component to eliminate drift between gossip and dispatcher stacks.
2. **mTLS everywhere** - The manager must support both server-side requirements (client cert mandatory) and client-side auth (supply local cert when dialing peers).
3. **Rotation safety** - Reloading certificates must be atomic, thread-safe, and logged; watchers need to observe file timestamps and inline data with overrides.
4. **Policy hooks** - Allow injection of custom validation (e.g., thumbprint pinning, CN/SAN checks) without duplicating code per service.
5. **Configuration parity** - Support all existing configuration knobs from gossip and dispatcher TLS blocks so no appsettings keys are lost.

## Deliverables
- Shared TLS manager implementation + options objects in `OmniRelay.Transport.Grpc` (or `OmniRelay.Core.Security`).
- Deprecation of bespoke providers such as `MeshGossipCertificateProvider` in favor of the shared service.
- Wiring across dispatcher, gossip, and leadership hosts to consume the manager for both HttpClient handlers and Kestrel HTTPS options.
- Documentation describing configuration keys, rotation expectations, and migration guidance.

## Acceptance Criteria
- All control-plane services obtain certificates exclusively through the shared manager.
- Certificate rotations (file change, inline data swap) propagate to both server + client stacks without restarts.
- TLS validation failures emit consistent log events regardless of which component triggered them.
- Configuration schemas (`mesh:gossip:tls`, dispatcher TLS) continue to work and are validated by options binding.
- Security review confirms mTLS and revocation enforcement match previous behavior.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Cover certificate loading from file path vs. inline base64 data, including password-protected bundles.
- Exercise reload triggers (time-based + file timestamp) to ensure concurrency, logging, and disposal behave.
- Validate validation callbacks: allowed thumbprints, custom validation delegate success/failure, revocation mode toggles.

### Integration tests
- Run end-to-end control-plane calls using certificates loaded through the manager; rotate cert files and ensure both client/server adopt the new certificate without dropping calls.
- Intentionally corrupt certificates or passwords and verify consumers log actionable errors without crashing.
- Combine multiple consumers (dispatcher + gossip) in one host to confirm the manager can supply independent client/server instances simultaneously.

### Feature tests
- Use OmniRelay.FeatureTests to configure TLS via appsettings/environment variables and ensure both dispatcher RPC and control-plane gossip share the same credentials.
- Trigger certificate reload scenarios within the feature harness (e.g., update secret volume) and confirm logs/metrics show single reload events.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, perform rolling certificate rotations across dozens of nodes, ensuring the shared manager never presents stale certs and that control-plane traffic stays encrypted.
- Combine chaos injections (revocation toggled, thumbprint mismatch) to verify consistent enforcement across all consumers.

## Implementation status
- `TransportTlsManager` + `TransportTlsOptions` live under `OmniRelay.ControlPlane.Security` and have replaced the bespoke gossip certificate provider. `MeshGossipHost`, `DiagnosticsControlPlaneHost`, and `LeadershipControlPlaneHost` all read the same `transportTls` configuration and reload certificates in lockstep, so HTTP/gRPC control-plane sockets now inherit the same rotation semantics and revocation policies as the dispatcher's data-plane inbounds.

## References
- `src/OmniRelay/Transport/Grpc/GrpcInbound.cs` / `GrpcOutbound.cs` - Current TLS option usage.
- `src/OmniRelay/Core/Gossip/MeshGossipCertificateProvider.cs` - Logic to consolidate.
- `docs/architecture/service-discovery.md` - TLS/mTLS requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
