# OmniRelay Integration Tests

Guidance for running the integration suite locally and in CI, with an emphasis on HTTP/3 scenarios that rely on MsQuic.

## Test Scope, Goals, and Approach

### Scope
- Cover end-to-end dispatcher, transport (gRPC/HTTP), codec, and client interactions instead of isolated classes.
- Exercise realistic workflows (handshakes, message exchange, cancellations, error propagation) using production wiring like `DispatcherOptions` and live transports.
- Include lifecycle and infrastructure behavior (startup/shutdown, port allocation, middleware, TLS/runtime flags) to mirror deployed systems.

### Goals
- Catch regressions in cross-component contracts, such as cancellation propagation or metadata negotiation.
- Validate reliability features—timeouts, retries, flow control, metrics hooks—under concurrent or long-running workloads.
- Ensure status codes, payload encodings, and fault translation stay consistent across process boundaries.

### Reasoning
- Unit tests guard local logic, but integration failures usually stem from orchestration gaps, timing races, or transport quirks that only surface with real stacks.
- Using actual transports (HTTP/3, gRPC) exposes platform differences seen in CI runners versus dev machines.
- Reproducing workflows that have failed in production/CI (e.g., duplex cancellation) provides direct regression coverage.

### Methodology
- Spin up dispatchers with concrete inbound/outbound transports on ephemeral ports, registering lightweight fixtures that mimic real services.
- Drive clients via public APIs (`CreateDuplexStreamClient`, etc.) and assert on both client-visible results and server-side signals (task completion sources, counters).
- Combine linked cancellation tokens, explicit `WaitAsync` guards, and readiness probes to surface timing bugs deterministically.
- Capture logs/metrics when scenarios fail so CI artifacts include actionable diagnostics.

### Approach
- Start with deterministic happy-path scenarios, then layer stress cases (client/server cancellation, flow-control pressure, injected errors).
- Use helper utilities like `WaitForGrpcReadyAsync` to remove startup races before assertions.
- Keep fixtures self-contained and fast to minimize flakiness; prefer per-test dispatcher instances over shared global state.
- Document natural follow-up actions (rerunning suites, inspecting metrics) alongside each scenario so failures are easy to triage.

### Best Practices
- **Test public seams.** Continue to drive behavior through DI, transports, and clients exactly as production does so failures describe actual regressions.
- **One scenario per test.** Follow a `Feature_Scenario_Outcome` pattern and keep assertions scoped to that scenario to simplify triage.
- **Lean on async fixtures.** Reuse `IAsyncLifetime`/collection fixtures for expensive hosts or Testcontainers so they spin up once and always dispose cleanly.
- **Capture diagnostics.** Pipe dispatcher logs and container output into `ITestOutputHelper`/`ILoggerFactory` so CI failures include actionable traces.
- **Gate optional dependencies.** Just like HTTP/3 gating, wrap Docker/tooling prerequisites in feature flags or capability checks and fail fast when unavailable.

## MsQuic / HTTP/3 prerequisites

- **Use modern runner images.** HTTP/3 tests require MsQuic support (libmsquic ≥ 2.2). Choose:
  - Windows 11 or Windows Server 2022.
  - Linux distributions that provide libmsquic packages (e.g., Ubuntu 22.04+).
- **Bake MsQuic into the runner.**
  - *Linux:* Add Microsoft’s package feed, then install `libmsquic` and keep it pinned in your provisioning script.
  - *Windows:* Install the MsQuic MSI or enable the optional OS feature. Reboot the image once to ensure the QUIC stack loads.
- **Verify readiness before running tests.** Add a pipeline step (or local script) that checks:

  ```bash
  dotnet script -q - <<'CS'
  using System.Net.Quic;
  Console.WriteLine($"QuicListener supported: {QuicListener.IsSupported}");
  CS
  ```

  Fail fast if the output is `false`. On Linux also confirm `ldd $(which dotnet)` lists `libmsquic`.
- **Gate HTTP/3-specific tests.** Only enable them on runners that pass the check above. Use an environment flag such as `OMNIRELAY_ENABLE_HTTP3_TESTS=true` in your CI definition and conditionally skip QUIC tests elsewhere.
- **Document the environment.** Record required OS versions, installation commands, and validation output so future runner images stay compliant.

## External tooling for compatibility tests

The `CompatibilityInteropIntegrationTests` fixture exercises yab/grpcurl/curl clients and an Envoy proxy. These cases are **skipped automatically** when the required executables are missing, but to run them end-to-end ensure the following tools are available on the PATH:

- `yab` (install via `go install go.uber.org/yarpc/yab@latest`).
- `grpcurl` (install via `go install github.com/fullstorydev/grpcurl/cmd/grpcurl@latest` or download a release).
- `curl` built with HTTP/3 support (`curl --version` should list `HTTP3`).
- `docker` with access to the running daemon (used to launch an Envoy container). The tests expect Docker to resolve `host.docker.internal`.

## Running the suite

```bash
dotnet test tests/OmniRelay.IntegrationTests/OmniRelay.IntegrationTests.csproj
```

Set `OMNIRELAY_ENABLE_HTTP3_TESTS=true` when you want to exercise HTTP/3 cases; leave it unset to run the rest of the suite on runners without MsQuic.

### ResourceLease mesh chaos suites (coming online)

The integration harness also hosts the new ResourceLease mesh failure drills described in `docs/architecture/omnirelay-rpc-mesh.md` (“Failure Drills” section). Enable them with:

```bash
OMNIRELAY_ENABLE_RESOURCELEASE_TESTS=true \
dotnet test tests/OmniRelay.IntegrationTests/OmniRelay.IntegrationTests.csproj
```

These suites spin up multiple dispatcher hosts with shared replicators/deterministic stores, kill peers mid-flight, and validate that replication lag/peer health metrics remain within documented thresholds. Review the architecture doc for the exact scenarios and required environment knobs (timeouts, deterministic store paths, etc.).
