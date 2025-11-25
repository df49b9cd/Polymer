#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
# Placeholder perf smoke: reuse dispatcher unit test assembly tagged for perf smokes if present.
# This keeps the gate lightweight; extend with BenchmarkDotNet harness later.

mkdir -p "$ROOT/artifacts/perf-smoke"
dotnet test "$ROOT/tests/OmniRelay.Dispatcher.UnitTests/OmniRelay.Dispatcher.UnitTests.csproj" \
  --filter Category=PerfSmoke \
  --no-build \
  --logger "trx;LogFileName=perf-smoke.trx" \
  --results-directory "$ROOT/artifacts/perf-smoke"
