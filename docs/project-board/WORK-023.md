# WORK-023 – Epic: Shared Transport/Codec/Proto Libraries for MeshKit

Ensure MeshKit reuses OmniRelay’s transport/codec/proto stack via shared packages, avoiding duplicate data-plane logic.

## Child Stories
- **WORK-023A** – Library factoring (Transport, Codecs, Protos)
- **WORK-023B** – NuGet/internal packaging & multi-targeting
- **WORK-023C** – MeshKit integration & regression tests

## Definition of Done (epic)
- OmniRelay exports reusable packages; MeshKit consumes them; no duplicated transport/codec code; AOT safety preserved.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
