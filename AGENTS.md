# Repository Guidelines

## Project Structure & Module Organization
OmniRelay.slnx groups runtime code under `src/`, with `src/OmniRelay` producing the dispatcher/runtime DLL, `src/OmniRelay.Configuration` shipping DI helpers, and `src/OmniRelay.Cli` plus the `src/OmniRelay.Codegen.*` and `src/OmniRelay.ResourceLeaseReplicator.*` folders covering tooling and optional services.
Matching unit, feature, integration, hyperscale, and yab interop suites sit in `tests/OmniRelay.*`, sharing fixtures from `tests/TestSupport`.
Reference docs and RFC-style notes live in `docs/`, runnable walkthroughs land in `samples/`, and repository artwork is kept in `branding/`.
Keep new assets inside those buckets so OmniRelay stays navigable.

## Build, Test, and Development Commands
`global.json` pins the .NET SDK to `10.0.100`, so install that preview before building.
Key loops:
- `dotnet build OmniRelay.slnx` – compiles every library, CLI, and analyzer with Nullable + analyzers enabled via `Directory.Build.props`.
- `dotnet test tests/OmniRelay.Tests/OmniRelay.Tests.csproj` – exercises the aggregate xUnit suite; ensure localhost HTTP/2 is available for gRPC flows.
- `dotnet pack src/OmniRelay.Cli/OmniRelay.Cli.csproj -c Release -o artifacts/cli` – produces a local CLI NuGet; `dotnet tool install --global OmniRelay.Cli --add-source artifacts/cli` installs it for smoke testing.

## Coding Style & Naming Conventions
`.editorconfig` enforces UTF-8, trimmed trailing whitespace, and spaces everywhere (4 for `.cs`/`.sh`, 2 for JSON, YAML, props, and resx).
Declare file-scoped namespaces, always keep braces, and stick with implicit usings and nullable reference types that `Directory.Build.props` enables.
Follow the `OmniRelay.<Feature>` pattern for projects and namespaces; mirror it in test assemblies (`OmniRelay.<Feature>.UnitTests`, etc.).
Depend on the centrally managed package versions in `Directory.Packages.props` rather than adding ad-hoc numbers.

## Testing Guidelines
All suites run on xUnit v3 with Shouldly assertions plus the `coverlet.collector`, so collect coverage with `dotnet test --collect:"XPlat Code Coverage"` when validating larger changes.
Place scenario-specific tests in the matching folder (`tests/OmniRelay.Dispatcher.UnitTests`, `tests/OmniRelay.IntegrationTests`, `tests/OmniRelay.YabInterop`, etc.) and reuse helpers from `tests/TestSupport`.
Tests that touch transports should use the provided `TestPortAllocator` and TLS factories to avoid flakiness.

## Commit & Pull Request Guidelines
Recent history follows conventional prefixes (`feat:`, `fix:`, `test:`, `docs:`), so continue using `type: summary` subjects (e.g., `feat: Enhance TLS configuration with inline certificate data`).
Reference the related issue or `todo.md` entry, describe behavioral changes and config migrations, and attach relevant `dotnet build`/`dotnet test` output or CLI screenshots.
For PRs, include reproduction steps, note any new docs (`docs/reference/...`) or samples touched, and highlight rollout considerations such as required HTTP/2 support or certificate handling changes.
