# Project Board Overview

Use this page to understand sequencing constraints across DISC epics and to quickly see which workstreams can execute in parallel.

## Dependency Backbone

- **Control-plane foundation** - `DISC-001` (Gossip) + `DISC-002` (Leadership) must establish stable membership, health signaling, and elections before registry, routing metadata, or multi-cluster work begins.
- **Registry data flow** - `DISC-003` (Shard schema) is prerequisite for `DISC-004` (Shard APIs), `DISC-005` (Rebalancer), `DISC-006` (Observability), `DISC-007`/`DISC-008` (Registry APIs), and `DISC-013` (Transport policy checks).
- **Security bootstrap** - `DISC-009` (Bootstrap) unlocks `DISC-010` (Join tooling), `DISC-013` (Transport governance), CLI work (`DISC-016`), and any environment automation requiring mTLS issuance.
- **Multi-cluster** - `DISC-011` (Cluster descriptors) feeds `DISC-012` (Replication/failover) after leadership is stable.
- **Observability consumers** - `DISC-015` (Dashboards) and `DISC-017` (Synthetic checks) rely on telemetry emitted by `DISC-002/004/005/012`.

## Concurrency Lanes

| Lane | Focus | Stories | Hard prerequisites | Notes on concurrency |
| --- | --- | --- | --- | --- |
| L0 | Control-plane heartbeat | `DISC-001`, `DISC-002` | — | Must be online before any other lane; both can progress together because shared gossip + leadership plumbing co-evolves. |
| L1 | Data persistence & security bootstrap | `DISC-003`, `DISC-009` | L0 | Schema/persistence and security bootstrap can be split across squads once gossip/leadership show steady health. |
| L2 | Registry services & transport policy | `DISC-004`, `DISC-005`, `DISC-006`, `DISC-013` | L1 | `DISC-004/005/013` share contracts and should co-plan; `DISC-006` starts as soon as rebalancer metrics exist and can trail by a sprint. |
| L3 | Access APIs, join tooling, CLI | `DISC-007`, `DISC-008`, `DISC-010`, `DISC-016` | L2 for registry APIs, L1 for security | Registry read APIs (`DISC-007`) light up first, write APIs (`DISC-008`) trail stabilization; join tooling (`DISC-010`) only needs `DISC-009`; CLI tracks whichever APIs it wraps. |
| L4 | Multi-cluster routing & failover | `DISC-011`, `DISC-012` | L0 + L2 matured | Cluster descriptors (`DISC-011`) must harden before replication/failover; lane can run alongside L3 once leadership/registry metadata is stable. |
| L5 | Operator experience & docs | `DISC-014`, `DISC-015` | L2 + L3 telemetry available | Samples/docs refresh (`DISC-014`) and dashboards (`DISC-015`) iterate together as soon as upstream APIs/metrics stabilize. |
| L6 | Reliability, chaos, synthetic health | `DISC-017`, `DISC-018`, `DISC-019` | L0-L3 minimum viable control plane | Synthetic checks and chaos environments can start together; automation/reporting (`DISC-019`) follows once chaos environments exist. |

Lanes L1-L6 can progress concurrently provided their prerequisites remain healthy. Use them to assign squads or PI objectives without re-deriving dependencies from each card.

## Epic Structure & Subtasks

- **E1 - Service Discovery & Transport (DISC-001)**: `REFDISC-001`, `REFDISC-002`, `REFDISC-003`, `REFDISC-004`, `REFDISC-005`, `REFDISC-020`, `REFDISC-032`.
- **E2 - Leadership & Coordination (DISC-002)**: `REFDISC-015`, `REFDISC-011`, `REFDISC-033`.
- **E3 - Registry & Persistence (DISC-003-DISC-008)**: `REFDISC-016`, `REFDISC-019`, `REFDISC-021`, `REFDISC-022`, `REFDISC-031`.
- **E4 - Security & Bootstrap (DISC-009-DISC-013)**: `REFDISC-003`, `REFDISC-009`, `REFDISC-014`, `REFDISC-023`, `REFDISC-026`, `REFDISC-025`, `REFDISC-027`.
- **E5 - Diagnostics & Control Plane UX (DISC-014-DISC-018)**: `REFDISC-004`, `REFDISC-009`, `REFDISC-010`, `REFDISC-024`, `REFDISC-027`, `REFDISC-028`, `REFDISC-017`, `REFDISC-018`.
- **E6 - Chaos & Reliability (DISC-017-DISC-019)**: `REFDISC-017`, `REFDISC-018`, `REFDISC-020`, `REFDISC-021`.
- **E7 - Platform, Tooling & AOT (cross-cutting)**: `REFDISC-029`, `REFDISC-030`, `REFDISC-034`, `REFDISC-035`, `REFDISC-036`, `REFDISC-037`.

Use these epics to map REFDISC subtasks; child cards should not close until their parent DISC acceptance criteria are met.

## Control-Plane Host Configuration

The dispatcher now lights up dedicated diagnostics + leadership services whenever the `diagnostics.controlPlane` block is configured. Both the HTTP and gRPC hosts are built through the shared `HttpControlPlaneHostBuilder`/`GrpcControlPlaneHostBuilder`, pick up the transport TLS manager, and register with the dispatcher lifecycle so they start/stop next to the data plane.

```json
"diagnostics": {
  "runtime": {
    "enableControlPlane": true,
    "enableLoggingLevelToggle": true,
    "enableTraceSamplingToggle": true
  },
  "controlPlane": {
    "httpUrls": [ "https://0.0.0.0:8080" ],
    "grpcUrls": [ "https://0.0.0.0:17421" ],
    "httpRuntime": { "enableHttp3": true },
    "grpcRuntime": { "enableHttp3": true },
    "tls": {
      "certificatePath": "/var/certs/omnirelay-control-plane.pfx",
      "certificatePassword": "change-me",
      "reloadInterval": "00:05:00",
      "allowedThumbprints": [ "‎AB12CD34EF56..." ]
    }
  }
}
```

The shared `TransportTlsManager` hydrates both hosts from the `tls` section (or the protocol-specific `httpTls`/`grpcTls` overrides) so certificate rotation and revocation checks stay in sync. Teams onboarding DISC-001 cards can drop this block into their dispatcher configuration and immediately inherit the `/omnirelay/control/*` HTTP endpoints plus the gRPC leadership stream without creating ad-hoc ASP.NET hosts.

## AOT-First Requirement

OmniRelay is cloud-native and hyperscale-focused, so **every DISC/REFDISC story carries an explicit AOT gate**:
- Native AOT publishes (`dotnet publish /p:PublishAot=true`) must succeed with trimming warnings treated as errors for the assemblies touched by the story.
- Unit/integration/feature/hyperscale tests must run against the native artifacts at least once before closure.
- CI enforcement lives in `REFDISC-034`-`REFDISC-037`; reference those cards in your deliverables.

Stories lacking the “Native AOT gate” acceptance bullet or the “All test tiers must run against native AOT artifacts” note should be updated before work begins.



