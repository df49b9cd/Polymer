# WORK-007B – Rotation/Renewal & Trust Bundle Delivery

## Goal
Automate certificate renewal and distribute trust bundles to agents/OmniRelay.

## Scope
- Renewal scheduler with backoff; grace/LKG handling.
- Trust bundle publication and retrieval.
- Client logic in agent/OmniRelay to renew and reload certs without traffic loss.

## Acceptance Criteria
- Renewal occurs before expiry; traffic continues uninterrupted in tests.
- Trust bundle updates propagate; stale bundles detected and refreshed.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
