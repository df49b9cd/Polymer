# OmniRelay Perf Baseline & SLOs

## Targets (initial)
- InProc: p99 added latency ≤ 300µs for unary RPC echo @ 10k RPS; alloc ≤ 0.5 KB/op.
- Sidecar: p99 added latency ≤ 600µs for unary RPC echo @ 8k RPS; alloc ≤ 0.7 KB/op.
- Edge: p99 added latency ≤ 900µs for unary RPC echo @ 6k RPS; alloc ≤ 0.9 KB/op.

## Measurement plan
- Use perf smoke (lightweight) after tests via `EnablePerfGate=true` + `eng/run-perf-smoke.sh` (placeholder until BenchmarkDotNet harness is added).
- Full benchmarks (to be added) will capture: throughput, latency distribution, alloc rate, CPU %, and HTTP/3 vs HTTP/2 impact.

## Artifacts
- SBOM and packages in `artifacts/packages` (for dependency transparency).
- Perf baselines will be captured under `artifacts/perf/` once the harness is wired.

## Next steps
- Add BenchmarkDotNet-based perf suite covering unary/streaming HTTP & gRPC per mode.
- Automate comparison vs stored baselines and fail on regression (WORK-002C gate).
