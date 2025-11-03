# Polymer CLI

The `polymer` command-line tool provides a lightweight way to validate dispatcher configuration, inspect a running dispatcher, and issue ad-hoc RPCs over HTTP or gRPC. This mirrors common `yab` workflows while staying aligned with Polymer's configuration and metadata model.

## Installation

Build the solution or publish the CLI project directly:

```bash
# Restore and build the solution
dotnet build

# Optionally publish a self-contained binary
dotnet publish src/Polymer.Cli/Polymer.Cli.csproj -c Release -o artifacts/cli
```

The resulting executable is located under `src/Polymer.Cli/bin/<Configuration>/net10.0/` or in the publish output.

## Command overview

```text
polymer <command> [options]
```

| Command | Purpose |
| ------- | ------- |
| `config validate` | Load one or more configuration files, apply ad-hoc overrides, and verify the dispatcher can be constructed. |
| `introspect` | Fetch `/polymer/introspect` and print a summary (or the raw JSON) of a running dispatcher. |
| `request` | Issue a unary RPC over HTTP or gRPC with configurable metadata and payload sources. |

Run `polymer <command> --help` for a detailed option list.

## Workflow examples

### Validate layered configuration

```bash
polymer config validate \
  --config docs/reference/configuration/appsettings.json \
  --config docs/reference/configuration/appsettings.Development.json \
  --set polymer:outbounds:ledger:unary:http:0:url=http://127.0.0.1:8081
```

The command prints the resolved service name, dispatcher status, procedure counts, and registered lifecycle components. Any schema or binding errors from `AddPolymerDispatcher` halt the run with a non-zero exit code.

### Inspect a running dispatcher

```bash
polymer introspect --url http://localhost:8080/polymer/introspect --format text
```

Switch `--format json` to emit the raw JSON payload for scripting or piping into `jq`.

### Issue test RPCs

HTTP example:

```bash
polymer request \
  --transport http \
  --url http://localhost:8080/yarpc/v1 \
  --service echo \
  --procedure echo::ping \
  --caller cli-demo \
  --encoding application/json \
  --body '{"message":"hello from polymer"}'
```

Binary gRPC example (payload encoded as base64):

```bash
polymer request \
  --transport grpc \
  --address http://localhost:9090 \
  --service echo \
  --procedure Ping \
  --caller cli-demo \
  --body-base64 CgxhbHNvIGdycGM=
```

Use `--body-file` to stream large payloads directly off disk and `--header key=value` to attach transport headers.

## Automation recipe

The CLI is designed to slot into shell scripts and CI workflows. The following helper waits for a dispatcher to become ready, validates configuration, and executes a smoke test call:

```bash
#!/usr/bin/env bash
set -euo pipefail

CONFIG=${1:-appsettings.json}
ENDPOINT=${2:-http://127.0.0.1:8080/yarpc/v1}
SERVICE=${3:-echo}
PROCEDURE=${4:-echo::ping}
BODY=${5:-'{"message":"smoke"}'}

until polymer introspect --url "${ENDPOINT%/yarpc/v1}/polymer/introspect" --timeout 2s >/dev/null 2>&1; do
  echo "Waiting for dispatcher ..."
  sleep 1
done

echo "Validating configuration ..."
polymer config validate --config "$CONFIG"

echo "Issuing smoke test ..."
polymer request \
  --transport http \
  --url "$ENDPOINT" \
  --service "$SERVICE" \
  --procedure "$PROCEDURE" \
  --encoding application/json \
  --body "$BODY"
```

Drop this script into `scripts/polymer-smoke.sh`, mark it executable, and point CI to it after publishing a build. Non-zero exits indicate the dispatcher failed validation or the smoke test RPC.

## Next steps

- Extend the CLI with presets for common payload codecs (JSON profiles, protobuf descriptors).
- Package the CLI as a global tool once the command surface stabilises.
