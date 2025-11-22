# Identity, AuthZ, and Policy Story

## Goal
- Provide a federated identity, authentication, and authorization fabric (mutual TLS, token issuance/validation, attribute-based policy, tenant quotas) that complements OmniRelay’s principal propagation and secures hyperscale services end-to-end.

## Scope
- Identity providers (OIDC/SAML), certificate authorities, token services, policy engines, quota/rate governance.
- Covers onboarding, lifecycle management, policy evaluation APIs, auditing, and integration with OmniRelay/MeshKit middleware.
- Excludes transport implementation (OmniRelay) or storage (handled elsewhere).

## Requirements
1. Federated identity service supporting enterprise + service principals with lifecycle automation (rotation, revocation).
2. Mutual TLS stack with automated certificate issuance, rotation, revocation integrated into MeshKit gossip endpoints and OmniRelay transports.
3. Token service issuing OAuth2/JWT/SPIFFE tokens with short-lived credentials, refresh workflows, and offline validation bundles.
4. Policy engine (ABAC/RBAC) with DSL + runtime evaluation service; integrates with OmniRelay middleware to enforce per-tenant rules.
5. Quota/rate governance tied to token metadata, feeding MeshKit backpressure signals.
6. Audit trail capturing authN/Z decisions, policy changes, and certificate events.

## Deliverables
- Identity service deployment artifacts, CA automation scripts, client SDKs/middleware hooks.
- Policy language spec, tooling, UI/CLI for policy management.
- Integration guides for OmniRelay hosts (config, middleware wiring).

## Acceptance Criteria
1. Mutual TLS handshake + token validation path meets latency targets; fallback flows documented.
2. Policy engine enforces deny-by-default, supports dynamic rule updates, and matches compliance audits.
3. OmniRelay middleware can retrieve principal metadata, enforce quotas, and propagate policy context end-to-end.
4. Auditing/logging covers credential issuance, policy changes, and enforcement outcomes.

## References
- OmniRelay principal binding middleware docs.
- Internal security standards, zero-trust requirements.
- Relevant compliance frameworks (SOC 2, ISO 27001, etc.).

## Testing Strategy
- Unit tests for token/cert SDKs, policy parser/validator.
- Integration tests across PKI infrastructure, identity providers, and OmniRelay hosts.
- Feature tests simulating tenant onboarding, key rotation, policy rollout/rollback.

### Unit Test Scenarios
- Token mint/validate flows, clock-skew handling.
- Policy compiler evaluating allow/deny cases, attribute resolution.
- Certificate rotation client verifying trust anchors and failure modes.

### Integration Test Scenarios
- Full mutual TLS handshake between MeshKit nodes using auto-issued certs.
- OmniRelay RPC with token-based auth hitting policy engine for authorization and quotas.
- Policy update propagation with rollback to previous version.

### Feature Test Scenarios
- Blue/green rollout of new identity provider, verifying service continuity.
- Chaos tests revoking certificates/tokens mid-traffic to confirm graceful degradation.
- Compliance drill demonstrating audit log traceability from request to policy decision.

