#!/usr/bin/env bash

set -euo pipefail

CONFIGURATION="${1:-Release}"

projects=(
  "tests/OmniRelay.FeatureTests/OmniRelay.FeatureTests.csproj"
  "tests/OmniRelay.IntegrationTests/OmniRelay.IntegrationTests.csproj"
  "tests/OmniRelay.HyperscaleFeatureTests/OmniRelay.HyperscaleFeatureTests.csproj"
)

echo "Running hyperscale smoke tests (Configuration=${CONFIGURATION})"
for project in "${projects[@]}"; do
  echo "==> dotnet test ${project}"
  dotnet test "$project" -c "$CONFIGURATION" --logger "console;verbosity=normal"
done

echo "Hyperscale smoke tests completed"
