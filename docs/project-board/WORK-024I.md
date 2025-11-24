# WORK-024I – Data-Plane Error Aggregation on Streams

## Goal
Use Hugo error aggregation helpers (`CollectErrorsAsync`, tap-each aggregate) in data-plane streaming paths where full failure visibility is needed.

## Scope
- Tee/outbound and codec pipelines in `src/OmniRelay.Codecs` and `src/OmniRelay.DataPlane/Transport` (HTTP/gRPC stream calls) that currently short-circuit on first error.
- Dispatcher streaming validations where multiple records/frames may fail independently.

## Acceptance Criteria
- Streams requiring diagnostics use aggregation helpers; aggregated `Error` includes counts/keys for observability.
- Short-circuit remains the default elsewhere; aggregation points documented per method.
- Tests assert aggregation over multiple failures and keep success path allocation-lean.

## Status
Planned

## SLOs & CI gates
- No hot-path allocation regression; validate with unit perf guards or counters.
- CI: `dotnet test tests/OmniRelay.Dispatcher.UnitTests` and codec/transport unit suites.

## Testing Strategy
- Unit: synthetic streams with mixed success/failure to verify aggregated error contents.
- Integration: dispatcher/transport feature tests to ensure aggregation doesn’t mask cancellations.
