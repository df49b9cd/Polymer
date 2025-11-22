# WORK-012 – Telemetry & Health Correlation with Config Epochs

## Goal
Ingest metrics/logs/traces from OmniRelay/agents, correlate them with config epochs/rollouts, and surface SLO/regression signals for operators and rollout gates.

## Scope
- OTLP ingest pipeline with per-tenant rate limits and buffering.
- Tagging of telemetry with node ID, capability set, config epoch/term, rollout stage.
- Health evaluation rules feeding WORK-011 gates and alerts.
- Storage and query paths for dashboards and troubleshooting.

## Requirements
1. **Correlation** – Every record must carry epoch/version; missing data is rejected or downgraded with warning.
2. **Scalability** – Handle fleet-scale ingestion with backpressure; drop policies observable.
3. **Security** – mTLS ingestion; authZ scopes for read/write; PII-safe logging.
4. **Availability** – Degradation does not block data-plane; buffer with bounded loss.

## Deliverables
- Telemetry collector/processor configuration in MeshKit.
- Enrichment layer adding epoch/capability tags.
- Alerts/dashboards for rollout health, control-plane lag, extension faults.

## Acceptance Criteria
- Rollout manager can query health signals by epoch/stage.
- Dashboards show per-epoch error/latency; alerts fire on SLO breach.
- Ingestion protected by mTLS and RBAC; rate limits enforced.

## Testing Strategy
- Load tests for ingest throughput/backpressure.
- Integration: ensure tags propagate from OmniRelay to collector; missing tags handled correctly.

## References
- `docs/architecture/MeshKit.SRS.md`
- `docs/architecture/OmniRelay.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
