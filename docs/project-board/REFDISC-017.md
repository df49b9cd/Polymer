# REFDISC-017 - CLI & Diagnostics Client Helpers

## Goal
Provide a shared client helper library for interacting with OmniRelay control-plane APIs (HTTP + gRPC) so CLI tools, automation scripts, and services can authenticate, send requests, and parse responses consistently.

## Scope
- Extract HTTP/gRPC client setup, auth handling (mTLS, tokens), and response parsing from the existing CLI.
- Offer high-level helpers for common diagnostics calls (`/omnirelay/control/*`, registry queries, peer listings).
- Support streaming responses and pagination where applicable.
- Document usage for CLI, automation scripts, and service integrations.

## Requirements
1. **Auth parity** - Support the same authentication methods as dispatcher (mTLS, bearer tokens), leveraging the shared TLS + middleware registries.
2. **Protocol support** - Clients must default to HTTP/3/gRPC with downgrade capability, reusing the shared control-plane factories (REFDISC-002).
3. **Error handling** - Normalize errors (status codes, gRPC statuses) into typed exceptions with actionable messages.
4. **Serialization** - Use the shared registry + diagnostics models to parse responses, ensuring version compatibility.
5. **Extensibility** - Allow new commands to plug into the helpers without duplicating transport or auth logic.

## Deliverables
- Client helper library under `OmniRelay.ControlPlane.Client`.
- CLI refactor to depend on the helper library.
- Documentation demonstrating how to use the helpers from scripts/services.

## Acceptance Criteria
- CLI commands continue to function with identical output/error messaging after migration.
- Automation scripts can reference the helper library to call control-plane APIs with minimal setup.
- Helpers reuse shared transport factories/TLS, ensuring consistency with dispatcher hosts.
- Error handling surfaces underlying causes (network, auth, validation) clearly.
- Library introduces no CLI-specific dependencies, enabling reuse by services.

- Native AOT gate: Publish with /p:PublishAot=true and treat trimming warnings as errors per REFDISC-034..037.

## Testing Strategy
All test tiers must run against native AOT artifacts per REFDISC-034..037.


### Unit tests
- Cover auth handlers (mTLS, token injection) to ensure headers/certificates attach correctly.
- Test error translation logic from HTTP/gRPC responses to typed exceptions.
- Validate serialization/deserialization for diagnostics and registry responses using sample payloads.

### Integration tests
- Execute helper calls against a test control-plane host (HTTP + gRPC) to verify handshake, auth, and parsing.
- Simulate auth failures and ensure helpers raise consistent exceptions.
- Test streaming/paginated endpoints to confirm helpers handle them gracefully.

### Feature tests
- In OmniRelay.FeatureTests, run CLI scenarios via the helpers and compare outputs/logs to pre-refactor baselines.
- Exercise automation workflows (e.g., scripted peer inspection) using the helpers to ensure they operate identically to the CLI.

### Hyperscale Feature Tests
- Under OmniRelay.HyperscaleFeatureTests, drive high-volume CLI/helper calls to ensure connection pooling and auth caching work at scale.
- Inject network faults to validate retry/backoff behavior remains consistent.

## References
- Current CLI client implementations (see `src/OmniRelay.Cli`).
- Shared transport/TLS refs: REFDISC-002, REFDISC-003.
- REFDISC-034..037 - AOT readiness baseline and CI gating.
