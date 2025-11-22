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

All TaskQueue instruments emit a `taskqueue.name` tag. When the `Hugo.TaskQueues.Diagnostics` package is configured, additional tags such as `service.name`, `taskqueue.shard`, and any custom enrichers from `TaskQueueMetricsOptions` are applied to every measurement.

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

## TaskQueue diagnostics package

`Hugo.TaskQueues.Diagnostics` builds on the primitives above to make TaskQueue instrumentation turnkey:

- `TaskQueueMetricsOptions` toggles `TaskQueueMetricGroups` and configures tag enrichers (`service.name`, shards, tenant tags, etc.).
- `TaskQueueActivityOptions` mirrors the .NET 10 sampling guidance so TaskQueue spans honor rate limits by default.
- `IMeterFactory.AddTaskQueueDiagnostics(...)` wires both options in a single call and returns a `TaskQueueDiagnosticsRegistration` (or use the `IServiceProvider` extension).

```csharp
await using var registration = meterFactory.AddTaskQueueDiagnostics(options =>
{
    options.MeterName = "OmniRelay.Dispatcher";
    options.Metrics.ServiceName = "omnirelay-control-plane";
    options.Metrics.DefaultShard = Environment.GetEnvironmentVariable("REGION");
    options.Metrics.EnabledGroups = TaskQueueMetricGroups.QueueDepth | TaskQueueMetricGroups.Backpressure;
});
```

For control-plane endpoints, instantiate `TaskQueueDiagnosticsHost`, attach your `TaskQueueBackpressureMonitor<T>` and `TaskQueueReplicationSource<T>` instances via `Attach(...)`, then stream `TaskQueueDiagnosticsEvent` values over HTTP/gRPC. The sample at `samples/Hugo.TaskQueueDiagnosticsHostSample/Program.cs` exposes a `/diagnostics/taskqueue` SSE endpoint that CLI tooling (for example `omnirelay control watch backpressure`) can subscribe to with zero custom plumbing.

## Usage guidelines

- Register the meter before creating wait groups or channels to ensure counters record the full lifecycle.
- Surface deterministic workflow metadata (`changeId`, `version`, `stepId`) as log scopes or OTLP attributes so the `workflow.*` metrics can be pivoted by change rollouts.
- Add exporters (Console, OTLP, Prometheus) through your telemetry provider to ship metrics to backends.
- Combine with [Publish metrics to OpenTelemetry](../how-to/observe-with-opentelemetry.md) for a full setup walkthrough.
