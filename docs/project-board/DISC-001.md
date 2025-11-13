# DISC-001 – Gossip Host Integration

## Goal
Embed a reusable gossip subsystem into every OmniRelay host (dispatcher, gateway, background worker) so membership, health, and metadata propagate automatically and underpin leader elections.

## Scope
- Implement a memberlist/Ringpop-style agent with configurable fanout, suspicion timers, retransmit limits, and metadata payloads (`nodeId`, `role`, `clusterId`, `region`, `meshVersion`, `http3Support`).
- Wire the agent into existing host builders so it starts/stops with the application and shares lifecycle logging.
- Expose health metrics (`mesh_gossip_members`, `mesh_gossip_rtt_ms`, `mesh_gossip_messages_total`) plus structured logs for join/leave events.
- Provide configuration docs + samples for dev/prod (ports, TLS, firewall rules).

## Requirements
1. **Transport/TLS** – Gossip traffic must run over mTLS using the same certificates issued by the mesh bootstrap tooling; certificate rotation cannot drop more than one gossip interval.
2. **Metadata schema** – Define a versioned JSON/Protobuf schema for gossip payloads and validate on receive; unknown fields must be ignored for forward compatibility.
3. **Configuration knobs** – Expose settings for fanout, suspicion interval, ping timeout, retransmit limit, and metadata refresh period via appsettings + environment variables.
4. **Instrumentation** – Publish Prometheus metrics and OpenTelemetry logs/traces capturing membership counts, RTT, and failures. Add alert recommendations (e.g., high suspicion rate).
5. **Diagnostics** – `/control/peers` must surface gossip-derived information (status, lastSeen) even before the registry service is wired up.

## Deliverables
- Gossip host component (library) with integration hooks for existing host builders.
- Configuration schema + defaults documented under `docs/architecture/service-discovery.md`.
- Unit/integration tests simulating join/leave, partition, and metadata upgrades.
- Sample config (dev + prod) in `samples/ResourceLease.MeshDemo` illustrating usage.

## Acceptance Criteria
- Bringing up three demo nodes shows mutual discovery within 2 seconds (verified via logs/metrics).
- Killing a node removes it from membership/metrics within the configured suspicion timeout.
- Configuration can be toggled without code changes (appsettings/environment).
- Security audit confirms gossip traffic uses mTLS and honors certificate revocation.

## Testing Strategy

### Unit tests
- Cover the SWIM timer wheel, suspicion counters, and retransmit limiter logic with deterministic time providers so join/suspect/leave transitions always match the configured fanout and interval settings.
- Validate metadata schema handling by feeding mixed-version payloads and asserting unknown fields are ignored while required fields trigger actionable validation errors.
- Exercise TLS certificate reload paths to ensure the host swaps credentials without dropping packets or leaking sockets.

### Integration tests
- Spin up three in-memory hosts with real gossip sockets to verify mutual discovery, metadata propagation (`http3Support`, `meshVersion`), and `/control/peers` surfacing gossip-derived state before the registry is available.
- Add failure-injection tests that kill or partition nodes to confirm suspicion timers, metrics (`mesh_gossip_*`), and structured logs converge within the acceptance thresholds.
- Run certificate rotation scenarios against the bootstrap service to ensure overlapping validity windows keep gossip traffic encrypted end to end.

### Feature tests

#### OmniRelay.FeatureTests
- Use the OmniRelay.FeatureTests harness to boot dispatcher, gateway, and worker roles with gossip enabled, then assert `omnirelay mesh peers` output, `/control/peers`, and dashboard metrics converge on the same membership view within 2 seconds.
- Execute an operator workflow (drain/cordon) entirely inside the feature harness, verifying gossip status flags, CLI output, and alert hooks reflect each lifecycle transition in real time.

#### OmniRelay.HyperscaleFeatureTests
- Spin up large node sets (tens to low hundreds) under the OmniRelay.HyperscaleFeatureTests suite, inject rolling deployments and packet loss, and confirm the gossip mesh maintains quorum, RTT budgets, and fast convergence.
- Run hyperscale certificate rotation and cross-region latency drills to ensure metadata propagation, suspicion timers, and metric/alert noise stay within defined thresholds even at elevated fanout.

## References
- `docs/architecture/service-discovery.md` – Sections “Membership gossip layer + leader elections” and “Transport & encoding strategy”.

## Implementation status (mesh gossip v1)

- **Code** – `MeshGossipHost` + option types live under `src/OmniRelay/Core/Gossip/` and are wired into every dispatcher via `AddMeshGossipAgent` (see `src/OmniRelay.Configuration`). The host emits structured join/leave/suspect logs, reloads mTLS certificates without downtime, and pushes metadata into `PeerLeaseHealthTracker`.
- **Instrumentation** – Prometheus meters `mesh_gossip_members`, `mesh_gossip_rtt_ms`, and `mesh_gossip_messages_total` are produced by the `OmniRelay.Core.Gossip` meter and enabled automatically inside `AddOmniRelayDispatcher`. Sample dashboards pick them up through the Prometheus scrape on `/metrics`.
- **Diagnostics** – `/control/peers` comes from the gossip snapshot and is exposed by every HTTP inbound regardless of whether the logging/toggle runtime is enabled. `/omnirelay/control/lease-health` now reflects gossip metadata, so operators can compare both views before the registry ships.
- **Configuration** – `mesh:gossip:*` settings support env variables, `appsettings.*`, and the sample dev/prod configs under `samples/ResourceLease.MeshDemo/`. Production templates document TLS paths, seed lists, fanout tuning, and certificate pinning.
- **Samples/tests** – The ResourceLease mesh demo enables gossip by default in `appsettings.Development.json` and demonstrates TLS/seed wiring in `appsettings.Production.json`. `MeshGossipMembershipTableTests` (in `tests/OmniRelay.Core.UnitTests/Gossip`) exercise join/metadata upgrades plus suspect/left transitions to guard the SWIM timers.
