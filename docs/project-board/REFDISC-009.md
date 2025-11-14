# REFDISC-009 - Diagnostics Runtime & Peer Health Kit

## Goal
Extract the dispatcher's diagnostics runtime (logging/tracing toggles, runtime state), `/omnirelay/control/*` handlers, and peer health/lease tracking into a reusable kit so any host can expose control knobs and publish peer health data without referencing dispatcher internals.

## Scope
- Move `DiagnosticsRuntimeState`, `IDiagnosticsRuntime`, control endpoint handlers, and options binding into a neutral package.
- Lift `PeerLeaseHealthTracker`, snapshot providers, and diagnostics transformers into the same kit with DI registrations.
- Provide helper extensions to register control endpoints plus peer health endpoints on any ASP.NET Core app and ensure multiple hosts in one process can share state safely.
- Integrate with the shared telemetry module so peer health events emit consistent metrics/spans.
- Document how dispatcher, gossip, leadership, and bootstrap services consume the kit.

## Requirements
1. **Thread-safe runtime state** - Diagnostics and peer health state must support concurrent updates, propagate change notifications, and deduplicate registrations across hosts.
2. **Endpoint consistency** - `/omnirelay/control/logging`, `/tracing`, `/lease-health`, `/peers`, and related endpoints must return identical payloads regardless of host and honor runtime toggle integration.
3. **Shared data model** - Peer health data requires a common representation (status, labels, metadata, lease info) consumed by telemetry and diagnostics endpoints.
4. **Authorization & configuration** - Allow custom auth policies (mTLS identity, bearer tokens) plus configuration binding (`diagnostics.runtime.*`) to enable/disable endpoints per host.
5. **Extensibility** - Provide hooks for new diagnostics/health modules to register endpoints, telemetry, and snapshot providers using the same serialization/error-handling patterns.

## Deliverables
- Diagnostics runtime + peer health kit (interfaces, state implementation, endpoint registration helpers, trackers).
- Dispatcher refactor to consume the kit rather than private implementations.
- Updates to control-plane hosts to record/report peer health and register diagnostics endpoints via the kit.
- Documentation describing configuration, security, data model conventions, and extension points.

## Acceptance Criteria
- Control endpoints return the same JSON payloads before/after dispatcher refactor and react to runtime toggles immediately.
- Control-plane hosts can enable logging/tracing toggles and peer health diagnostics independently by referencing the kit.
- Peer health diagnostics (`/omnirelay/control/lease-health`, `/peers`) function even when the dispatcher is offline as long as the kit is registered.
- Authorization policies attach uniformly to all endpoints, and configuration toggles work across all consumers.
- Metrics emitted by peer health trackers align with previous counters (counts per status, event rates) and feed existing dashboards.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Verify runtime state updates (set log level, set sampling probability) propagate to subscribers, validate payloads, and guard against invalid ranges.
- Test peer health tracker concurrency, metadata enrichment, and snapshot generation, ensuring snapshots remain consistent under concurrent updates.
- Exercise endpoint handlers for null/invalid requests to ensure consistent HTTP status codes and auth enforcement.

### Integration tests
- Host a test app using the kit, issue control requests (GET/POST), record peer health events, and confirm state changes plus JSON payloads.
- Toggle configuration to disable specific endpoints and verify they return 404 or are not mapped; repeat with multiple hosts in one process to ensure registrations deduplicate.
- Confirm telemetry counters/spans emit when peer health transitions occur and when diagnostics toggles fire.

### Feature tests
- Within OmniRelay.FeatureTests, enable diagnostics endpoints on dispatcher and gossip hosts via the kit, then drive logging/tracing toggles and inspect peer health results to ensure parity.
- Simulate node failures/lease churn, ensuring peer health diagnostics update promptly regardless of host origin.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, stress control endpoints with concurrent toggles (log level thrash) and high peer health event volume to ensure latency/perf remain acceptable.
- Exercise RBAC/mTLS auth requirements at scale to ensure authorization hooks remain reliable.

## References
- `src/OmniRelay.Configuration/ServiceCollectionExtensions.cs` and `src/OmniRelay/Core/Peers/PeerLeaseHealthTracker.cs` - Source logic to extract.
- `docs/architecture/service-discovery.md` - Control-plane diagnostics and peer health requirements.
- REFDISC-034..037 - AOT readiness baseline and CI gating.


