# REFDISC-005 - Peer Utility Kit for Service Discovery

## Goal
Break out the dispatcher’s peer chooser, circuit breaker, and endpoint capability tracking into reusable utilities so service-discovery control-plane features can share peer balancing logic with the data plane.

## Scope
- Extract `IPeerChooser`, `RoundRobinPeerChooser`, preferred peer routing, and circuit-breaker logic from `GrpcOutbound`.
- Provide neutral abstractions for peer metadata (endpoint URI, HTTP/3 support, health state) that can be sourced from gossip/leadership.
- Add helpers to translate gossip/leadership snapshots into peer lists consumable by both dispatcher outbounds and control-plane RPC clients.
- Document integration patterns so service discovery can plug peer utilities into the HTTP/3 client factory (REFDISC-002).

## Requirements
1. **Shared models** - Define peer descriptors that capture endpoint URL, role, protocol hints, and health status without referencing dispatcher-specific registries.
2. **Chooser parity** - All chooser strategies (round-robin, power-of-two choices, preferred peer fallback) available to dispatcher must be exposed to control-plane consumers.
3. **Circuit breaker hooks** - Provide reusable breaker implementation with configurable thresholds/timeouts, plus integration with telemetry counters.
4. **Health feedback** - Support injecting RTT/failure metrics from control-plane calls back into the peer chooser to influence selection.
5. **Thread safety** - Utilities must remain lock-free or minimal-lock just like current `GrpcOutbound` implementations to avoid contention under hyperscale loads.

## Deliverables
- Peer utility package with choosers, circuit breakers, and capability tracking.
- Refactoring of `GrpcOutbound` to consume the shared utilities.
- Updates to service-discovery agents to use the utilities when selecting peers for gossip, leadership elections, and discovery RPCs.
- Documentation describing how to map gossip membership snapshots into peer descriptors and configure chooser strategies.

## Acceptance Criteria
- Dispatcher behavior for peer selection and circuit breaking is unchanged after adopting the utilities.
- Gossip/leadership components reuse the same choosers and can observe consistent load distribution across peers.
- Circuit breaker state transitions (open/half-open/closed) emit shared telemetry and influence both data-plane and control-plane traffic.
- RTT/failure metrics fed from control-plane calls adjust peer preference without manual intervention.
- Utilities introduce no additional dependencies on dispatcher or other heavy runtime components.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Cover chooser selection sequences under varying peer counts and health states to ensure deterministic balancing.
- Validate circuit breaker transitions given sequences of successes/failures and configurable thresholds.
- Test peer capability hint updates (HTTP/3 supported/unsupported) and ensure selection honors the hints.

### Integration tests
- Plug utilities into a sample control-plane client and verify peer rotation + breaker recovery when endpoints fail.
- Feed gossip membership updates through the utilities and ensure peer sets refresh without memory leaks or deadlocks.
- Confirm telemetry counters for peer selection and breaker state align with dispatcher metrics.

### Feature tests
- Within OmniRelay.FeatureTests, instrument both dispatcher outbounds and control-plane clients with the shared utilities, then simulate peer failures to observe identical fallback behavior.
- Validate operator workflows (e.g., draining a peer) by ensuring both data-plane and control-plane traffic honor new peer statuses simultaneously.

### Hyperscale Feature Tests
- In OmniRelay.HyperscaleFeatureTests, scale to hundreds of peers, inject rolling degradations, and ensure chooser logic maintains even distribution and breaker stability.
- Measure RTT feedback loops to confirm high-latency peers are deprioritized for both control-plane and data-plane traffic without oscillation.

## Implementation status
- Shared peer models, choosers, breakers, and health trackers now live in `src/OmniRelay/Core/Peers/`. `PeerListCoordinator`, `PeerLeaseHealthTracker`, and the chooser implementations (`RoundRobinPeerChooser`, `TwoRandomPeerChooser`, `FewestPendingPeerChooser`) expose dispatcher-agnostic abstractions backed by unit tests so both the data-plane and service-discovery stacks consume the same logic.
- `src/OmniRelay/Transport/Grpc/GrpcOutbound.cs` wraps each peer inside a `PeerCircuitBreaker` and resolves choosers through the new `IPeerChooser` factory so balancing, preferred-peer routing, and circuit breaking match the dispatcher baseline even when control-plane agents dial peers via the shared transport factories.
- Gossip, diagnostics, and leadership services wire the same utilities: `MeshGossipHost` feeds membership + health updates into `PeerLeaseHealthTracker` (`src/OmniRelay/Core/Gossip/MeshGossipHost.cs`), and the diagnostics control-plane host renders `/control/peers` / `/omnirelay/control/lease-health` straight from `PeerLeaseHealthDiagnostics` (`src/OmniRelay/Core/Diagnostics/DiagnosticsControlPlaneHost.cs`), ensuring operators and CLI tooling observe the same peer state as the dispatcher.

## References
- `src/OmniRelay/Transport/Grpc/GrpcOutbound.cs` - Source of current peer chooser/breaker logic.
- `src/OmniRelay/Core/Gossip/` - Membership metadata feeding peer selection.
- `docs/architecture/service-discovery.md` - Service discovery + peer health requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
