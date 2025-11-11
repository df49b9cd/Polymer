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

## References
- `docs/architecture/service-discovery.md` – Sections “Membership gossip layer + leader elections” and “Transport & encoding strategy”.
