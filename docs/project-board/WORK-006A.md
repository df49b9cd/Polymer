# WORK-006A – Protobuf Schemas & Versioning Policy

## Goal
Define versioned protobuf schemas for control payloads and publish a compatibility/deprecation policy.

## Scope
- Schemas for routes, clusters, policies, extensions, capability sets, epochs/terms.
- Versioning rules and deprecation windows documented.

## Acceptance Criteria
- Schemas checked in with generated code; versioning policy documented.
- Contract tests ensure backward compatibility for minor changes.

## Status
Open

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
