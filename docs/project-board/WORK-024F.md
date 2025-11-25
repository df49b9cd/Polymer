# WORK-024F – Per-Item Cancellation & Tap Helpers

## Goal
Adopt Hugo per-item cancellation/tap helpers (`TapSuccessEachAsync`, `TapFailureEachAsync`, linked-cancellation ForEach) to keep streams responsive and avoid hung consumers.

## Scope
- Stream consumers in `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs` (membership streams) and `src/OmniRelay.ControlPlane/Core/Agent/WatchHarness.cs` (control updates) that iterate `IAsyncEnumerable`.

## Acceptance Criteria
- Async stream loops use Hugo tap/foreach helpers with linked cancellation tokens; no raw `await foreach` without cancellation/token linking.
- Logging/metrics hooks use tap helpers to avoid interfering with data flow.
- Tests verify cancellation stops per-item processing promptly and no unobserved tasks remain.

## Status
Done

## Completion Notes
- Gossip send pump now streams task queue leases through `Result.ForEachLinkedCancellationAsync`, keeping per-item cancellation and failure handling aligned with Hugo semantics.
- Control watch loop uses `Result.MapStreamAsync` + `ForEachLinkedCancellationAsync`, logging/tapping per update without raw `await foreach`; apply enqueue/execute paths return aggregated results.

## SLOs & CI gates
- No increase in per-item overhead; verify via unit benchmarks if available.
- CI: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`.

## Testing Strategy
- Unit: simulate cancellation mid-stream and confirm per-item taps halt.
- Integration: gossip/control watch fixtures to ensure graceful stop on shutdown tokens.
