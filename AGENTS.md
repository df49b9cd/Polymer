You are an expert C#/.NET developer. You help with .NET tasks by giving clean, well-designed, error-free, fast, secure, readable, and maintainable code that follows .NET conventions. You also give insights, best practices, general software design tips, and testing best practices.

# Repository Guidelines

## Important Notes
- We must be as performant and efficient as possible due to our focus on Native AOT. Therefore read and understand the 'dotnet-performance-guidelines.md' located in docs/knowledge-base.
- Always, keep the docs/knowledge-base documents updated and current.

## Project Structure & Module Organization
- `src/` houses all production code; core runtime lives in `src/OmniRelay`, CLI in `src/OmniRelay.Cli`, and codegen in `src/OmniRelay.Codegen.*`.
- `tests/` mirrors those areas with xUnit projects (`OmniRelay.Core.UnitTests`, `OmniRelay.Cli.UnitTests`, `OmniRelay.HyperscaleFeatureTests`, etc.). Interop and yab suites sit in `tests/OmniRelay.YabInterop`.
- `docs/` contains architecture notes and guidance (AOT, diagnostics, samples).
- `eng/` holds repeatable scripts (`run-ci.sh`, `run-aot-publish.sh`, `run-hyperscale-smoke.sh`). Docker recipes live in `docker/`. Runnable samples are under `samples/`.

## Build, Test, and Development Commands
- Restore/build solution: `dotnet build OmniRelay.slnx` (targets .NET 10; respects Directory.Build.* settings).
- Fast unit slice: `dotnet test tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj`.
- Full CI parity: `./eng/run-ci.sh` (wraps restore + build + primary test sets).
- Hyperscale/interop container smoke: `docker build -f docker/Dockerfile.hyperscale.ci .` (invokes `eng/run-hyperscale-smoke.sh` internally).
- Native AOT publish: `./eng/run-aot-publish.sh [rid] [Configuration]` (defaults to `linux-x64 Release`).
- CLI development: `dotnet run --project src/OmniRelay.Cli -- --help` for local validation flows.

## Coding Style & Naming Conventions
- C# uses spaces with `indent_size = 4`; file-scoped namespaces; System usings sorted first; braces required even for single statements; newline before braces.
- Prefer `var` for locals; UTF-8 with final newline; trim trailing whitespace.
- Namespaces and packages follow `OmniRelay.*`; public types/members in `PascalCase`, locals/fields in `camelCase`; async methods end with `Async`.
- Keep configuration examples under `docs/` or `samples/`; avoid committing real secrets or environment-specific endpoints.

## Testing Guidelines
- Framework: xUnit across unit, integration, and feature suites. Typical naming: `*Tests.cs` for unit, `*FeatureTests` for broader coverage.
- Run targeted filters with `dotnet test <proj> --filter Category=<name>` when available; keep new tests deterministic (no external network).
- CI reports coverage to Codecov; aim to cover new branches/edge cases when touching transports, middleware, or codecs.
- For transport/interop changes, run `tests/OmniRelay.YabInterop` and the hyperscale Docker recipe before opening a PR.

## Commit & Pull Request Guidelines
- Follow the existing conventional-prefix style seen in history (`feat:`, `fix:`, `chore:`, `docs:`, `revert …`). Keep subject imperative and ≤72 characters; include scope in the body if helpful.
- PRs should link issues/tickets, list user-facing changes and breaking notes, and quote key commands executed (e.g., `dotnet build OmniRelay.slnx; dotnet test …`).
- Attach logs or screenshots for CLI/messages changes when output shape matters; update `docs/` or samples alongside behavior changes.
- Before pushing, ensure `dotnet format`/IDE analyzers are clean per `.editorconfig` and that core/unit suites pass.***
