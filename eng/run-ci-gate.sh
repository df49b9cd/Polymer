#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
ARTIFACTS="$ROOT/artifacts/ci"
RID=${RID:-linux-x64}
CONFIG=${CONFIG:-Release}
SKIP_AOT=${SKIP_AOT:-0}

mkdir -p "$ARTIFACTS/test-results"

mkdir -p "$ARTIFACTS"

# 1) Build all
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet build "$ROOT/OmniRelay.slnx" -c "$CONFIG" --nologo

# 2) Targeted test slices (fast gate)
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test "$ROOT/tests/OmniRelay.Dispatcher.UnitTests/OmniRelay.Dispatcher.UnitTests.csproj" -c "$CONFIG" --no-build --nologo --logger "trx;LogFileName=dispatcher.trx" --results-directory "$ARTIFACTS/test-results"
DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet test "$ROOT/tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj" -c "$CONFIG" --no-build --nologo --logger "trx;LogFileName=core.trx" --results-directory "$ARTIFACTS/test-results"

# 3) AOT publish (data-plane, control-plane, CLI) unless skipped
if [[ "$SKIP_AOT" != "1" ]]; then
  DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$ROOT/src/OmniRelay.DataPlane.Host/OmniRelay.DataPlane.Host.csproj" -c "$CONFIG" -r "$RID" -p:PublishAot=true -p:StripSymbols=true -p:PublishSingleFile=true -p:SelfContained=true -o "$ARTIFACTS/dataplane-host-$RID"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$ROOT/src/OmniRelay.ControlPlane.Host/OmniRelay.ControlPlane.Host.csproj" -c "$CONFIG" -r "$RID" -p:PublishAot=true -p:StripSymbols=true -p:PublishSingleFile=true -p:SelfContained=true -o "$ARTIFACTS/controlplane-host-$RID"
  DOTNET_CLI_TELEMETRY_OPTOUT=1 dotnet publish "$ROOT/src/OmniRelay.Cli/OmniRelay.Cli.csproj" -c "$CONFIG" -r "$RID" -p:PublishAot=true -p:PublishSingleFile=true -p:SelfContained=true -o "$ARTIFACTS/cli-$RID"
fi

echo "CI gate completed. Artifacts under $ARTIFACTS".
