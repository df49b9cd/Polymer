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

## Running the suite

```bash
dotnet test tests/OmniRelay.IntegrationTests/OmniRelay.IntegrationTests.csproj
```

Set `OMNIRELAY_ENABLE_HTTP3_TESTS=true` when you want to exercise HTTP/3 cases; leave it unset to run the rest of the suite on runners without MsQuic.***End Patch*** End Patch
