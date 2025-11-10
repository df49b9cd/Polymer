# ResourceLease Mesh Demo

An end-to-end sample that highlights the OmniRelay ResourceLease RPC mesh feature set: durable replication, deterministic capture, peer health tracking, backpressure hooks, diagnostics control plane, and background workers that lease work through the canonical `resourcelease::*` procedures.

## What it hosts

| Component | Description |
| --- | --- |
| ResourceLease dispatcher | Runs on `http://127.0.0.1:7420/yarpc/v1` (service `resourcelease-mesh-demo`, namespace `resourcelease.mesh`). |
| Durable replication | `SqliteResourceLeaseReplicator` persists events to `mesh-data/replication.db` and mirrors them to an in-memory log served at `/demo/replication`. |
| Deterministic store | `SqliteDeterministicStateStore` captures effect ids in `mesh-data/deterministic.db`. |
| Backpressure hooks | `BackpressureAwareRateLimiter` + diagnostics listener keep the HTTP worker pool in sync with SafeTaskQueue backpressure. |
| Diagnostics endpoints | `/demo/lease-health`, `/demo/backpressure`, `/demo/replication`, `/demo/enqueue` (human-friendly helpers layered on top of `/omnirelay/control/*`). |
| Background services | `LeaseSeederHostedService` enqueues synthetic work; `LeaseWorkerHostedService` leases/heartbeats/complete/fail items to showcase replication + peer metrics. |

## Node roles

`meshDemo.roles` (array or comma-separated string) selects which pieces run inside the current process. By default every role is enabled so the demo behaves exactly like previous versions.

| Role | Responsibilities |
| --- | --- |
| `dispatcher` | Hosts the ResourceLease dispatcher, peer health tracker, backpressure hooks, deterministic capture, and durable replication. |
| `diagnostics` | Exposes `/demo/*` helper endpoints and the home page banner. Requires `dispatcher`. |
| `seeder` | Runs `LeaseSeederHostedService` which enqueues synthetic work items via HTTP calls. |
| `worker` | Runs `LeaseWorkerHostedService`. Launch multiple processes with different `workerPeerId` values to simulate a fleet of peers. |

Set roles via environment variable (`MESHDEMO_meshDemo__roles=dispatcher,worker`) or CLI overrides (`--meshDemo:roles=dispatcher,worker`). Values are case-insensitive and accept comma-separated lists.

## Distributed mesh quickstart

Run each command from the repository root in separate terminals. Update `rpcUrl` if the dispatcher is hosted elsewhere.

```bash
# 1) Dispatcher + diagnostics (control plane + RPC port 7420)
dotnet run --project samples/ResourceLease.MeshDemo -- \
  --meshDemo:roles=dispatcher,diagnostics \
  --meshDemo:rpcUrl=http://0.0.0.0:7420 \
  --urls=http://0.0.0.0:5158

# 2) Seeder (pushes synthetic leases into the dispatcher)
dotnet run --project samples/ResourceLease.MeshDemo -- \
  --meshDemo:roles=seeder \
  --meshDemo:rpcUrl=http://127.0.0.1:7420

# 3) Worker A
dotnet run --project samples/ResourceLease.MeshDemo -- \
  --meshDemo:roles=worker \
  --meshDemo:rpcUrl=http://127.0.0.1:7420 \
  --meshDemo:workerPeerId=mesh-worker-a

# 4) Worker B
dotnet run --project samples/ResourceLease.MeshDemo -- \
  --meshDemo:roles=worker \
  --meshDemo:rpcUrl=http://127.0.0.1:7420 \
  --meshDemo:workerPeerId=mesh-worker-b
```

Visit the diagnostics node (for example `http://localhost:5158/`) to watch replication logs, peer health, and backpressure as the workers lease and complete items. Point seeders/workers at remote dispatchers by changing their `rpcUrl`.

## Docker Compose mesh lab

Spin up the dispatcher, diagnostics helpers, seeders, workers, Prometheus, and Grafana with a single command from `samples/ResourceLease.MeshDemo`:

```bash
docker compose up --build
```

> **Note:** The included `Dockerfile` publishes the sample as a Linux `linux-x64` Native AOT binary, so the resulting containers start quickly and do not require the .NET runtime.

Services:

| Service | Description | Host ports |
| --- | --- | --- |
| `mesh-dispatcher` | Dispatcher + diagnostics role. Persists SQLite data to the `mesh-data` volume and exposes `/demo/*`, `/metrics`, and RPC port `7420`. | `7420`, `5158` |
| `mesh-seeder` | Seeds demo work items over HTTP. | _n/a_ (scraped via the mesh network) |
| `mesh-worker-a` / `mesh-worker-b` | Worker roles that lease/heartbeat/complete items. Scale out by running `docker compose up --build --scale mesh-worker-a=0 --scale mesh-worker=3` or by duplicating services. | _n/a_ |
| `prometheus` | Scrapes every node at `/metrics`. UI at `http://localhost:9090`. | `9090` |
| `grafana` | Pre-provisioned with the **ResourceLease Mesh Overview** dashboard (folder **OmniRelay**). UI at `http://localhost:3000` (admin/admin). | `3000` |

Dashboards highlight queue depth, active leases, replication throughput, peer health, HTTP traffic, and backpressure transitions by reading the OmniRelay meters exported via OpenTelemetry/Prometheus.

## Run it (single process)

```bash
dotnet run --project samples/ResourceLease.MeshDemo
```

Outputs:

- ResourceLease RPC endpoint: `http://127.0.0.1:7420/yarpc/v1`
- Diagnostics UI: `http://localhost:5158/` (default ASP.NET port; see console)

## Interact with the dispatcher

### Enqueue work via HTTP helper

```bash
curl -X POST http://localhost:5158/demo/enqueue \
  -H "Content-Type: application/json" \
  -d '{ "resourceType":"demo.order","resourceId":"external-cli","partitionKey":"tenant-42" }'
```

### Enqueue via OmniRelay CLI

```bash
omnirelay request \
  --transport http \
  --url http://127.0.0.1:7420/yarpc/v1 \
  --service resourcelease-mesh-demo \
  --procedure resourcelease.mesh::enqueue \
  --encoding application/json \
  --body '{"payload":{"resourceType":"demo.order","resourceId":"cli","partitionKey":"tenant-cli","payloadEncoding":"application/json","body":"eyJtZXNzYWdlIjoiY2xpIn0="}}'
```

### Inspect health + replication

```bash
curl http://localhost:5158/demo/lease-health      # PeerLeaseHealthTracker snapshot
curl http://localhost:5158/demo/backpressure      # Latest SafeTaskQueue backpressure signal
curl http://localhost:5158/demo/replication       # Recent replication events from SQLite
```

## Configuration

- `appsettings.json` (`meshDemo` section) controls:
  - `rpcUrl`: HTTP inbound for ResourceLease RPCs.
  - `dataDirectory`: where SQLite replication/deterministic files are stored.
  - `workerPeerId`: peer identifier used by the background worker.
  - `seederIntervalSeconds`: cadence for seeding demo work.
  - `roles`: enables one or more of `dispatcher`, `diagnostics`, `seeder`, `worker`.
- Override any value via environment variables prefixed with `MESHDEMO_` (e.g., `MESHDEMO_meshDemo__rpcUrl`, `MESHDEMO_meshDemo__roles=dispatcher,worker`).

## Concepts showcased

- Resource-neutral `ResourceLease*` contracts.
- Durable replication + deterministic stores without external dependencies (SQLite).
- `PeerLeaseHealthTracker` diagnostics exposed over HTTP.
- Backpressure-aware rate limiting via `BackpressureAwareRateLimiter` + `RateLimitingBackpressureListener`.
- CLI-friendly helper endpoints for drain/restore/introspection workflows.
- Background worker that exercises `resourcelease.mesh::{lease,heartbeat,complete,fail}` to demonstrate requeue + replication lag metrics.
