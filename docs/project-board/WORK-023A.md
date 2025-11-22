# WORK-023A – Library Factoring (Transport, Codecs, Protos)

## Goal
Extract shared components into `src/OmniRelay.Transport`, `src/OmniRelay.Codecs`, and `src/OmniRelay.Protos` while keeping OmniRelay as the data-plane core, and wire OmniRelay to consume them (no duplicate sources).

## Scope
- Move/commonize transport pipeline, HTTP/gRPC clients/servers, middleware, pooling into `OmniRelay.Transport`.
- Factor codecs (encoders/decoders, content negotiation, protobuf JSON/CBOR) into `OmniRelay.Codecs`.
- Establish proto source layout and codegen pipeline for `OmniRelay.Protos`.
- Update OmniRelay to reference these projects and drop direct compilation of moved files.

## Acceptance Criteria
- Core/Transport sources compiled only via the new projects; OmniRelay project references them and no longer compiles those files directly.
- Builds succeed with the new structure; OmniRelay data-plane tests remain green.

## Status
Done — codec/transport contracts and errors physically moved into `src/OmniRelay.Codecs`; protos moved into `src/OmniRelay.Protos`; OmniRelay references the libraries and no longer compiles those sources directly; solution builds clean.

## Testing Strategy
- Unit: Cover new logic/config parsing/helpers introduced by this item.
- Integration: Exercise end-to-end behavior via test fixtures (hosts/agents/registry) relevant to this item.
- Feature: Scenario-level validation of user-visible workflows touched by this item across supported deployment modes/roles.
- Hyperscale: Run when the change affects runtime/throughput/scale; otherwise note non-applicability with rationale in the PR.
