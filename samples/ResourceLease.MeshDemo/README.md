# Lakehouse Catalog Mesh Demo

An end-to-end sample that highlights how OmniRelay’s ResourceLease mesh can coordinate a fleet of lakehouse metadata catalog servers: catalog mutations are replicated through OmniRelay, peers apply schema commits deterministically, and operators can observe replication/backpressure/peer health in real time.

## What it hosts

| Component | Description |
| --- | --- |
| ResourceLease dispatcher | Runs on `http://127.0.0.1:7420/omnirelay/v1` (service `resourcelease-mesh-demo`, namespace `resourcelease.mesh`). |
| Durable replication | `SqliteResourceLeaseReplicator` persists events to `mesh-data/replication.db` and mirrors them to an in-memory log served at `/demo/replication`. |
| Deterministic store | `SqliteDeterministicStateStore` captures effect ids in `mesh-data/deterministic.db`. |
| Backpressure hooks | `BackpressureAwareRateLimiter` + diagnostics listener keep the HTTP worker pool in sync with SafeTaskQueue backpressure. |
| Diagnostics endpoints | `/demo/lease-health`, `/demo/backpressure`, `/demo/replication`, `/demo/catalogs`, `/demo/enqueue` (human-friendly helpers layered on top of `/omnirelay/control/*`). |
| Lakehouse catalog simulator | `LakehouseCatalogSeederHostedService` emits catalog operations (create table, alter schema, commit snapshot, vacuum); `LakehouseCatalogWorkerHostedService` applies them while reporting catalog state via `/demo/catalogs`. |

## Node roles

`meshDemo.roles` (array or comma-separated string) selects which pieces run inside the current process. By default every role is enabled so the demo behaves exactly like previous versions.

| Role | Responsibilities |
| --- | --- |
| `dispatcher` | Hosts the ResourceLease dispatcher, peer health tracker, backpressure hooks, deterministic capture, and durable replication. |
| `diagnostics` | Exposes `/demo/*` helper endpoints and the home page banner. Requires `dispatcher`. |
| `seeder` | Runs `LakehouseCatalogSeederHostedService` which enqueues catalog mutations (create table, alter schema, snapshot commits, vacuums). |
| `worker` | Runs `LakehouseCatalogWorkerHostedService`, applying catalog changes and updating the in-memory catalog snapshot. Launch multiple processes with different `workerPeerId` values to emulate metadata servers. |

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

Visit the diagnostics node (for example `http://localhost:5158/`) to watch replication logs, peer health, catalog snapshots (`/demo/catalogs`), and backpressure as catalog servers process mutations. Point seeders/workers at remote dispatchers by changing their `rpcUrl`.

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

### Grafana dashboard bundle

- The `grafana/dashboards/resourcelease-mesh.json` export ships a **ResourceLease Mesh Overview** board with production-oriented panels for queue depth (p50/p95/p99), replication lag, peer health, pending reassignments, lease signals, HTTP success/error rates, request latency, and retry churn.
- Dashboard variables (`$service`, `$role`, `$instance`) pivot every panel by `service_name`, `mesh_role`, and `instance` labels so you can focus on a single dispatcher, worker role, or the entire fleet.
- Provisioning manifests under `grafana/provisioning/*` pre-create the Prometheus datasource (`uid: mesh-prom`) and pin the dashboard into the **OmniRelay** folder when you run `docker compose up`.
- To bring the dashboard into an existing Grafana:
  1. Copy `grafana/dashboards/resourcelease-mesh.json` into your ops repo (or use Grafana’s “Import dashboard” flow).
  2. Update the datasource UID if your Prometheus instance does not use `mesh-prom`.
  3. Make sure your Prometheus scrape config adds `service_name`, `mesh_role`, and `instance` labels (the demo’s `prometheus.yml` shows one option via static labels).
  4. Optionally reuse the provisioning snippets provided in `grafana/provisioning/` to auto-install the datasource + dashboard during infrastructure rollout.

## Run it (single process)

```bash
dotnet run --project samples/ResourceLease.MeshDemo
```

Outputs:

- ResourceLease RPC endpoint: `http://127.0.0.1:7420/omnirelay/v1`
- Diagnostics UI: `http://localhost:5158/` (default ASP.NET port; see console)

## Interact with the dispatcher

### Enqueue catalog operations via HTTP helper

```bash
curl -X POST http://localhost:5158/demo/enqueue \
  -H "Content-Type: application/json" \
  -d '{
        "catalog":"fabric-lakehouse",
        "database":"sales",
        "table":"orders",
        "operation":"CommitSnapshot",
        "version":42,
        "principal":"spark-cli"
      }'
```

### Enqueue via OmniRelay CLI

```bash
omnirelay request \
  --transport http \
  --url http://127.0.0.1:7420/omnirelay/v1 \
  --service resourcelease-mesh-demo \
  --procedure resourcelease.mesh::enqueue \
  --encoding application/json \
  --body '{cd
    "payload":{
      "resourceType":"lakehouse.catalog",
      "resourceId":"fabric-lakehouse.sales.orders.v0042",
      "partitionKey":"fabric-lakehouse",
      "payloadEncoding":"application/json",
      "body":"eyJjYXRhbG9nIjogImZhYnJpYy1sYWtlaG91c2UiLCAiZGF0YWJhc2UiOiAic2FsZXMiLCAidGFibGUiOiAib3JkZXJzIiwgIm9wZXJhdGlvblR5cGUiOiAiQ29tbWl0U25hcHNob3QiLCAidmVyc2lvbiI6IDQyLCAicHJpbmNpcGFsIjogInNwYXJrLWNsaSIsICJjb2x1bW5zIjogWyJpZCBTVFJJTkciLCAicGF5bG9hZCBTVFJJTkciXSwgImNoYW5nZXMiOiBbImNvbW1pdCBzbmFwc2hvdCBmcm9tIENMSSJdLCAic25hcHNob3RJZCI6ICJjbGktc25hcHNob3QiLCAidGltZXN0YW1wIjogIjIwMjQtMDEtMDFUMDA6MDA6MDBaIiwgInJlcXVlc3RJZCI6ICJjbGkifQ=="
    }
  }'
```

### Inspect health + replication

```bash
curl http://localhost:5158/demo/lease-health      # PeerLeaseHealthTracker snapshot
curl http://localhost:5158/demo/backpressure      # Latest SafeTaskQueue backpressure signal
curl http://localhost:5158/demo/replication       # Recent replication events from SQLite
curl http://localhost:5158/demo/catalogs          # Current lakehouse catalog snapshot
```

## Generate load with the OmniRelay CLI benchmark

Use the OmniRelay CLI’s `benchmark` command (often shortened to “bench”) to synthesize enqueue traffic and light up the Grafana dashboards.

1. **Expose the CLI** (from the repo root):

   ```bash
   # Option 1: run in-place via dotnet run
   alias omnirelay='dotnet run --project src/OmniRelay.Cli/OmniRelay.Cli.csproj --'
   # Option 2: install the packaged tool once and use it everywhere
   # dotnet tool install --global OmniRelay.Cli
   ```

2. **Create a sample enqueue payload** (feel free to tweak catalog/database/table ids):

   ```bash
   cat > bench-payload.json <<'JSON'
   {
     "payload": {
       "resourceType": "lakehouse.catalog",
       "resourceId": "fabric-lakehouse.sales.orders.v0042",
       "partitionKey": "fabric-lakehouse",
       "payloadEncoding": "application/json",
       "body": "eyJjYXRhbG9nIjogImZhYnJpYy1sYWtlaG91c2UiLCAiZGF0YWJhc2UiOiAic2FsZXMiLCAidGFibGUiOiAib3JkZXJzIiwgIm9wZXJhdGlvblR5cGUiOiAiQ29tbWl0U25hcHNob3QiLCAidmVyc2lvbiI6IDQyLCAicHJpbmNpcGFsIjogInNwYXJrLWNsaSIsICJjb2x1bW5zIjogWyJpZCBTVFJJTkciLCAicGF5bG9hZCBTVFJJTkciXSwgImNoYW5nZXMiOiBbImNvbW1pdCBzbmFwc2hvdCBmcm9tIENMSSJdLCAic25hcHNob3RJZCI6ICJjbGktc25hcHNob3QiLCAidGltZXN0YW1wIjogIjIwMjQtMDEtMDFUMDA6MDA6MDBaIiwgInJlcXVlc3RJZCI6ICJjbGkifQ=="
     }
   }
   JSON
   ```

3. **Run the benchmark** (adjust `--rps`, `--concurrency`, or `--duration` to shape load):

   ```bash
   omnirelay benchmark \
     --transport http \
     --url http://127.0.0.1:7420/omnirelay/v1 \
     --service resourcelease-mesh-demo \
     --procedure resourcelease.mesh::enqueue \
     --encoding application/json \
     --body-file bench-payload.json \
     --concurrency 16 \
     --duration 45s \
     --warmup 5s \
     --requests 0 \
     --rps 150
   ```

The command issues sustained enqueue requests against the dispatcher, causing pending depth, active leases, replication throughput, and retry panels to react within a few seconds. Point `--url` at any remote dispatcher endpoint when exercising a distributed deployment.

## Configuration

- `appsettings.json` (`meshDemo` section) controls:
  - `rpcUrl`: HTTP inbound for ResourceLease RPCs.
  - `dataDirectory`: where SQLite replication/deterministic files are stored.
  - `workerPeerId`: peer identifier used by the background worker.
  - `seederIntervalSeconds`: cadence for injecting catalog operations.
  - `roles`: enables one or more of `dispatcher`, `diagnostics`, `seeder`, `worker`.
  - `urls`: optional array of ASP.NET Core listener URLs (for example `["http://0.0.0.0:5158"]`). When omitted, the default ASP.NET Core port selection applies. Override via `--meshDemo:urls:0=` or `MESHDEMO_meshDemo__urls__0`.
  - `catalogs`, `databasePrefixes`, `principals`: seed data for catalog names, database prefixes, and committing principals. Override to mirror your own topology (e.g., `MESHDEMO_meshDemo__catalogs__0=fabric-prod`).
- Override any value via environment variables prefixed with `MESHDEMO_` (e.g., `MESHDEMO_meshDemo__rpcUrl`, `MESHDEMO_meshDemo__roles=dispatcher,worker`).

## Concepts showcased

- Resource-neutral `ResourceLease*` contracts.
- Durable replication + deterministic stores without external dependencies (SQLite).
- `PeerLeaseHealthTracker` diagnostics exposed over HTTP.
- Backpressure-aware rate limiting via `BackpressureAwareRateLimiter` + `RateLimitingBackpressureListener`.
- CLI-friendly helper endpoints for drain/restore/introspection workflows.
- Background worker that exercises `resourcelease.mesh::{lease,heartbeat,complete,fail}` to demonstrate requeue + replication lag metrics.
