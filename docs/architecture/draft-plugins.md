# draft-plugins

Purpose: enumerate every OmniRelay surface that should remain provider-agnostic so we can swap first-party modules with third-party infrastructure without rewrites. Each capability lists the contract we expose and representative external providers. Integrations must stay Native AOT–friendly, avoid reflection, and wrap all I/O in Hugo Channels + Result pipelines (with `ResultExecutionPolicy` for retries/backpressure) instead of throwing exceptions.

## Design principles
- Stable contracts: keep interface/DTO shapes small and versioned; prefer protocol-neutral contracts (Protobuf/JSON) and capability flags to preserve compatibility.
- Trim-friendly: avoid heavy SDKs; prefer minimal HTTP/gRPC clients, generated models, and source generators so connectors stay AOT-compatible.
- Safe failures: all plugin calls return `Result<T>`; no business exceptions. Apply timeouts, circuit breakers, and compensation via Hugo result pipelines.
- Observable by default: emit OTel spans/metrics/logs using Hugo telemetry adapters so operators can compare provider performance.
- Configuration over code: all provider choices come from configuration + dependency injection wiring; samples/runbooks should document the swap steps.

## Pluggable capability catalog

| Capability | OmniRelay contract | Example external providers | Notes |
| --- | --- | --- | --- |
| Identity management (AuthN) | OIDC token validation, client credential flows, workload identity bootstrap for mTLS | Auth0, Azure Entra ID, Okta, Active Directory/AD FS/LDAP, Google IAM | Support multi-tenant issuers; cache JWKS; align claims to OmniRelay authorization graph. |
| Certificate & secret management | Issue/renew leaf certs, store private keys, rotate secrets, expose trust bundles | Azure Key Vault, HashiCorp Vault, AWS KMS/Secrets Manager, GCP Secret Manager, SPIFFE/SPIRE CA | Keep CSR/CA flows pluggable; enforce short-lived certs; store only references in config. |
| Service discovery / registry | Register endpoints, health metadata, routing/shard data | HashiCorp Consul, etcd, ZooKeeper, Istio/Linkerd service registry, AWS Cloud Map, GCP Service Directory | Support watch/stream semantics; tolerate eventual consistency; decouple from topology strategies. |
| Service topology strategies | Routing graph (star, bus, mesh, ring), leader/shard assignment policies | Istio/Envoy control planes, Linkerd, AWS App Mesh, GCP Service Mesh | Expose topology as policy; allow providers to own traffic shaping while OmniRelay enforces policy validation. |
| Observability (metrics/logs/traces) | Emit OTel signals, exemplars, alerts hooks | OpenTelemetry Collector, Prometheus + Alertmanager, Grafana Cloud, Datadog, New Relic, Elastic APM, Jaeger/Zipkin | Keep exporters pluggable; support push/pull; budget-friendly sampling per provider. |
| Messaging & eventing | Publish/subscribe, ordered streams, exactly-once/at-least-once knobs | Apache Kafka/Redpanda, RabbitMQ, NATS/JetStream, Azure Service Bus, AWS SNS/SQS, GCP Pub/Sub | Abstract producer/consumer via Hugo channels; surface idempotency keys and dead-letter policies. |
| API gateway / edge | Request ingress, authN/Z enforcement, request shaping, TLS termination | Envoy, Kong, Traefik, AWS API Gateway, Azure API Management, GCP API Gateway/ESPv2, NGINX Plus | Keep OmniRelay edge filters minimal; allow external gateway ownership while retaining tracing headers and auth contracts. |
| Feature flags & experiments | Boolean/variant flags, experiment assignment, telemetry hooks | LaunchDarkly, Azure App Configuration, AWS AppConfig, Unleash, Statsig, Flipt | Evaluate flags locally with cached rules; avoid blocking I/O on hot paths. |
| Distributed config / KV | Dynamic configuration fetch, watch/notify, strongly or eventually consistent stores | Consul KV, etcd, ZooKeeper, Redis, AWS SSM Parameter Store, GCP Config Controller | Provide typed binding + validation; support offline defaults. |
| Caching | Key/value and object caching with eviction + TTL | Redis (OSS/Azure/AWS), Memcached, Aerospike | Keep serialization pluggable; expose backpressure and cache stampede protection. |
| Job scheduling & orchestration | Durable schedules, retries, sagas/long-running workflows | Hangfire, Quartz.NET, Temporal/Cadence, Apache Airflow, AWS Step Functions, Azure Durable Functions | Map to Hugo result pipelines for retries/compensation; ensure idempotent handlers. |
| Policy/authorization (AuthZ) | ABAC/RBAC/relationship-based checks, policy evaluation | Open Policy Agent (OPA), Cedar (AWS), Permit.io, Permify, OpenFGA | Keep data-plane policy checks sidecar/local where possible; cache decisions with TTL + audit logs. |
| Data storage (operational) | CRUD, transactions, multi-tenant partitioning | PostgreSQL, SQL Server, MySQL/MariaDB, MongoDB, Cosmos DB, Couchbase, DynamoDB, Firestore | Use provider-neutral repository contracts; keep migrations separate per provider; prefer parameterized SQL and batching for AOT. |
| Schema / contract governance | Schema registration/compatibility for events and RPC | Confluent Schema Registry, Azure Event Hubs Schema Registry, Apicurio, Redpanda Console | Validate compatibility at publish/deploy time; expose per-tenant registries where needed. |
| Release strategies | Progressive delivery: canary, blue/green, traffic mirroring | Argo Rollouts, Flagger (Istio/Linkerd), Spinnaker, LaunchDarkly experiments | Bind to topology + gateway plugins; enforce automated rollback policies. |
| Traffic shaping & rate limiting | Quotas, token buckets, surge protection, WAF hooks | Envoy RateLimit Service, Redis-backed leaky bucket, Istio mixer/wasm filters, Cloudflare/fastly edge limits | Keep enforcement near edge; expose budget telemetry; fail closed for unsafe paths. |
| Object and artifact storage (optional) | Binary/blob storage for payloads, artifacts, and configs | AWS S3, Azure Blob Storage, GCP Cloud Storage, MinIO | Stream via Hugo channels; enforce checksum and encryption-at-rest controls. |

## Current adapters (as of 2025-11-25)

| Capability | In-repo adapter(s) | Gaps vs catalog |
| --- | --- | --- |
| Identity management (AuthN) | SPIFFE issuer `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/SpiffeWorkloadIdentityProvider.cs`; file-based issuer `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/FileBootstrapIdentityProvider.cs`; bootstrap token service/server/client `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/BootstrapTokenService.cs`, `BootstrapServer.cs`, `BootstrapClient.cs`; in-process CA `src/OmniRelay.ControlPlane/Core/Identity/CertificateAuthorityService.cs`; principal binding middleware `src/OmniRelay.DataPlane/Core/Middleware/PrincipalBindingMiddleware.cs` | No OIDC/OAuth token validators; no external IdP connectors (Auth0/Entra/Okta/AD/LDAP); no JWKS cache/rotation adapters. |
| Certificate & secret management | Internal CA + TLS cache `src/OmniRelay.DataPlane/Security/TransportTlsManager.cs`; agent cert manager `src/OmniRelay.ControlPlane/Core/Agent/AgentCertificateManager.cs`; bootstrap identity providers above | No Azure Key Vault / HashiCorp Vault / AWS KMS/Secrets Manager / GCP Secret Manager providers; no HSM/KMS signing adapters. |
| Service discovery / registry | Mesh gossip + leadership `src/OmniRelay.ControlPlane/Core/Gossip/MeshGossipHost.cs`, `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs`; control-plane APIs `src/OmniRelay.ControlPlane/Core/Shards/ControlPlane/ShardControlPlaneService.cs`, `ShardControlGrpcService.cs`; repositories `src/OmniRelay.ShardStore.Relational/RelationalShardStore.cs`, `src/OmniRelay.ShardStore.Postgres/PostgresShardStoreFactory.cs`, `src/OmniRelay.ShardStore.Sqlite/SqliteShardStoreFactory.cs`, `src/OmniRelay.ShardStore.ObjectStorage/ObjectStorageShardStore.cs`, `src/OmniRelay.ShardStore.ObjectStorage/InMemoryShardObjectStorage.cs` | No Consul/etcd/ZooKeeper/Istio/Cloud Map connectors; no watch-based external registry ingestion. |
| Service topology strategies | Shard hashing/topology strategies `src/OmniRelay.ControlPlane/Core/Shards/Hashing/RendezvousShardHashStrategy.cs`, `RingShardHashStrategy.cs`, `LocalityAwareShardHashStrategy.cs`; strategy registry `src/OmniRelay.ControlPlane/Core/Shards/Hashing/ShardHashStrategyRegistry.cs` | No plugins that delegate topology to external meshes (Istio/App Mesh/Linkerd); no policy-to-provider mapping layer. |
| Observability (metrics/logs/traces) | OpenTelemetry/Prometheus exporters `src/OmniRelay.Diagnostics.Telemetry/OmniRelayTelemetryExtensions.cs`; runtime sampler `src/OmniRelay.Diagnostics.Runtime/DiagnosticsRuntimeSampler.cs`; logging defaults `src/OmniRelay.Diagnostics.Logging/OmniRelayLoggingExtensions.cs`; probes/chaos endpoints `src/OmniRelay.Diagnostics.Probes/OmniRelayProbesExtensions.cs`; alerting via webhook channel/publisher `src/OmniRelay.Diagnostics.Alerting/WebhookAlertChannel.cs`, `AlertPublisher.cs`; docs/metadata endpoints `src/OmniRelay.Diagnostics.Documentation/OmniRelayDocumentationExtensions.cs` | No vendor-specific exporters (Datadog/NewRelic/Elastic) beyond OTLP; no log shipping adapters (e.g., Loki/Elastic). |
| Messaging & eventing | None | Kafka/Redpanda/RabbitMQ/NATS/Azure Service Bus/AWS SNS-SQS/GCP Pub/Sub adapters absent. |
| API gateway / edge | In-proc HTTP/3+gRPC transports `src/OmniRelay.DataPlane/Transport/Http/HttpInbound.cs`, `HttpOutbound.cs`, `src/OmniRelay.DataPlane/Transport/Grpc/GrpcInbound.cs`, `GrpcOutbound.cs`; transport policy evaluator `src/OmniRelay.DataPlane/Dispatcher/Config/TransportPolicy.cs` | No Envoy/Kong/Traefik/API Gateway front-door adapters; no external rate-limit/WAF integration. |
| Feature flags & experiments | None | No LaunchDarkly/Azure App Configuration/AWS AppConfig/Unleash/Statsig adapters; no local rule engine. |
| Distributed config / KV | None | No Consul KV/etcd/ZooKeeper/Redis/SSM Parameter Store connectors; no watcher pipeline. |
| Caching | None | No Redis/Memcached/Aerospike adapters; no cache stampede controls. |
| Job scheduling & orchestration | None | No Hangfire/Quartz.NET/Temporal/Airflow/Step Functions/Durable Functions integration. |
| Policy/authorization (AuthZ) | Mesh authorization policy + gRPC interceptor `src/OmniRelay.DataPlane/Security/Authorization/MeshAuthorizationEvaluator.cs`, `MeshAuthorizationGrpcInterceptor.cs`; transport security policy `src/OmniRelay.DataPlane/Transport/Security/TransportSecurityPolicyEvaluator.cs`; bootstrap policy `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/BootstrapPolicyEvaluator.cs`; principal binding middleware `src/OmniRelay.DataPlane/Core/Middleware/PrincipalBindingMiddleware.cs` | No OPA/Cedar/Permit.io/Permify/OpenFGA adapters; no remote PDP/PEP bridge. |
| Data storage (operational) | Relational shard store `src/OmniRelay.ShardStore.Relational/RelationalShardStore.cs`; Postgres factory `src/OmniRelay.ShardStore.Postgres/PostgresShardStoreFactory.cs`; SQLite factory `src/OmniRelay.ShardStore.Sqlite/SqliteShardStoreFactory.cs`; object/in-memory shard repos `src/OmniRelay.ShardStore.ObjectStorage/ObjectStorageShardStore.cs`, `src/OmniRelay.ShardStore.ObjectStorage/InMemoryShardObjectStorage.cs`; replication sinks `src/OmniRelay.ResourceLeaseReplicator.Sqlite/SqliteResourceLeaseReplicator.cs`, `src/OmniRelay.ResourceLeaseReplicator.ObjectStorage/ObjectStorageResourceLeaseReplicator.cs`, `src/OmniRelay.ResourceLeaseReplicator.Grpc/GrpcResourceLeaseReplicator.cs` | No Cosmos/Mongo/MySQL/Dynamo/Firestore providers; object storage repo lacks S3/Blob/GCS concrete adapters. |
| Schema / contract governance | Codegen/proto toolchain `src/OmniRelay.Codegen.Protobuf.*` | No schema registry integration (Confluent/Apicurio/Event Hubs); no compatibility enforcement pipeline. |
| Release strategies | None | No Argo Rollouts/Flagger/Spinnaker hooks; no blue-green/canary controller integration. |
| Traffic shaping & rate limiting | None | No Envoy RateLimit/Redis leaky-bucket/Istio mixer/edge provider adapters. |
| Object and artifact storage | Resource lease object-store abstraction `src/OmniRelay.ResourceLeaseReplicator.ObjectStorage/IResourceLeaseObjectStore.cs`; shard object storage abstraction `src/OmniRelay.ShardStore.ObjectStorage/IShardObjectStorage.cs`; in-memory shard object storage `src/OmniRelay.ShardStore.ObjectStorage/InMemoryShardObjectStorage.cs`; object-storage-backed shard repo `src/OmniRelay.ShardStore.ObjectStorage/ObjectStorageShardStore.cs`; replication via object store `src/OmniRelay.ResourceLeaseReplicator.ObjectStorage/ObjectStorageResourceLeaseReplicator.cs` | No concrete S3/Azure Blob/GCS/MinIO implementations; no checksum/encryption plumbing. |

## Data-plane aligned architecture direction

- Planes with strict contracts: control plane (topology, discovery, identity bootstrap, policy/config snapshots), data plane (hot-path transports, authN/Z, retries/backpressure, telemetry emit), plugin plane (adapters wired via DI/modules, never hot-path blocking calls).
- Canonical interfaces (versioned DTOs, Protobuf/JSON): `IdentityProvider`, `SecretStore`, `RegistryClient`, `TopologyStrategy`, `RateLimiter`, `CacheClient`, `ConfigClient`, `FlagEvaluator`, `MessageBus`, `SchemaRegistry`, `GatewayAdapter`; data plane speaks only to these abstractions, providers live in `OmniRelay.Plugins.*`.
- Policy-first wiring: data plane consumes signed snapshots (transport policy, authZ policy, rate-limit budgets, retry/timeout defaults) pushed by control plane; no per-request control-plane RPCs.
- Topology & discovery: control plane produces atomic `RoutingSnapshot`; external registries (Consul/CloudMap/etc.) sync into control plane only—data plane never calls them directly.
- Identity & mTLS: default SPIFFE/in-process CA; add key-vault/vault/KMS signers behind `IWorkloadIdentityProvider` + `ICertificateCache`. Data plane reads short-lived certs from local cache; renewal handled by control-plane agent.
- Edge vs mesh: keep in-proc HTTP/gRPC lean; optional front-door adapters (Envoy/Kong/API GW) must preserve tracing/auth headers and emit consistent `RequestMeta`.
- Observability: OTel core stays; exporters are plugins. Enforce per-provider sampling/rate budgets via snapshots; log shipping runs off hot path.
- Resilience defaults: every plugin interface declares mandatory `ResultExecutionPolicy` (timeouts, retries, circuit breaker) tuned for Native AOT; transport middleware applies cheapest applicable policy.
- Packaging suggestion: `OmniRelay.Abstractions` (contracts), `OmniRelay.DataPlane`, `OmniRelay.ControlPlane`, `OmniRelay.Plugins.*` (per provider), `OmniRelay.Tests.Conformance` (per-capability contract suites).
- Rollout: start with external Consul registry + S3/Blob object store + Auth0/Entra OIDC to validate contracts/snapshots; second wave covers rate limiting, caching, messaging, feature flags.

## Implementation approach (first-party now, swap-ready later)

- Build first-party providers that satisfy each canonical interface under `OmniRelay.Plugins.Internal.*` (e.g., internal registry, internal CA, in-memory/object storage, built-in topology strategies). These ship as defaults to keep friction low.
- Keep all interfaces in `OmniRelay.Abstractions` and inject via DI; data-plane packages depend only on abstractions, never on concrete providers or external SDKs.
- External providers live in isolated packages `OmniRelay.Plugins.<Provider>` with minimal deps and AOT-safe SDK choices; they implement the same conformance suites.
- Configuration selects providers by key; control plane produces signed snapshots that include provider keys and settings so data-plane swaps are atomic and reversible.
- Enforce replaceability gates: no provider-specific types cross public surfaces; all provider errors must map to `Result<T>` with standardized error codes/metadata.
- Compatibility policy: N/N-1 for contracts; feature flags around new providers; snapshot capability flags prevent older data-plane nodes from loading unsupported adapters.
- Testing: each provider must pass capability conformance tests plus chaos/hardening suites; defaults stay in CI gates; third-party adapters can be optional builds.

## Candidates to extract into plugin packages

| Capability | Current location | Proposed package | Notes |
| --- | --- | --- | --- |
| Internal registry & shard stores | `src/OmniRelay.ShardStore.Relational/*`, `src/OmniRelay.ShardStore.Postgres/*`, `src/OmniRelay.ShardStore.Sqlite/*`, `src/OmniRelay.ShardStore.ObjectStorage/*` | `OmniRelay.Plugins.Internal.Registry` (with subpackages `...Relational`, `...Postgres`, `...Sqlite`, `...ObjectStorage`) | Keep `IShardRepository` + DTOs in `OmniRelay.Abstractions`; move storage-specific factories/adapters here. |
| Gossip/leadership implementation | `src/OmniRelay.ControlPlane/Core/Gossip/*`, `src/OmniRelay.ControlPlane/Core/LeadershipCoordinator.cs` | `OmniRelay.Plugins.Internal.Mesh` | Control-plane remains owner of contracts; mesh runtime becomes swap-ready if an external mesh is later wrapped. |
| Identity/CA providers | `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/SpiffeWorkloadIdentityProvider.cs`, `FileBootstrapIdentityProvider.cs`, `Core/Identity/CertificateAuthorityService.cs`, `DataPlane/Security/TransportTlsManager.cs`, `ControlPlane/Core/Agent/AgentCertificateManager.cs` | `OmniRelay.Plugins.Internal.Identity` | Keep `IWorkloadIdentityProvider`, `ICertificateCache` abstractions in `OmniRelay.Abstractions`; move concrete issuers/cache implementations here. |
| Observability defaults | `src/OmniRelay.Diagnostics.Telemetry/*`, `Diagnostics.Logging/*`, `Diagnostics.Probes/*`, `Diagnostics.Alerting/*`, `Diagnostics.Documentation/*` | `OmniRelay.Plugins.Internal.Observability` | Leaves telemetry contracts in `OmniRelay.Abstractions.Diagnostics`; exporters/channel impls become replaceable. |
| Transport policies & in-proc edge | `src/OmniRelay.DataPlane/Transport/*`, `Dispatcher/Config/TransportPolicy.cs` | `OmniRelay.Plugins.Internal.Transport` | Data-plane should depend on `ITransportAdapter` abstractions; current HTTP/gRPC adapters move here while keeping hot-path core minimal. |
| Replication sinks | `src/OmniRelay.ResourceLeaseReplicator.Sqlite/*`, `...ObjectStorage/*`, `...Grpc/*` | `OmniRelay.Plugins.Internal.Replication` | Contracts (`IResourceLeaseReplicator`, sinks, events) stay in `OmniRelay.Abstractions`. |
| Shard hashing/topology strategies | `src/OmniRelay.ControlPlane/Core/Shards/Hashing/*` | `OmniRelay.Plugins.Internal.Topology` | Keep strategy contracts + IDs in abstractions; make strategies swappable (ring, rendezvous, locality-aware) to allow external meshes/routers later. |
| AuthZ/transport policy evaluators | `src/OmniRelay.DataPlane/Security/Authorization/*`, `src/OmniRelay.DataPlane/Transport/Security/*`, `src/OmniRelay.DataPlane/Dispatcher/Config/TransportPolicy.cs` | `OmniRelay.Plugins.Internal.Authorization` | Enables replacement by OPA/Cedar/OpenFGA/edge rate-limit PDPs; keep policy contracts in abstractions. |
| Bootstrap replay/attestation helpers | `src/OmniRelay.ControlPlane/ControlPlane/Bootstrap/IBootstrapReplayProtector.cs`, `InMemoryBootstrapReplayProtector.cs`, attestation hooks in `BootstrapPolicyEvaluator` | `OmniRelay.Plugins.Internal.Bootstrap` | Allows swapping replay protection/attestation stores (Redis/KV) without touching bootstrap server. |
| Alert channels | `src/OmniRelay.Diagnostics.Alerting/*` (webhook, throttler, publisher) | `OmniRelay.Plugins.Internal.Alerting` | Keeps alert contracts stable while enabling PagerDuty/Slack/Teams/Email channels later. |
| Extension hosts (DSL/Wasm/Native) | `src/OmniRelay.DataPlane/Core/Extensions/*` | `OmniRelay.Plugins.Internal.Extensions` | Keeps extension contracts in abstractions; lets us plug different sandboxes/runtimes or disable entirely for AOT footprint. |

## Plugin loading boundaries (who owns what)

| Capability | Loaded by data plane | Loaded by control plane | Notes |
| --- | --- | --- | --- |
| Transport adapters (HTTP/gRPC), rate-limiters, retries | ✅ | ⬜ | Hot path only; configured via snapshots produced by control plane. |
| AuthN (token validation) & AuthZ enforcement | ✅ | ⬜ | Data plane enforces; control plane distributes policies/keys. |
| Identity bootstrap (CA/SPIFEE/KMS signers) | ⬜ | ✅ | Control plane issues/renews certs; data plane consumes cached material only. |
| Service registry ingestion/sync | ⬜ | ✅ | Control plane owns watches into Consul/Cloud Map/etc.; publishes routing snapshots. |
| Topology strategies (hashing, ring/rendezvous) | ⬜ | ✅ | Control plane computes assignments; data plane applies snapshots. |
| Observability exporters (OTLP/Prometheus/vendor) | ✅ | ⬜ | Data plane exports spans/metrics; control plane may emit its own but not required for data-plane traffic. |
| Config/flag evaluators | ✅ | ⬜ | Evaluated in data plane; config/flag definitions come from control plane snapshots. |
| Caching & distributed cache clients | ✅ | ⬜ | Data plane caches responses/state; control plane unaffected. |
| Messaging/event bus producers/consumers | ✅ | ⬜ | Data plane adapters drive pub/sub; control plane may host admin UIs separately. |
| Replication sinks (lease/shard replication) | ✅ | ⬜ | Runs near data-plane workers; control plane coordinates but doesn’t host sinks. |
| Alert channels | ✅ | ✅ | Both planes may emit alerts; keep channels reusable. |
| Bootstrap replay/attestation stores | ⬜ | ✅ | Applies to control-plane bootstrap endpoints only. |
| Extension hosts (Wasm/DSL/Native) | ✅ | ⬜ | Executed in data plane; control plane should not load extension runtimes. |

## Control-plane ⇄ Data-plane interface (what flows, how)

- **Bootstrap**: data plane nodes join via the control-plane bootstrap API (`BootstrapServer`), receive a signed workload identity bundle (SPIFFE/CA) and bootstrap config seed. All responses are `Result<T>`; errors carry codes + metadata for observability.
- **Snapshot delivery (pull or push)**: control plane emits signed, versioned snapshots containing routing/topology (`RoutingSnapshot`), transport policy, authZ policy, feature/config flags, rate-limit budgets, and exporter configs. Delivery options: (a) data plane pulls via gRPC/HTTP control API; (b) control plane pushes over a streaming channel (preferred for freshness). Snapshots apply atomically and expose capability flags for N/N-1 compatibility.
- **Registry/topology sync**: control plane ingests external registries and computes assignments; data plane only ingests the synthesized snapshot (no direct registry calls). Shard/hash strategies remain opaque to data plane beyond IDs + assigned routes.
- **Identity refresh**: control plane renews certs/keys and publishes new bundles; data plane TLS managers read from local cache (file/memory) and hot-reload without blocking traffic.
- **Policy enforcement path**: policies (authZ, transport, retry/backpressure, rate limits) are resolved in control plane, serialized into snapshots, and enforced in data-plane middleware. No per-request control-plane RPCs on hot paths.
- **Telemetry/health feedback**: data plane streams health/metrics back to control plane via lightweight channels (OTLP or bespoke gRPC) so control plane can drive rebalancing/alerts. Feedback uses bounded channels with `ResultExecutionPolicy` for backpressure.
- **Change safety**: snapshots include monotonic version + signature; data plane validates signature/trust root and rolls back to last-good on failure. Capability flags gate loading of providers not supported by the current binary.
- **Error model**: all control/data exchanges use Hugo `Result<T>`; business exceptions are avoided. Timeouts and retries follow per-channel `ResultExecutionPolicy` baked into the snapshot.

## System architecture (draft)

- **Planes**
  - Control plane: bootstrap API, registry/topology compiler, policy compiler, snapshot publisher, certificate/identity services, health aggregation.
  - Data plane: transport gateways (HTTP/3, gRPC), authN/Z middleware, routing executor, retry/backpressure, caching, telemetry emitters, replication sinks.
  - Plugin plane: provider packages (`OmniRelay.Plugins.*`) that implement canonical interfaces; loaded into either control or data plane per the boundary table above.
  - Abstractions: provides abstractions.

- **Runtime components (control plane)**
  - Bootstrap service (`BootstrapServer`) issues workload identities and seeds config.
  - Registry sync adapters (future Consul/Cloud Map/etcd) ingest endpoints; merged into routing graph.
  - Topology compiler (hash strategies) produces `RoutingSnapshot` with shard/leader assignments.
  - Policy compiler merges authZ, transport, retry, rate-limit, feature/config into signed snapshots.
  - Snapshot publisher exposes pull (gRPC/HTTP) and push (stream) channels with monotonic versions and signatures.
  - Certificate authority + signer providers (SPIFFE/internal CA; future Vault/KMS) mint and rotate trust material.
  - Control-plane telemetry feeds SLOs and alert channels.

- **Runtime components (data plane)**
  - Hot-path transports (HTTP/3, gRPC) with authN/Z middleware and transport security enforcement.
  - Routing executor consumes `RoutingSnapshot` to select peers/topology strategy.
  - Plugin adapters for cache, config/flags, messaging, rate limiting, schema registry, etc., behind canonical interfaces.
  - Replication sinks (lease/shard) and observability exporters (OTLP/Prometheus/vendor) run off the hot path with bounded channels.
  - TLS manager loads cached certs and hot-reloads without blocking requests.

- **Configuration & rollout**
  - Single declarative config ingested by control plane; validated against policy; compiled into signed snapshot.
  - Data plane atomically swaps snapshots; supports dual snapshots for canary/blue-green via weights in routing metadata.
  - Capability flags in snapshots prevent incompatible plugins from loading; N/N-1 compatibility maintained.

- **Security & safety**
  - mTLS everywhere; trust roots delivered via bootstrap; cert renewal handled by control plane.
  - All cross-plane calls use Hugo `Result<T>` with `ResultExecutionPolicy` for timeout/retry/backpressure; no business exceptions.
  - Snapshots are signed and versioned; last-good rollback on validation failure.

- **Performance & AOT**
  - Minimal dependencies in data plane; plugin packages choose AOT-safe SDKs; no reflection-heavy serializers.
  - Generated clients, pooled HttpClient, zero per-request allocation for policies/serializers.

- **Deployment shape**
  - Control plane can run as HA trio (3–5 replicas) with persistent store for registry state.
  - Data plane scales horizontally; no cross-node coordination on hot path beyond snapshot application and peer health signals.


## Usage guidance
- Keep OmniRelay defaults first-party where it simplifies developer ergonomics, but ensure every contract supports external adapters through DI modules and configuration files under `samples/` and `docs/reference/hugo` patterns.
- Provide provider-specific runbooks and thin adapters rather than forking pipelines; prioritize minimal dependencies to remain AOT-ready.
- Add conformance tests per capability (contract-level) so any provider adapter must pass the same suite before shipping.
- Surface provider choice and health in diagnostics (CLI and `/control/*` endpoints) to make swaps observable and reversible.
