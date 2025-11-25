# Control Protocol (Work-006)

## Schemas & versioning (006A)
- Protobuf: `src/OmniRelay.Protos/Protos/control_plane.proto`.
- Key types:
  - `CapabilitySet { items[], build_epoch }`
  - `WatchResumeToken { version, epoch, opaque }`
  - `ControlWatchRequest { node_id, capabilities, resume_token }`
  - `ControlWatchResponse { version, epoch, payload, full_snapshot, resume_token, backoff, error, required_capabilities[] }`
  - `ControlSnapshotRequest/Response` mirror watch (no resume).
- Versioning policy:
  - **Semantic wire versions** carried in `version` and `epoch`. Increment epoch on breaking schema changes; version for payload changes within an epoch.
  - **Backward-compatible additions**: only add optional/nullable fields; do not repurpose numeric tags.
  - **Deprecation window**: keep fields alive for ≥2 minor versions; document removal in release notes.
  - **Capability gating**: features behind capability strings (e.g., `core/v1`, `dsl/v1`). Servers never emit payloads requiring capabilities the client did not advertise.

## Watch streams with resume/backoff (006B)
- Server (ControlPlaneWatchService in `src/OmniRelay.ControlPlane/Core/ControlProtocol`):
  - Validates capabilities via `ControlProtocolOptions.SupportedCapabilities`; on mismatch returns a `ControlWatchResponse` carrying `error.code=control.unsupported_capability` and `backoff.millis=5000` (configurable).
  - Publishes updates from `ControlPlaneUpdateStream` (implementations can push via `IControlPlaneUpdatePublisher`).
  - Generates resume tokens `{ version, epoch, opaque=node_id|resume_opaque }` and returns a full snapshot when the resume token does not match the current version/epoch.
  - Emits default backoff hints from options (1 s by default).
  - Error responses now echo `required_capabilities` derived from the rejection metadata so agents can surface missing flags without guessing.
- Client (WatchHarness in `src/OmniRelay.ControlPlane/Core/Agent`):
  - Applies LKG cache on startup and reuses persisted `resume_token`.
  - LKG cache now carries hash + HMAC signature; corrupt/unsigned caches are ignored and control watch proceeds.
  - On errors, logs and respects server-provided backoff (if present) or falls back to exponential 1 s → 2 s … capped at 30 s.
  - Saves version/epoch/payload/resume_token after each successful apply using `LkgCache.SaveAsync`.

## Capability negotiation (006C)
- Client advertises `CapabilitySet` (`items` + `build_epoch`).
- Server checks against its supported set (`core/v1`, `dsl/v1`); if unsupported, sends an error response with remediation text.
- Responses include `required_capabilities` so clients can detect when they are missing a feature and fall back to LKG; capability errors include remediation guidance.

## Errors & observability (006D)
- Error model: `ControlError { code, message, remediation }` embedded in watch responses; typical codes: `control.unsupported_capability`, `control.invalid_resume_token` (reserved), `control.payload.invalid` (client-side validation).
- Logging (AgentLog):
  - `ControlWatchError` (code/message), `ControlWatchResume` (resume token), `ControlBackoffApplied` (ms), `ControlUpdateRejected/Applied`, validation timing, LKG applied; CA errors surfaced via RPC metadata.
- Metrics/tracing: hooks live in WatchHarness/TelemetryForwarder; integrate with OTLP exporters later.
- Admin visibility: control-plane service exposes required capabilities and backoff in the first response; agents log remediation hints; CA trust bundle exposed via `CertificateAuthority.TrustBundle`.

## Operational defaults
- Backoff: start 1 s, double to max 30 s; server hint overrides.
- Payload: currently empty placeholder; wiring supports binary bundles (routes/policies/extensions) once produced.
- Security: run over mTLS; opaque resume token is echoed; sanitize before logging if it contains user data.
- Identity: agent renews its certificate via the CA gRPC service before `renew_after` (80% of lifetime by default) and writes the refreshed PFX + trust bundle to disk.
