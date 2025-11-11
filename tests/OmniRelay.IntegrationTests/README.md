# OmniRelay Integration Tests

Guidance for running the integration suite locally and in CI, with an emphasis on HTTP/3 scenarios that rely on MsQuic.

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
