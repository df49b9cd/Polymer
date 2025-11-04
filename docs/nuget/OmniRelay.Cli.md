# OmniRelay.Cli

`OmniRelay.Cli` is a .NET global tool that ships the `omnirelay` command for operating OmniRelay dispatchers.

## Commands

- `omnirelay config validate` &mdash; validate layered configuration files.
- `omnirelay introspect` &mdash; query `/polymer/introspect` and print dispatcher state.
- `omnirelay request` &mdash; issue HTTP or gRPC RPCs with JSON/protobuf profiles.
- `omnirelay benchmark` &mdash; drive load against HTTP/gRPC transports.
- `omnirelay script run` &mdash; execute scripted smoke tests.

## Installation

```bash
dotnet tool install --global OmniRelay.Cli
```

Documentation and examples live in `docs/reference/cli.md` and `docs/reference/cli-scripts/`.

