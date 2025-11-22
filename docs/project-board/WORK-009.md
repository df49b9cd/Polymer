# WORK-009 – Bootstrap & Watch Harness (Validation, Backoff, Resume)

## Goal
Provide reusable startup and watch harnesses for OmniRelay and MeshKit roles to initialize config, validate compatibility, and maintain control streams with backoff/resume semantics.

## Scope
- Boot pipeline: load LKG, validate signatures/capabilities, request latest snapshot, apply deltas.
- Watch lifecycle: backoff strategy, resume tokens, health state machine, metrics.
- Validation hooks: schema validation, capability checks, extension/package signature verification before activation.
- Shared logging/telemetry setup and feature-flag plumbing.

## Requirements
1. **Deterministic startup** – Clear order: load LKG → validate → connect → stage → activate; failures leave system safe.
2. **Visibility** – Expose state (Connecting/Staging/Active/Degraded) via admin endpoints and metrics.
3. **Resilience** – Resume after disconnect without missing epochs; LKG used only when signatures/epochs match expectations.
4. **Reuse** – Harness consumed by all hosts/roles to avoid drift.

## Deliverables
- Shared harness library + host wiring for OmniRelay (all modes) and MeshKit (central/agent/bridge).
- Metrics/logging for state transitions, backoff, validation failures.
- Docs describing bootstrap flow and troubleshooting.

## Acceptance Criteria
- All hosts start via the harness; startup sequence logged and observable.
- Resume/backoff tested with forced disconnects; no missed deltas.
- Validation failures stop activation and surface actionable errors.

## Testing Strategy
- Integration: startup with good/bad configs, forced disconnect/reconnect, corrupted LKG.
- Feature: validation failure paths and observability signals.

## References
- `docs/architecture/OmniRelay.SRS.md`
- `docs/architecture/MeshKit.SRS.md`

## Status
Needs re-scope (post-BRD alignment).
