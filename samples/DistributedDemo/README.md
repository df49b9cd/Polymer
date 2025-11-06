# Distributed Demo Sample

This sample spins up a small OmniRelay topology with Docker Compose:

- **Gateway** exposes HTTP and gRPC inbounds, accepts JSON checkout requests, calls downstream gRPC services with Protobuf codecs, and fans out audit events over HTTP.
- **Inventory** runs two replicas (primary / secondary) to exercise different peer-chooser strategies (`fewest-pending`). Each instance exposes Prometheus metrics and responds with Protobuf payloads.
- **Audit** consumes HTTP oneway calls using a JSON codec and logs order events.
- **OpenTelemetry Collector** receives OTLP exports and exposes a Prometheus endpoint for collected metrics.
- **Prometheus** scrapes gateway + services + collector.

## Prerequisites

- Docker + Docker Compose
- .NET 10 SDK if you want to build/run the services locally outside containers

## Build & Run

From the repository root:

```bash
cd samples/DistributedDemo
docker compose build
docker compose up
```

Exposed ports:

- Gateway HTTP: `http://localhost:5080`
- Gateway gRPC: `http://localhost:5081`
- Prometheus UI: `http://localhost:9090`
- OpenTelemetry collector Prometheus exporter: `http://localhost:9464/metrics`
- Grafana UI: `http://localhost:3000` (default credentials `admin` / `admin`)

Send a checkout request:

```bash
curl -X POST \
  -H "Content-Type: application/json" \
  -d '{ "orderId": "order-1", "sku": "widget", "quantity": 2, "currency": "USD" }' \
  http://localhost:5080/yarpc/v1/checkout::create
```

### Quick RPC Smoke Tests

Use these commands to confirm the gateway, inventory, and audit services are reachable once the stack is running.

```bash
# HTTP direct call with curl (JSON request / response)
curl -X POST \
  -H "Content-Type: application/json" \
  -H "Rpc-Procedure: checkout::create" \
  -H "Rpc-Caller: smoke-test" \
  -d '{ "orderId": "smoke-1", "sku": "widget", "quantity": 2, "currency": "USD" }' \
  http://localhost:5080/yarpc/v1/checkout::create

# OmniRelay CLI request over HTTP (wrap CLI args after --)
dotnet run \
  --project src/OmniRelay.Cli/OmniRelay.Cli.csproj -- \
  request \
  --transport http \
  --url http://localhost:5080/yarpc/v1/checkout::create \
  --service distributed.gateway \
  --procedure checkout::create \
  --caller smoke-http \
  --encoding application/json \
  --body '{ "orderId": "smoke-2", "sku": "widget", "quantity": 2, "currency": "USD" }'

# OmniRelay CLI request over gRPC
dotnet run \
  --project src/OmniRelay.Cli/OmniRelay.Cli.csproj -- \
  request \
  --transport grpc \
  --address http://localhost:5081 \
  --service distributed.gateway \
  --procedure checkout::create \
  --caller smoke-grpc \
  --encoding application/json \
  --body '{ "orderId": "smoke-3", "sku": "widget", "quantity": 2, "currency": "USD" }'
```

### Benchmark with the OmniRelay CLI

`dotnet run` automatically builds the CLI on the fly. Add the `--` separator before CLI arguments so they reach the tool.

Key flags used below:
- `--concurrency` controls the number of parallel workers.
- `--duration` and `--warmup` bound the measurement window.
- `--requests` caps the total measured calls (`0` disables the cap so the test ends when `--duration` elapses).
- `--rps` throttles aggregate throughput, which is useful to avoid overloading local machines.
- `--body` accepts templated placeholders (`{{worker}}`, `{{seq}}`) expanded per worker and request.

```bash
# HTTP (port 5080)
dotnet run \
  --project src/OmniRelay.Cli/OmniRelay.Cli.csproj -- \
  benchmark \
  --transport http \
  --url http://localhost:5080/yarpc/v1/checkout::create \
  --service distributed.gateway \
  --procedure checkout::create \
  --caller bench-http \
  --encoding application/json \
  --body '{ "orderId": "bench-{{worker}}-{{seq}}", "sku": "widget", "quantity": 2, "currency": "USD" }' \
  --concurrency 16 \
  --requests 0 \
  --duration 30s \
  --warmup 5s \
  --rps 200

# gRPC (port 5081)
dotnet run \
  --project src/OmniRelay.Cli/OmniRelay.Cli.csproj -- \
  benchmark \
  --transport grpc \
  --address http://localhost:5081 \
  --service distributed.gateway \
  --procedure checkout::create \
  --caller bench-grpc \
  --encoding application/json \
  --body '{ "orderId": "bench-{{worker}}-{{seq}}", "sku": "widget", "quantity": 2, "currency": "USD" }' \
  --concurrency 16 \
  --requests 0 \
  --duration 30s \
  --warmup 5s \
  --rps 200
```

The gateway uses JSON inbound codecs, calls inventory via a Protobuf outbound codec, and publishes a JSON oneway audit event. Inventory replicas demonstrate a different peer chooser (`fewest-pending`), so traffic will alternate between `inventory-primary` and `inventory-secondary` as they compete for leases.

## Observability Stack

- Each service exposes `/omnirelay/metrics` for Prometheus scraping.
- The gateway is configured (via `appsettings.json`) to export traces/metrics to the OpenTelemetry collector at `otel-collector:4317`.
- The collector logs traces to stdout and re-exports OTLP metrics to Prometheus at port `9464`.
- Grafana is pre-provisioned with a Prometheus datasource and an "OmniRelay Demo Overview" dashboard (folder **OmniRelay**).

You can open the Prometheus UI and run queries such as `yarpcore_rpc_requests_total` to see gateway + inventory RPC activity.

## Project Layout

- `Gateway` – Generic Host + `AddOmniRelayDispatcher` configuration-driven setup.
- `Inventory` – Manual dispatcher wiring with Protobuf codecs and console middleware.
- `Audit` – Minimal HTTP oneway service using JSON codecs.
- `Shared` – Shared contracts (JSON records + generated Protobuf messages).
- `docker-compose.yml` – Defines the entire stack including OpenTelemetry + Prometheus.
- `prometheus.yml`, `otel-collector-config.yaml` – Observability configuration.

Stop the stack with `docker compose down`. Rebuild after local changes with `docker compose build`.
