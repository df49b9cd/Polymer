#!/usr/bin/env bash
set -euo pipefail

ROOT="$(git rev-parse --show-toplevel)"
PACK_OUT="$ROOT/artifacts/packages"
PROJECTS=(
  "$ROOT/src/OmniRelay.Protos/OmniRelay.Protos.csproj"
  "$ROOT/src/OmniRelay.Codecs/OmniRelay.Codecs.csproj"
  "$ROOT/src/OmniRelay.Transport/OmniRelay.Transport.csproj"
  "$ROOT/src/OmniRelay.ControlPlane.Abstractions/OmniRelay.ControlPlane.Abstractions.csproj"
)

mkdir -p "$PACK_OUT"

for proj in "${PROJECTS[@]}"; do
  echo "Building $proj (Release)"
  dotnet build "$proj" -c Release --nologo
  echo "Packing $proj"
  dotnet pack "$proj" \
    -c Release \
    --no-build \
    -p:GenerateSbom=true \
    -p:SbomPackageFormats=spdxjson%3bcyclonedxjson \
    -p:PackageOutputPath="$PACK_OUT" \
    "$@"
done

echo "Packages + SBOMs written to $PACK_OUT"
