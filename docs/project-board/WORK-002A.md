# WORK-002A – Ban-List Analysis & AOT Safety Guards

## Goal
Identify and block reflection/JIT/dynamic APIs in OmniRelay hot paths with automated checks.

## Scope
- Expand banned-API list per `dotnet-performance-guidelines.md` and project needs.
- Add analyzer or Roslyn-based check; integrate into build.
- Provide suppression workflow for rare safe cases.

## Deliverables
- Analyzer/check + CI wiring.
- Docs in knowledge-base covering banned APIs and how to remediate.

## Acceptance Criteria
- Build/CI fails on new banned API usage in OmniRelay assemblies.
- Suppression requires justification and is documented.

## Status
Done — Banned API list created (`eng/banned-apis.txt`) and enforced via `CheckBannedApis` target (runs before build). Guidance documented in `docs/knowledge-base/aot-banned-apis.md`; bypass only with `SkipBannedApiCheck=true` and justification.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
