#!/usr/bin/env bash
set -euo pipefail
ROOT="$(git rev-parse --show-toplevel)"
# Placeholder perf smoke: reuse dispatcher unit test assembly tagged for perf smokes if present.
# This keeps the gate lightweight; extend with BenchmarkDotNet harness later.

dotnet test "$ROOT/tests/OmniRelay.Dispatcher.UnitTests/OmniRelay.Dispatcher.UnitTests.csproj" --filter Category=PerfSmoke --no-build
