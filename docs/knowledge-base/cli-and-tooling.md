# CLI & Operator Tooling

## OmniRelay CLI (`src/OmniRelay.Cli`)
- Built with System.CommandLine; exposed commands include `config` (validate/scaffold), `serve`, `introspect`, `request`, `benchmark`, `script`, `mesh`, and `bootstrap` utilities.
- `mesh` subcommands cover peers, leaders, upgrade (drain/resume), bootstrap token management, and the shard suite:
  - `mesh shards list` — filter by namespace/owner/status/search, supports cursor/pagination and JSON output.
  - `mesh shards diff` — fetches diff streams between resume tokens.
  - `mesh shards simulate` — posts node sets (`--node nodeId[:weight[:region[:zone]]`) to `/control/shards/simulate` and renders plan deltas.
- CLI uses a shared `OmniRelayCliJsonContext` for serialization; `CliRuntime.HttpClientFactory` is injectable for tests.

## Automation Scripts
- `eng/run-ci.sh` reproduces pipeline builds/tests.
- `eng/run-hyperscale-smoke.sh` drives hyperscale feature suites.
- `eng/run-aot-publish.sh [rid] [Configuration]` compiles Native AOT dispatcher artifacts with trimming warnings treated as errors.

## Codegen Tooling
- `protoc-gen-omnirelay-csharp` (from `OmniRelay.Codegen.Protobuf`) integrates with `protoc` invocations; incremental generator package wires OmniRelay endpoints automatically during build.

## Developer Utilities
- `docs/reference/diagnostics.md` provides CLI recipes for shard commands, drain workflows, tracing/logging toggles, and Prometheus scraping.
- `samples/` include runnable CLI scenarios (e.g., Observability playground) that pair CLI commands with dispatcher hosts.