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

## Implementation Matrix

### DISC epics

| Story | Status | Notes/Evidence |
| --- | --- | --- |
| DISC-001 - Gossip Host Integration | Implemented | Gossip host, metrics, and diagnostics shipped (`src/OmniRelay/Core/Gossip/MeshGossipHost.cs:1`, `tests/OmniRelay.Core.UnitTests/Gossip/MeshGossipMembershipTableTests.cs:1`). |
| DISC-002 - Leadership Service | Implemented | Leadership coordinator + SSE/gRPC streams and CLI watch command are live (`src/OmniRelay/Core/Leadership/LeadershipCoordinator.cs:1`, `src/OmniRelay.Cli/Program.cs:702`). |
| DISC-003 - Shard Schema & Persistence | Implemented | Shard models + relational stores and hashing strategies added (`src/OmniRelay/Core/Shards/ShardRecord.cs:1`, `src/OmniRelay.ShardStore.Relational/RelationalShardStore.cs:1`). |
| DISC-004 - Shard APIs & Tooling | Not implemented | No `/control/shards` endpoints or shard CLI verbs present (`rg control/shards` under `src` returns none). |
| DISC-005 - Health-Aware Rebalancer | Not implemented | No rebalancer services, jobs, or metrics exist (`rg \"Rebalanc\" src` empty). |
| DISC-006 - Rebalance Observability Package | Not implemented | Rebalancer metrics/dashboards not present because rebalancer is absent. |
| DISC-007 - Registry Read APIs | Not implemented | Registry query endpoints and models are not exposed in control-plane hosts. |
| DISC-008 - Registry Mutation APIs | Not implemented | No registry write endpoints/CLI verbs found. |
| DISC-009 - Security Bootstrap Integration | Implemented | Bootstrap server issues bundles over mTLS with CLI support (`src/OmniRelay/ControlPlane/Bootstrap/BootstrapServer.cs:1`, `src/OmniRelay.Cli/Program.cs:646`). |
| DISC-010 - Join & Certificate Tooling | Implemented | `omnirelay mesh bootstrap issue/join` commands generate and consume join bundles (`src/OmniRelay.Cli/Program.cs:595`, `src/OmniRelay.Cli/Program.cs:631`). |
| DISC-011 - Multi-Cluster Descriptors | Not implemented | No cluster descriptor types/endpoints located. |
| DISC-012 - Cross-Cluster Replication & Failover | Not implemented | Cross-cluster replication/failover services absent. |
| DISC-013 - Transport & Encoding Policy Engine | Partially implemented | Transport security policy enforcement exists for HTTP/gRPC (`src/OmniRelay/Transport/Security/TransportSecurityPolicyEvaluator.cs:1`), but encoding policy/CLI validation and downgrade dashboards are absent. |
| DISC-014 - Sample & Documentation Refresh | Not implemented | No sample/docs refresh tied to this card found. |
| DISC-015 - Operator Dashboards & Alerts | Not implemented | No Grafana dashboards or alert rules delivered for operator views. |
| DISC-016 - OmniRelay Mesh CLI Enhancements | Partially implemented | CLI exposes peers/leaders/upgrade/bootstrap flows (`src/OmniRelay.Cli/Program.cs:445`) but lacks shard/cluster/transport verbs and config validation UX. |
| DISC-017 - Synthetic Health Checks | Partially implemented | Probe/chaos scheduling primitives exist (`src/OmniRelay.Diagnostics.Probes/ProbeSchedulerHostedService.cs:1`) yet no control API probes, alerts, or deployment manifests are wired. |
| DISC-018 - Chaos Test Environments | Partially implemented | Chaos coordinator and experiment hooks defined (`src/OmniRelay.Diagnostics.Probes/ChaosCoordinator.cs:1`), but no packaged environments or operator tooling shipped. |
| DISC-019 - Chaos Automation & Reporting | Not implemented | No automation/reporting layers beyond basic probe snapshots were found. |

### REFDISC subtasks

| Story | Status | Notes/Evidence |
| --- | --- | --- |
| REFDISC-001 - Shared Control-Plane Host Builders | Implemented | HTTP/gRPC control-plane builders integrated with dispatcher lifecycle (`src/OmniRelay/ControlPlane/Hosting/GrpcControlPlaneHostBuilder.cs:1`). |
| REFDISC-002 - Control-Plane Client Factories | Implemented | HTTP/gRPC client factories with profiles live (`src/OmniRelay/ControlPlane/Clients/GrpcControlPlaneClientFactory.cs:1`). |
| REFDISC-003 - Unified TLS & Certificate Management | Implemented | Shared TLS manager handles reload/pinning (`src/OmniRelay/ControlPlane/Security/TransportTlsManager.cs:1`). |
| REFDISC-004 - Shared Interceptor & Middleware Registries | Implemented | Transport interceptor registries power control-plane hosts (`src/OmniRelay/Transport/Grpc/Interceptors/GrpcTransportInterceptorBuilder.cs:1`). |
| REFDISC-005 - Peer Utility Kit for Service Discovery | Implemented | Peer health/lease helpers available (`src/OmniRelay/Core/Peers/PeerLeaseHealthTracker.cs:1`). |
| REFDISC-009 - Diagnostics Runtime & Peer Health Kit | Implemented | Diagnostics runtime + peer health metrics wired (`src/OmniRelay.Diagnostics.Runtime/DiagnosticsRuntimeServiceCollectionExtensions.cs:1`). |
| REFDISC-010 - Unified Telemetry Registration | Implemented | Telemetry registration module centralised (`src/OmniRelay.Diagnostics.Telemetry/OmniRelayTelemetryExtensions.cs:1`). |
| REFDISC-011 - Shared Lifecycle Orchestrator | Implemented | Lifecycle orchestrator coordinates host components (`src/OmniRelay/ControlPlane/Hosting/LifecycleOrchestrator.cs:1`). |
| REFDISC-012 - Configuration Binding & Validation Kit | Implemented | Dispatcher/config binding with validation shipped (`src/OmniRelay.Configuration/ServiceCollectionExtensions.cs:1`). |
| REFDISC-014 - Bootstrap & Join Toolkit | Implemented | Bootstrap token validation and bundle creation exposed (`src/OmniRelay/ControlPlane/Bootstrap/BootstrapServer.cs:1`). |
| REFDISC-015 - Leadership Coordination Primitives | Implemented | Leadership coordinator/leases present (`src/OmniRelay/Core/Leadership/LeadershipCoordinator.cs:1`). |
| REFDISC-016 - Registry Model & Serialization Library | Implemented | Shard model/serialization live (`src/OmniRelay/Core/Shards/ShardRecord.cs:1`). |
| REFDISC-017 - CLI & Diagnostics Client Helpers | Partially implemented | CLI uses shared control-plane client factories (`src/OmniRelay.Cli/CliRuntime.cs:12`), but no standalone helper library or typed wrappers beyond factories. |
| REFDISC-018 - Chaos & Health Probe Infrastructure | Partially implemented | Probe scheduler/chaos hooks exist (`src/OmniRelay.Diagnostics.Probes/ProbeSchedulerHostedService.cs:1`) yet dashboards/alert wiring are absent. |
| REFDISC-019 - Shared State Store & Persistence Adapters | Implemented | Deterministic state stores for filesystem/SQLite/object storage shipped (`src/OmniRelay/Dispatcher/FileSystemDeterministicStateStore.cs:1`). |
| REFDISC-020 - Backpressure & Rate-Limiting Primitives | Implemented | Backpressure-aware limiter integrated (`src/OmniRelay/ControlPlane/Throttling/BackpressureAwareRateLimiter.cs:1`). |
| REFDISC-021 - Resource Lease Replication Framework | Implemented | Replicators for gRPC/sqlite/object storage available (`src/OmniRelay.Dispatcher/ResourceLeaseDeterministic.cs:1`, `src/OmniRelay.ResourceLeaseReplicator.Grpc/GrpcResourceLeaseReplicator.cs:1`). |
| REFDISC-022 - Command Scheduling & Work Queue Abstractions | Implemented | TaskQueue-backed dispatcher component exposed (`src/OmniRelay/Dispatcher/ResourceLeaseDispatcher.cs:1`). |
| REFDISC-023 - Transport Security Policy Enforcement Kit | Implemented | Transport security policy + gRPC interceptor enforced (`src/OmniRelay/Transport/Security/TransportSecurityGrpcInterceptor.cs:1`). |
| REFDISC-024 - Logging Configuration & Enricher Kit | Implemented | Logging extensions/options provided (`src/OmniRelay.Diagnostics.Logging/OmniRelayLoggingExtensions.cs:1`). |
| REFDISC-025 - Alerting & Notification Framework | Implemented | Alert publisher + webhook channel present (`src/OmniRelay.Diagnostics.Alerting/AlertPublisher.cs:1`). |
| REFDISC-026 - Credential & Secret Management Abstractions | Implemented | Secrets providers/audit hooks shipped (`src/OmniRelay/Security/Secrets/CompositeSecretProvider.cs:1`). |
| REFDISC-027 - Policy-Based Authorization Module | Implemented | Mesh authorization evaluator/interceptor present (`src/OmniRelay/Security/Authorization/MeshAuthorizationEvaluator.cs:1`). |
| REFDISC-028 - Schema & Documentation Generation Utilities | Implemented | Documentation/codegen utilities in place (`src/OmniRelay.Diagnostics.Documentation/OmniRelayDocumentationExtensions.cs:1`, `src/OmniRelay.Codegen.Protobuf.Generator/ProtobufIncrementalGenerator.cs:1`). |
| REFDISC-029 - Configuration Reload & Watcher Services | Not implemented | No shared configuration watcher/reload service beyond TLS reload interval helpers. |
| REFDISC-030 - Runtime Bootstrap Harness | Not implemented | No runtime bootstrap harness or starter templates located. |
| REFDISC-031 - Shared Test Harness Toolkit | Implemented | Test support libraries + feature/hyperscale harnesses available (`tests/TestSupport/Support/TestRuntimeConfigurator.cs:1`, `tests/OmniRelay.FeatureTests/Fixtures/FeatureTestMeshOptions.cs:1`). |
| REFDISC-032 - Control-Plane Event Bus | Implemented | Event bus/subscription plumbing added (`src/OmniRelay/ControlPlane/Events/ControlPlaneEventBus.cs:1`). |
| REFDISC-033 - Upgrade & Drain Orchestration Kit | Implemented | Drain coordinator/state tracking wired to CLI upgrade verbs (`src/OmniRelay/ControlPlane/Upgrade/NodeDrainCoordinator.cs:1`, `src/OmniRelay.Cli/Program.cs:530`). |
| REFDISC-034 - AOT Compliance Baseline for Core Runtime | Not implemented | No native AOT baseline/build gate exists for core dispatcher. |
| REFDISC-035 - Control-Plane Library AOT Readiness | Not implemented | No evidence of trimming annotations or AOT validation for control-plane libs. |
| REFDISC-036 - Native AOT Tooling & Packaging | Partially implemented | AOT publish helper script exists (`eng/run-aot-publish.sh:1`), but packaging artifacts/tests are not wired. |
| REFDISC-037 - AOT CI Gating & Runtime Validation | Not implemented | CI gating for native AOT builds not present. |

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



