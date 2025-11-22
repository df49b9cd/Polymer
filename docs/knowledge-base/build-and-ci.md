# Build & CI

## Local Build Loop
- Restore/build: `dotnet build OmniRelay.slnx` (targets .NET 10, honors Directory.Build.* files).
- Targeted tests: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj` or specific suites via `--filter Category=<name>`.

## Scripts
- `./eng/run-ci.sh` – wraps restore + build + core unit/integration suites to match GitHub Actions.
- `./eng/run-aot-publish.sh [rid] [Configuration]` – publishes trimmed Native AOT dispatcher binaries (defaults to `linux-x64 Release`).
- `./eng/run-hyperscale-smoke.sh` – runs hyperscale feature/integration smoke tests inside the repo.

## Docker Targets
- `docker build -f docker/Dockerfile.ci .` – reproduces CI image (restore/build/test) with cached NuGet feeds.
- `docker build -f docker/Dockerfile.hyperscale.ci .` – executes hyperscale smoke harness inside a container.

## CI Pipelines
- GitHub Actions (see `publish-packages.yml`) run Docker-based builds, execute prioritized tests, collect Codecov coverage, and publish NuGet packages on tag pushes (`v*`).
- Duplicate runs are cancelled via concurrency groups; badges for build, coverage, and NuGet packages appear in `README.md`.

## Native AOT & Trimming
- OmniRelay enforces trimming analyzers (`EnableTrimAnalyzer`, `EnableAotAnalyzer`, `EnableSingleFileAnalyzer` set to true). Treat warnings as errors for AOT builds; follow `docs/architecture/aot-guidelines.md` for linker hints.

## Output
- Build artifacts land under `src/*/bin/Debug|Release/net10.0`. Tests may emit coverage artifacts in `tests/*/TestResults/` when coverage collectors are enabled.