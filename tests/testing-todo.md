CLI + Tooling:

src/OmniRelay.Cli/Program.cs (line 1830) (RunAutomationAsync) and  (line 2196) (RunMeshLeadersStatusAsync/RunMeshLeadersWatchAsync) drive scripts, HTTP requests, introspection, delays, and SSE streaming, but we only assert “file missing” or “URL invalid”. Add fixture scripts to cover happy-path execution, --dry-run, --continue-on-error, SSE parsing, and timeout branches so CLI regressions are caught.
src/OmniRelay.Cli/CliRuntime.cs (lines 24-183) wires the default host, file-system, HTTP client, and gRPC invoker. No test proves DefaultServeHostFactory actually wires AddOmniRelayDispatcher, or that DefaultGrpcInvokerFactory starts/stops/cleans up GrpcOutbound. Introduce unit tests that substitute fake IHost/GrpcOutbound instances and assert lifecycle + disposal semantics.


Dispatcher + Transport:

src/OmniRelay/Dispatcher/Config/DispatcherConfigMapper.cs — add tests for HTTPS inbound validation (require certs), gRPC inbound URL/service-name validation, HTTP outbound streaming guards, HttpClientFactory reuse, and gRPC outbound HTTP/3/runtime/peer chooser validation. The new mapper should enforce these without reflection.
src/OmniRelay.Core/Transport/TeeOutbounds.cs:231-351 & 474-559 adds background-drain logic, aggregated stop semantics, and oneway teeing, but Codecov reports 0% on the new sections. Augment tests/OmniRelay.Core.UnitTests/Transport/TeeOutboundsTests.cs with cases that (a) verify DrainShadowWorkAsync cancels outstanding work, (b) assert simultaneous start failure stops both outbounds, and (c) cover the oneway path and aggregated exceptions.


Leadership + Gossip:

src/OmniRelay/Core/Leadership/LeadershipCoordinator.cs (lines 174-360) and  (lines 493-588) contain the new evaluation loop logic: gossip gating, election backoff, renew failure handling, lease health updates, and shard scope resolution. Current tests (tests/OmniRelay.Core.UnitTests/Leadership/LeadershipCoordinatorTests.cs (lines 13-173) plus the integration suite) only cover “elect someone” and “wait for gossip healthy”, so scenarios like renewal failure, expired leases transitioning to PublishLoss, shard namespace parsing, and _leaseHealthTracker updates are entirely untested.
src/OmniRelay/Core/Leadership/LeadershipEventHub.cs (lines 36-111) implements snapshot-first subscriptions, per-scope filtering, backlog drops, and cancellation cleanup, yet there is no direct unit test; coverage only comes indirectly through coordinator tests. Add focused tests that subscribe, assert the initial snapshot emission, simulate backlog overflow (ensure DroppedEvent fires), and ensure cancellation removes subscriptions.
src/OmniRelay/Core/Leadership/LeadershipGrpcExtensions.cs (lines 1-45) converts internal events/tokens to protobuf types; nothing verifies enum mapping (e.g., SteppedDown → ProtoLeadershipEventKind.SteppedDown) or label preservation. A simple serialization test would guard against wire regressions.
These are the hotspots driving the Codecov warning—filling them with targeted unit/integration tests will raise the patch coverage and, more importantly, lock down the new behaviors. Natural next steps are to prioritize high-risk runtime paths first (CLI automation, dispatcher builder validation, leadership renewals) so we uncover regressions before landing further changes.
