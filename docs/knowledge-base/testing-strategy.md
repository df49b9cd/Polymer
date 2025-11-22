# Testing Strategy

## Guidelines
- All tests use xUnit (`TestTimeouts.Default` ensures runs complete quickly). Keep them deterministic (no external network) and follow naming conventions (`*Tests.cs`, `*FeatureTests`).
- When touching transports/middleware/shards, run the relevant suites plus `dotnet build OmniRelay.slnx`. Large changes should pass `./eng/run-ci.sh` or containerized hyperscale smoke tests.

## Suites
- **Unit**
  - `tests/OmniRelay.Core.UnitTests` – covers transports, middleware, peer choosers, codecs, resource lease logic, and sharding repositories (see `RelationalShardStoreTests`).
  - Component-specific projects (Configuration, Dispatcher, Codegen) validate binder policies, middleware stacks, and incremental generators.
- **Integration**
  - `tests/OmniRelay.IntegrationTests` use `ShardControlPlaneTestHost` and other harnesses to exercise HTTP/gRPC negotiation, shard diagnostics, configuration hosting, and bootstrap flows.
  - `tests/OmniRelay.YabInterop` runs yab-driven HTTP/gRPC conformance scenarios for cross-language parity.
- **Feature**
  - `tests/OmniRelay.FeatureTests` script operator workflows (e.g., CLI shard commands) against live hosts.
  - `tests/OmniRelay.HyperscaleFeatureTests` stress diagnostics endpoints with thousands of shards and pagination loops.
- **Specialized**
  - `tests/OmniRelay.Cli.UnitTests` stub `HttpClientFactory` to lock CLI behavior.
  - `tests/OmniRelay.Codegen.Tests` ensure protoc/Roslyn outputs stay in sync with runtime expectations.

## Commands
- Fast loop: `dotnet build OmniRelay.slnx && dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`.
- CLI validations: `dotnet test tests/OmniRelay.Cli.UnitTests/OmniRelay.Cli.UnitTests.csproj`.
- Feature & hyperscale: `dotnet test tests/OmniRelay.FeatureTests/OmniRelay.FeatureTests.csproj` and `dotnet test tests/OmniRelay.HyperscaleFeatureTests/OmniRelay.HyperscaleFeatureTests.csproj` (takes ~40s on arm64).
- Full CI parity: run `./eng/run-ci.sh` or `docker build -f docker/Dockerfile.hyperscale.ci .`.