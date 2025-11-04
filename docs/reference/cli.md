# OmniRelay CLI

The `omnirelay` command-line tool provides a lightweight way to validate dispatcher configuration, inspect a running dispatcher, and issue ad-hoc RPCs over HTTP or gRPC. This mirrors common `yab` workflows while staying aligned with Polymer's configuration and metadata model.

## Installation

Build the solution or publish the CLI project directly:

```bash
# Restore and build the solution
dotnet build

# Optionally publish a self-contained binary
dotnet publish src/OmniRelay.Cli/OmniRelay.Cli.csproj -c Release -o artifacts/cli
```

The resulting executable is located under `src/OmniRelay.Cli/bin/<Configuration>/net10.0/` or in the publish output.

## Command overview

```text
omnirelay <command> [options]
```

| Command | Purpose |
| ------- | ------- |
| `config validate` | Load one or more configuration files, apply ad-hoc overrides, and verify the dispatcher can be constructed. |
| `introspect` | Fetch `/polymer/introspect` and print a summary (or the raw JSON) of a running dispatcher. |
| `request` | Issue a unary RPC over HTTP or gRPC with configurable metadata and payload sources. |
| `script run` | Replay a sequence of requests, introspection calls, and delays defined in a JSON automation file. |

Run `omnirelay <command> --help` for a detailed option list.

## Workflow examples

### Validate layered configuration

```bash
omnirelay config validate \
  --config docs/reference/configuration/appsettings.json \
  --config docs/reference/configuration/appsettings.Development.json \
  --set polymer:outbounds:ledger:unary:http:0:url=http://127.0.0.1:8081
```

The command prints the resolved service name, dispatcher status, procedure counts, and registered lifecycle components. Any schema or binding errors from `AddPolymerDispatcher` halt the run with a non-zero exit code.

### Inspect a running dispatcher

```bash
omnirelay introspect --url http://localhost:8080/polymer/introspect --format text
```

Switch `--format json` to emit the raw JSON payload for scripting or piping into `jq`.

### Issue test RPCs

HTTP example:

```bash
omnirelay request \
  --transport http \
  --url http://localhost:8080/yarpc/v1 \
  --service echo \
  --procedure echo::ping \
  --caller cli-demo \
  --encoding application/json \
  --body '{"message":"hello from omnirelay"}'

omnirelay request \
  --url http://localhost:8080/yarpc/v1 \
  --service echo \
  --procedure echo::ping \
  --profile json:pretty \
  --body '{"message":"profile aware"}'
```

The second variant applies the `json:pretty` profile, which auto-populates `Content-Type`, `Accept`, and the request encoding before reformatting the inline JSON.

Binary gRPC example (payload encoded as base64):

```bash
omnirelay request \
  --transport grpc \
  --address http://localhost:9090 \
  --service echo \
  --procedure Ping \
  --caller cli-demo \
  --body-base64 CgxhbHNvIGdycGM=
```

Use `--body-file` to stream large payloads directly off disk and `--header key=value` to attach transport headers.

## Codec-aware profiles

Profiles shortcut common encoding concerns:

- `--profile json[:pretty]` sets `Content-Type`/`Accept` to `application/json`, updates the request encoding, and (for `pretty`) reflows inline JSON prior to dispatch.
- `--profile protobuf:<message>` converts JSON input into protobuf bytes. Supply one or more `--proto-file` values that point to descriptor sets (e.g. `.protoset` files) and, if the profile omits a message name, provide `--proto-message package.Type`.

Example end-to-end flow using the bundled echo descriptor:

```bash
dotnet run --project tests/OmniRelay.YabInterop/OmniRelay.YabInterop.csproj -- --dump-descriptor docs/reference/cli-scripts/echo.protoset

omnirelay request \
  --transport grpc \
  --address http://127.0.0.1:9090 \
  --service echo \
  --procedure Ping \
  --profile protobuf:echo.EchoRequest \
  --proto-file docs/reference/cli-scripts/echo.protoset \
  --body '{"message":"hello from protobuf"}'
```

If the body was supplied via `--body-base64`, the CLI assumes you already encoded the payload and skips profile transforms.

## Scripted automation

Complex smoke tests become easier when they are declared in a single JSON file. The `script run` subcommand executes each step in order and stops on the first failure by default (use `--continue-on-error` to keep going, or `--dry-run` to preview the flow).

Example script: `docs/reference/cli-scripts/echo-harness.json`

```bash
# Start the sample echo harness (separate terminal)
bash tests/OmniRelay.YabInterop/run-yab.sh

# In another terminal, replay the scripted scenario
omnirelay script run --file docs/reference/cli-scripts/echo-harness.json
```

Each step is typed (`request`, `introspect`, or `delay`) and matches the options exposed by the interactive commands:

```json
{
  "steps": [
    {
      "type": "request",
      "transport": "http",
      "service": "echo",
      "procedure": "echo::ping",
      "url": "http://127.0.0.1:8080/yarpc/v1",
      "encoding": "application/json",
      "body": "{\"message\":\"hello\"}",
      "headers": {
        "x-run-id": "automation"
      }
    },
    {
      "type": "delay",
      "duration": "1s"
    },
    {
      "type": "introspect",
      "url": "http://127.0.0.1:8080/polymer/introspect",
      "format": "text",
      "timeout": "3s"
    }
  ]
}
```

This mirrors common `yab` automation flows, while keeping the payload format aligned with Polymer's transport metadata. Mix HTTP and gRPC requests in one file, adjust headers per call, and share the scripts with CI for reproducible diagnostics.

The companion script `docs/reference/cli-scripts/grpc-protobuf.json` exercises the protobuf profile against the gRPC listener. Generate the descriptor once (see above), start the harness in dual mode (`MODE=both bash tests/OmniRelay.YabInterop/run-yab.sh`), and replay the script:

```bash
omnirelay script run --file docs/reference/cli-scripts/grpc-protobuf.json
```

Stop the harness when you are done (`Ctrl+C`).

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

until omnirelay introspect --url "${ENDPOINT%/yarpc/v1}/polymer/introspect" --timeout 2s >/dev/null 2>&1; do
  echo "Waiting for dispatcher ..."
  sleep 1
done

echo "Validating configuration ..."
omnirelay config validate --config "$CONFIG"

echo "Issuing smoke test ..."
omnirelay request \
  --transport http \
  --url "$ENDPOINT" \
  --service "$SERVICE" \
  --procedure "$PROCEDURE" \
  --encoding application/json \
  --body "$BODY"
```

Drop this script into `scripts/omnirelay-smoke.sh`, mark it executable, and point CI to it after publishing a build. Non-zero exits indicate the dispatcher failed validation or the smoke test RPC.

## Install as a .NET tool

`OmniRelay.Cli` packs as a .NET global tool. Publish a local package and install it from the generated feed:

```bash
dotnet pack src/OmniRelay.Cli/OmniRelay.Cli.csproj -c Release -o artifacts/cli
dotnet tool install --global OmniRelay.Cli --add-source artifacts/cli

# Available everywhere as `omnirelay`
omnirelay --help
```

Push the resulting `.nupkg` to your package registry of choice once the version is finalised and trim the `--add-source` flag.

## Next steps

- Surface profile discovery (`omnirelay request --profile list`) and user-defined profile files.
- Add protobuf descriptor caching to avoid rebuilding sets on every invocation.
- Publish pre-built descriptor packs for popular demos to simplify quickstarts.
