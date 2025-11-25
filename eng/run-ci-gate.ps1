Param(
    [string]$RID = "win-x64",
    [string]$CONFIG = "Release",
    [switch]$SKIP_AOT
)

$ErrorActionPreference = "Stop"
$root = (git rev-parse --show-toplevel)
$artifacts = Join-Path $root "artifacts/ci"

New-Item -ItemType Directory -Force -Path (Join-Path $artifacts "test-results") | Out-Null
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

# 1) Build all
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
dotnet build "$root/OmniRelay.slnx" -c $CONFIG --nologo

# 2) Targeted test slices (fast gate)
dotnet test "$root/tests/OmniRelay.Dispatcher.UnitTests/OmniRelay.Dispatcher.UnitTests.csproj" -c $CONFIG --no-build --nologo --logger "trx;LogFileName=dispatcher.trx" --results-directory "$artifacts/test-results"
dotnet test "$root/tests/OmniRelay.Core.UnitTests/OmniRelay.Core.UnitTests.csproj" -c $CONFIG --no-build --nologo --logger "trx;LogFileName=core.trx" --results-directory "$artifacts/test-results"

# 3) AOT publish (data-plane, control-plane, CLI) unless skipped
if (-not $SKIP_AOT) {
    dotnet publish "$root/src/OmniRelay.DataPlane.Host/OmniRelay.DataPlane.Host.csproj" -c $CONFIG -r $RID -p:PublishAot=true -p:StripSymbols=true -p:PublishSingleFile=true -p:SelfContained=true -o (Join-Path $artifacts "dataplane-host-$RID")
    dotnet publish "$root/src/OmniRelay.ControlPlane.Host/OmniRelay.ControlPlane.Host.csproj" -c $CONFIG -r $RID -p:PublishAot=true -p:StripSymbols=true -p:PublishSingleFile=true -p:SelfContained=true -o (Join-Path $artifacts "controlplane-host-$RID")
    dotnet publish "$root/src/OmniRelay.Cli/OmniRelay.Cli.csproj" -c $CONFIG -r $RID -p:PublishAot=true -p:PublishSingleFile=true -p:SelfContained=true -o (Join-Path $artifacts "cli-$RID")
}

Write-Host "CI gate completed. Artifacts under $artifacts."
