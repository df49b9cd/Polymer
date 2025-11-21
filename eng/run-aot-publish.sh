#!/usr/bin/env bash

set -euo pipefail

RID="${1:-linux-x64}"
case "$RID" in
  linux-x64|linux-arm64|osx-x64|osx-arm64) ;;
  *)
    echo "Unsupported RID: $RID. Supported: linux-x64, linux-arm64, osx-x64, osx-arm64"
    exit 1
    ;;
esac
CONFIG="${2:-Release}"
ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT_ROOT="$ROOT_DIR/artifacts/aot/${RID}"

projects=(
  "tests/OmniRelay.MeshKit.AotSmoke/OmniRelay.MeshKit.AotSmoke.csproj"
  "src/OmniRelay.Cli/OmniRelay.Cli.csproj"
)

props=(
  "-p:PublishAot=true"
  "-p:TrimMode=full"
  "-p:TreatWarningsAsErrors=true"
  "-p:WarningsNotAsErrors=IL3053"
  "-p:InvariantGlobalization=true"
  "-p:StripSymbols=false"
  "-p:DisableParallelAot=true"
  "--self-contained"
  "true"
)

echo "Running Native AOT publish for RID=${RID}, Configuration=${CONFIG}"
mkdir -p "$OUT_ROOT"

for project in "${projects[@]}"; do
  name="$(basename "${project%.*}")"
  out_dir="${OUT_ROOT}/${name}"
  echo "==> Publishing ${project} to ${out_dir}"
  dotnet publish "$ROOT_DIR/$project" -c "$CONFIG" -r "$RID" "${props[@]}" -o "$out_dir"
done

echo "Native AOT publishes completed. Outputs are under $OUT_ROOT"
