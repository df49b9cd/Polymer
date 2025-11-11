# Diagnostics Reference

`GoDiagnostics` emits `System.Diagnostics.Metrics` instruments that you can export through OpenTelemetry or other meter providers. You can wire everything in manually with `GoDiagnostics.Configure(IMeterFactory, string meterName = "Hugo.Go")` or install the `Hugo.Diagnostics.OpenTelemetry` package and call `builder.AddHugoDiagnostics(...)` for Aspire-aligned defaults, schema-aware meters, activity sources, OTLP/Prometheus exporters, and rate-limited sampling.

## Quick navigation

- [Instruments](#instruments)
  - [Wait groups](#wait-groups)
  - [Result pipelines](#result-pipelines)
  - [Channel selectors](#channel-selectors)
  - [Task queues](#task-queues)
  - [Workflow execution](#workflow-execution)
- [Configuration options](#configuration-options)
- [Usage guidelines](#usage-guidelines)

> **Best practice:** Register diagnostics before you create channels, wait groups, or result pipelines—metrics only emit after instrumentation is configured.

## Instruments

### Wait groups

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `waitgroup.additions` | `Counter<long>` | operations | Incremented when `WaitGroup.Add` or `WaitGroup.Go` schedules work. |
| `waitgroup.completions` | `Counter<long>` | operations | Incremented when tracked work completes. |
| `waitgroup.outstanding` | `UpDownCounter<long>` | tasks | Tracks in-flight wait-group operations. |

### Result pipelines

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `result.successes` | `Counter<long>` | events | Number of successful `Result<T>` instances created. |
| `result.failures` | `Counter<long>` | events | Number of failed `Result<T>` instances, including `TapError` surfaces. |

### Channel selectors

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `channel.select.attempts` | `Counter<long>` | operations | Incremented before calling `Go.Select`/`Go.SelectAsync`. |
| `channel.select.completions` | `Counter<long>` | operations | Incremented when a select branch completes successfully. |
| `channel.select.timeouts` | `Counter<long>` | operations | Incremented when select operations hit their timeout budget. |
| `channel.select.cancellations` | `Counter<long>` | operations | Incremented when select operations observe cancellation tokens. |
| `channel.select.latency` | `Histogram<double>` | ms | Measures elapsed time spent inside `Go.SelectAsync`. |
| `channel.depth` | `Histogram<long>` | items | Samples backlog depth each time channel reads occur. |

### Task queues

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `taskqueue.enqueued` | `Counter<long>` | items | Number of work items enqueued. |
| `taskqueue.leased` | `Counter<long>` | leases | Number of leases granted by task queues. |
| `taskqueue.completed` | `Counter<long>` | leases | Number of leases completed successfully. |
| `taskqueue.failed` | `Counter<long>` | leases | Number of leases that failed. |
| `taskqueue.expired` | `Counter<long>` | leases | Number of leases expired without renewal. |
| `taskqueue.requeued` | `Counter<long>` | items | Number of work items requeued after failure or expiration. |
| `taskqueue.deadlettered` | `Counter<long>` | items | Number of work items routed to the dead-letter handler. |
| `taskqueue.heartbeats` | `Counter<long>` | leases | Number of heartbeat renewals issued. |
| `taskqueue.pending` | `UpDownCounter<long>` | items | Current number of pending work items awaiting leases. |
| `taskqueue.active` | `UpDownCounter<long>` | leases | Current number of active leases in flight. |
| `taskqueue.pending.depth` | `Histogram<long>` | items | Observed pending backlog depth around queue operations. |
| `taskqueue.active.depth` | `Histogram<long>` | leases | Observed active lease count around queue operations. |
| `taskqueue.lease.duration` | `Histogram<double>` | ms | Lease durations from grant to completion. |
| `taskqueue.heartbeat.extension` | `Histogram<double>` | ms | Durations granted by heartbeat renewals. |

### ResourceLease mesh instruments

The ResourceLease dispatcher and peer health subsystems emit additional meters for observability dashboards:

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `omnirelay.resourcelease.pending` | `Histogram<long>` | items | Pending SafeTaskQueue depth samples recorded on enqueue/dequeue. |
| `omnirelay.resourcelease.active` | `Histogram<long>` | leases | Active lease count samples. |
| `omnirelay.resourcelease.backpressure.transitions` | `Counter<long>` | events | Incremented whenever SafeTaskQueue backpressure toggles. |
| `omnirelay.peer.lease.healthy` | `ObservableGauge<long>` | peers | Number of peers currently considered healthy by `PeerLeaseHealthTracker`. |
| `omnirelay.peer.lease.unhealthy` | `ObservableGauge<long>` | peers | Number of peers marked unhealthy (missing heartbeats or disconnected). |
| `omnirelay.peer.lease.pending_reassignments` | `ObservableGauge<long>` | leases | Total pending reassignments caused by failures/requeues. |
| `omnirelay.resourcelease.replication.events` | `Counter<long>` | events | Replication events emitted by `ResourceLeaseReplicationEvent` publishers (tagged by `lease.event_type`, `rpc.peer`, `shard.id`). |
| `omnirelay.resourcelease.replication.lag` | `Histogram<double>` | ms | End-to-end lag between event timestamp and sink application (recorded by checkpointing sinks). |

Pair these metrics with the control-plane endpoints described under “Mesh Diagnostics + Tooling” to build dashboards for queue depth, peer health, replication lag, and backpressure state.

### Workflow execution

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `workflow.started` | `Counter<long>` | workflows | Number of workflow executions started. |
| `workflow.completed` | `Counter<long>` | workflows | Number of workflows that completed successfully. |
| `workflow.failed` | `Counter<long>` | workflows | Number of workflows that failed. |
| `workflow.canceled` | `Counter<long>` | workflows | Number of workflows that were canceled. |
| `workflow.terminated` | `Counter<long>` | workflows | Number of workflows terminated explicitly. |
| `workflow.active` | `UpDownCounter<long>` | workflows | Current number of active workflow executions. |
| `workflow.replay.count` | `Histogram<long>` | replays | Observed replay counts emitted per execution. |
| `workflow.logical.clock` | `Histogram<long>` | ticks | Observed logical clock position recorded at start/completion. |
| `workflow.duration` | `Histogram<double>` | ms | Execution duration from start to completion. |

Each workflow measurement includes metric tags for `workflow.namespace`, `workflow.id`, `workflow.run_id`, `workflow.task_queue`, and `workflow.schedule_*` identifiers when present. Distributed tracing leverages the same metadata through `Activity` tags so deterministic replay diagnostics correlate with traces.

## Configuration options

- Use `builder.AddHugoDiagnostics(options => ...)` (from `Hugo.Diagnostics.OpenTelemetry`) to register meters, activity sources, OTLP/Prometheus exporters, runtime instrumentation, and rate-limited sampling in one call. The helper mirrors Aspire ServiceDefaults and surfaces options for `ServiceName`, `OtlpEndpoint`, `OtlpProtocol`, `AddPrometheusExporter`, and sampling limits.
- Call `GoDiagnostics.Configure(IMeterFactory factory, string meterName = GoDiagnostics.MeterName)` during startup when you need manual control over meter creation (for example, non-OpenTelemetry providers). The helper applies the library version and telemetry schema URL automatically.
- Use `GoDiagnostics.Configure(Meter meter)` when DI already exposes a pre-built `Meter`.
- Create a schema-aware activity source with `GoDiagnostics.CreateActivitySource(string? name = GoDiagnostics.ActivitySourceName)` and optionally throttle spans via `GoDiagnostics.UseRateLimitedSampling(...)` when your workload emits high volumes of internal activities.
- Invoke `GoDiagnostics.Reset()` (typically in unit tests) to dispose existing meters before registering new ones.
- Pass `GrpcTelemetryOptions` to `GrpcInbound`/`GrpcOutbound` so the built-in logging/metrics interceptors attach automatically without bespoke interceptor wiring.
- `AddOmniRelayDispatcher` reuses the host’s `ILoggerFactory` for every inbound transport. Runtime logging toggles (`/omnirelay/control/logging`) immediately adjust HTTP middleware output, gRPC interceptors (e.g., `GrpcServerLoggingInterceptor`), and any custom logging inside registered procedures.
- Peer metrics emitted by `OmniRelay.Core.Peers` include `polymer.peer.inflight`, `polymer.peer.successes`, `polymer.peer.failures`, and `polymer.peer.lease.duration` (histogram). Pair these with the `/omnirelay/introspect` endpoint, which now surfaces per-peer success/failure counts and latency percentiles, to build health dashboards.
- Logging middleware and transport interceptors now attach request scopes (`rpc.request_id`, `rpc.peer`, activity id tags) so any structured log emitted during a call inherits trace-aware correlation metadata.

## OmniRelay Diagnostics Configuration

`AddOmniRelayDispatcher` understands a `diagnostics` section that wires OpenTelemetry exporters and runtime controls without additional code. Metrics default to enabled and are exported through OTLP and an optional Prometheus scrape endpoint hosted by every HTTP inbound.

```json
{
  "omnirelay": {
    "service": "gateway",
    "diagnostics": {
      "openTelemetry": {
        "enabled": true,
        "enableMetrics": true,
        "prometheus": {
          "enabled": true,
          "scrapeEndpointPath": "/omnirelay/metrics"
        },
        "otlp": {
          "enabled": true,
          "endpoint": "http://localhost:4317"
        }
      },
      "runtime": {
        "enableControlPlane": true,
        "enableLoggingLevelToggle": true,
        "enableTraceSamplingToggle": true
      }
    }
  }
}
```

- `openTelemetry.prometheus.enabled` exposes `/omnirelay/metrics`, which returns the Prometheus text format scraped directly from OpenTelemetry. gRPC inbounds do not host a scrape endpoint—plan to scrape the HTTP inbound even when the RPC traffic of interest flows over gRPC.
- `openTelemetry.otlp.*` configures the standard OTLP exporter (`protocol` defaults to gRPC; omit the endpoint to use the SDK default).
- `runtime` enables lightweight control-plane endpoints:
  - `GET /omnirelay/control/logging` returns `{ "minimumLevel": "Information" }`.
  - `POST /omnirelay/control/logging` accepts `{ "level": "Warning" }` (or `null` to reset) and updates `LoggerFilterOptions.MinLevel` on the fly.
  - `GET /omnirelay/control/tracing` reports the active sampling probability, and `POST /omnirelay/control/tracing` accepts `{ "probability": 0.25 }` to throttle new `Activity` creation in `RpcTracingMiddleware` unless an upstream span forces sampling.
- Sampling changes take effect immediately through `DiagnosticsRuntimeSampler`, which wraps the OpenTelemetry pipeline and honours parent/linked spans while applying the requested probability.

These endpoints appear alongside `/omnirelay/introspect` on every HTTP inbound when `runtime.enableControlPlane` is true.

### Control-plane quickstart

With the `appsettings.json` above and an OmniRelay HTTP inbound listening on `http://localhost:8080`, the following commands exercise the runtime controls end-to-end:

```bash
# Check the current minimum log level
curl http://localhost:8080/omnirelay/control/logging

# Raise log level to Warning
curl -X POST \
  -H 'Content-Type: application/json' \
  -d '{ "level": "Warning" }' \
  http://localhost:8080/omnirelay/control/logging

# Reset to the configuration default
curl -X POST \
  -H 'Content-Type: application/json' \
  -d '{ "level": null }' \
  http://localhost:8080/omnirelay/control/logging

# Inspect and adjust tracing sampling probability
curl http://localhost:8080/omnirelay/control/tracing
curl -X POST \
  -H 'Content-Type: application/json' \
  -d '{ "probability": 0.25 }' \
  http://localhost:8080/omnirelay/control/tracing

# Scrape Prometheus metrics exposed by the HTTP inbound
curl http://localhost:8080/omnirelay/metrics
```

> **Example:** `samples/Configuration.Server` enables both Prometheus scraping and runtime toggles purely through configuration and exposes the `/omnirelay/control/*` endpoints automatically on its HTTP inbound.

## Usage guidelines

- Register the meter before creating wait groups or channels to ensure counters record the full lifecycle.
- Surface deterministic workflow metadata (`changeId`, `version`, `stepId`) as log scopes or OTLP attributes so the `workflow.*` metrics can be pivoted by change rollouts.
- Add exporters (Console, OTLP, Prometheus) through your telemetry provider to ship metrics to backends.
- Combine with [Publish metrics to OpenTelemetry](../how-to/observe-with-opentelemetry.md) for a full setup walkthrough.
