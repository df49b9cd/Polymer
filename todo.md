# HTTP/3 Parity TODO

## Infrastructure & Platform

- [ ] Guarantee every runtime environment meets QUIC prerequisites (Windows 11/Server 2022 or Linux with `libmsquic` ≥ 2.2, TLS 1.3 capable certs).
  - [ ] Inventory OS versions and runtime images across prod, staging, and dev clusters.
  - [ ] Add CI/CD guardrails that block deployments on unsupported OS or missing QUIC features.
  - [ ] Publish matrix of compliant environments in platform docs.
- [ ] Ensure certificate provisioning pipelines allow TLS 1.3 cipher suites (`TLS_AES_*`, `TLS_CHACHA20_*`) and rotate any legacy certs that fail QUIC handshakes.
  - [ ] Audit current certificate inventory for incompatible cipher policies.
  - [ ] Update issuance templates / CSR automation to request TLS 1.3 compatible suites.
  - [ ] Implement automated TLS probe that verifies QUIC handshake success per environment.
- [ ] Automate `libmsquic` installation for supported Linux distros in provisioning scripts and document verification steps.
  - [ ] Author distro-specific install scripts or Ansible/Chef modules pinning supported `libmsquic` versions.
  - [ ] Integrate installation into base image build or node bootstrap workflows.
  - [ ] Add runtime startup check that emits an alert when `libmsquic` is missing or the wrong version.
- [ ] Confirm Kubernetes ingress / load balancers / CDNs can forward UDP/QUIC traffic on required ports; document any appliances that terminate HTTP/3.
  - [ ] Map network topology for each environment and identify all edge components.
  - [ ] Execute vendor-specific tests to validate HTTP/3 pass-through (include ALB/NGINX/Envoy/CDN configurations).
  - [ ] Capture results and mitigation steps for components without QUIC support.
- [ ] Verify edge devices emit `alt-svc` headers unchanged and that UDP 443 is opened wherever TCP 443 is already allowed.
  - [ ] Add synthetic HTTP/2 probe that checks for expected `alt-svc` responses end-to-end.
  - [ ] Update firewall / security group automation to include UDP 443 and audit existing rulesets.
  - [ ] Configure monitoring alert when `alt-svc` headers are stripped or modified upstream.
- [ ] Open firewall rules for UDP alongside existing TCP listeners; add health checks for dropped QUIC packets.
  - [ ] Update firewall change requests and infrastructure-as-code templates for UDP allowances.
  - [ ] Implement periodic QUIC ping/echo health checks to detect packet loss or blocks.
  - [ ] Document validation steps for operators post-change.
- [ ] Update deployment runbooks to include QUIC troubleshooting commands (`ss -u`, `netsh ... excludedportrange`, etc.).
  - [ ] Expand runbook pages with Linux/Windows command examples and expected outputs.
  - [ ] Schedule knowledge-sharing session with SRE/on-call teams covering new procedures.
  - [ ] Add quick-reference checklist for incident responders.

## Kestrel Listener Configuration

- [ ] Add an HTTP/3 feature flag in transport configuration (opt-in per listener and environment).
  - [x] Define configuration schema/CLI flags for enabling HTTP/3 per endpoint.
  - [x] Implement runtime toggle that falls back to existing HTTP/1.1+HTTP/2 behavior by default.
  - [ ] Document configuration usage and defaults in operator guide.
- [ ] Update `HttpInbound` listeners to use `HttpProtocols.Http1AndHttp2AndHttp3` and validate TLS 1.3 availability before enabling HTTP/3.https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot?view=aspnetcore-9.0#configure-grpc-client-to-use-http3
  - [x] Modify listener setup to request multi-protocol support and add TLS capability checks.
  - [ ] Add integration tests covering HTTP/1.1, HTTP/2, and HTTP/3 negotiation.
  - [ ] Gate feature behind the new configuration flag.
- [ ] Update `GrpcInbound` listeners to allow HTTP/3 (ensure MsQuic handshake succeeds when TLS callbacks are configured).
  - [x] Adjust listener options to include HTTP/3 and audit TLS callbacks for unsupported settings.
  - [ ] Verify MsQuic handshake success with existing interceptors and auth flows.
  - [ ] Add regression tests ensuring HTTP/2-only deployments remain unaffected.
- [ ] Confirm endpoint configuration sets `ListenOptions.Protocols` and `EnableAltSvc` equivalents so HTTP/3 advertises correctly across all bindings.
  - [ ] Audit all current listener definitions for missing protocol declarations or alt-svc settings.
  - [x] Add validation to startup logging highlighting misconfigured endpoints.
  - [ ] Update configuration samples/templates to include the correct options.
- [ ] Surface MsQuic-specific tuning knobs (connection idle timeout, stream limits, keep-alive) via runtime options with sensible defaults.
  - [ ] Design configuration structure mapping MsQuic options to OmniRelay runtime settings.
  - [ ] Implement wiring in Http/Grpc inbound/outbound components.
  - [ ] Provide documentation describing recommended defaults and tuning guidance.
- [ ] Ensure graceful shutdown/drain logic works for QUIC transports and still signals retry metadata (`retry-after`) consistently.
  - [ ] Extend draining logic tests to cover HTTP/3 requests/calls.
  - [ ] Validate `retry-after` headers/metadata appear for QUIC clients.
  - [ ] Update runbooks describing graceful shutdown expectations under HTTP/3.

## Feature Parity: HTTP Surface

- [ ] Verify `/omnirelay/introspect`, `/healthz`, `/readyz`, and other existing HTTP endpoints work over HTTP/3 and remain backward compatible with HTTP/1.1/2.
  - [ ] Build automated test suite hitting endpoints over all supported protocols.
  - [ ] Ensure observability endpoints continue to function without additional client configuration.
  - [ ] Record any protocol-specific limitations and create mitigation issues if required.
- [ ] Revisit WebSocket usage in `HttpInbound`: document that classic WebSockets stay on HTTP/1.1 and confirm duplex scenarios have an HTTP/3-friendly alternative (e.g., HTTP/3 streams or keep HTTP/1.1 fallback).
  - [ ] Review current WebSocket consumers and identify ones needing QUIC-compatible alternatives.
  - [ ] Prototype HTTP/3 streaming fallback or document mandatory HTTP/1.1 usage where appropriate.
  - [ ] Publish guidance for service teams on choosing between WebSockets and HTTP/3 streams.
- [ ] Confirm streaming/duplex pipelines (pipe-based framing, large payload support) handle QUIC flow control limits; tune frame size when necessary.
  - [ ] Execute load tests emphasizing large payloads and bidirectional streaming over QUIC.
  - [ ] Adjust frame sizing or buffering heuristics if MsQuic flow control stalls appear.
  - [ ] Document recommended streaming limits and alert thresholds.
- [ ] Ensure request/response size limits, header validation, and timeouts map to QUIC semantics; add tests for limits enforcement under packet loss.
  - [ ] Cross-check Kestrel and MsQuic limit mappings for parity with HTTP/1.1/2 settings.
  - [ ] Create chaos tests injecting packet loss to confirm timeouts behave as expected.
  - [ ] Update configuration defaults or documentation where behavior differs.
- [ ] Validate error handling and status mapping (including retry headers) behaves identically when requests fall back from HTTP/3 to HTTP/2/1.1.
  - [ ] Simulate downgrade scenarios and confirm error payloads/headers match existing contracts.
  - [ ] Add automated regression tests covering fallback paths.
  - [ ] Align logging/metrics labels so operators can correlate errors across protocols.

## Feature Parity: gRPC Surface

- [ ] Validate gRPC interceptors, compression providers, and telemetry hooks operate identically when the transport upgrades to HTTP/3.
  - [ ] Run interceptor pipeline tests using HTTP/3-enabled test harnesses.
  - [ ] Verify compression negotiation and message sizes behave the same as HTTP/2 baseline.
  - [ ] Confirm telemetry spans/logs from interceptors include protocol data.
- [ ] Update `GrpcOutbound`/gRPC-dotnet client construction to opt into HTTP/3 per https://learn.microsoft.com/en-us/aspnet/core/grpc/troubleshoot?view=aspnetcore-9.0#configure-grpc-client-to-use-http3 and expose configuration for disabling when peers lack QUIC.
  - [ ] Implement HTTP/3-aware channel factory with opt-in flags and fallback logic.
  - [ ] Expose configuration through appsettings/CLI and ensure defaults remain HTTP/2 for compatibility.
  - [ ] Add unit tests verifying correct handler configuration for each mode.
- [ ] Confirm client handlers set `HttpRequestMessage.VersionPolicy = RequestVersionOrHigher` (or stricter policies) and tune `SocketsHttpHandler` HTTP/3 settings (keep-alive, connection pooling) so call concurrency matches HTTP/2 parity targets.
  - [ ] Audit existing handler construction and update to set version policies explicitly.
  - [ ] Validate connection pooling and keep-alive tuning under load using benchmark suite.
  - [ ] Document recommended handler overrides for high-concurrency workloads.
- [ ] Exercise server keep-alive settings under HTTP/3 and expose MsQuic keep-alive knobs alongside current HTTP/2 configuration.
  - [ ] Add configuration binding for MsQuic keep-alive options on the server side.
  - [ ] Stress test long-lived streams verifying keep-alive behavior.
  - [ ] Update operational docs describing keep-alive tuning for HTTP/3 deployments.
- [ ] Confirm gRPC health checks and draining semantics still produce `StatusCode.Unavailable` with retry metadata.
  - [ ] Run end-to-end drain tests capturing gRPC status codes over HTTP/3.
  - [ ] Ensure metadata serialization matches existing HTTP/2 expectations.
  - [ ] Capture results in SLO/SLA documentation.
- [ ] Capture and document any gRPC client library limitations (per language) when connecting over HTTP/3.
  - [ ] Survey officially supported gRPC client libraries for HTTP/3 readiness.
  - [ ] File follow-up issues for unsupported clients or document required workarounds.
  - [ ] Publish compatibility table for consumers.
- [ ] Update protobuf code generation templates to emit HTTP/3-aware client/channel wiring (e.g., default `GrpcChannelOptions` with HTTP/3 enabled).
  - [ ] Modify generator templates and runtime helpers to surface HTTP/3 configuration hooks.
  - [ ] Update generated sample projects/tests to cover HTTP/3 channel creation.
  - [ ] Document generator options and migration guidance.

## Outbound Calls & YARP Integrations

- [ ] Allow `HttpOutbound` and other client stacks to request HTTP/3 (`HttpVersion.Version30` or `HttpVersionPolicy.RequestVersionOrHigher`) with configurable fallback.
  - [ ] Introduce configuration knobs for outbound transports choosing desired HTTP version policy.
  - [ ] Implement fallback strategy when upstream HTTP/3 negotiation fails.
  - [ ] Add integration tests covering all version modes.
- [ ] Extend service discovery / routing metadata to prefer HTTP/3 endpoints when available and handle negotiation failures cleanly.
  - [ ] Update discovery schema to record per-endpoint protocol support.
  - [ ] Modify routing logic to prioritize HTTP/3 while retaining HTTP/2/1.1 fallback.
  - [ ] Ensure telemetry records chosen protocol per request for debugging.
- [ ] Verify `GrpcOutbound` channel pooling, circuit breaker, and retry logic behave with HTTP/3-only endpoints and surface telemetry when peers fall back to HTTP/2.
  - [ ] Run resilience tests under failure scenarios (transport errors, handshake failures).
  - [ ] Confirm circuit breaker metrics capture HTTP/3-specific failure modes.
  - [ ] Adjust retry policies if QUIC introduces different transient error patterns.
- [ ] Verify upstream proxies and service meshes accept HTTP/3 from OmniRelay; document any services limited to HTTP/2/1.1.
  - [ ] Coordinate with platform teams to enable HTTP/3 ingress/egress configs in meshes (Envoy/Istio/etc.).
  - [ ] Execute smoke tests for representative upstream services.
  - [ ] Document exceptions and required configuration per service team.
- [ ] Update CLI commands (`serve`, `bench`, etc.) to expose HTTP/3 flags and ensure generated configuration includes HTTP/3 endpoints when enabled.
  - [x] Add CLI options and help text describing HTTP/3 usage.
  - [ ] Ensure CLI-generated configs/appsettings include HTTP/3 toggles.
  - [ ] Add integration tests for CLI workflows that enable HTTP/3.

## Compatibility & Rollout

- [ ] Build protocol negotiation health checks that alert when HTTP/3 fails and traffic reverts to HTTP/2/1.1 beyond an acceptable threshold.
  - [ ] Define acceptable fallback percentage and alert thresholds.
  - [ ] Implement health check job leveraging telemetry data.
  - [ ] Wire alerts into existing on-call paging systems.
- [ ] Provide runtime configuration switches to disable HTTP/3 per listener/service without redeploying binaries.
  - [ ] Expose dynamic config (feature flags or admin API) that toggles HTTP/3.
  - [ ] Ensure toggle propagates safely across distributed instances.
  - [ ] Document operational procedure for emergency rollback.
- [ ] Document rollout sequencing (staging → canary → production) including verification of `alt-svc` propagation and UDP reachability at each phase.
  - [ ] Draft phased rollout plan with required verification gates.
  - [ ] Align release calendar with dependent teams and schedule canaries.
  - [ ] Publish checklist and success metrics for each rollout stage.

## Observability & Telemetry

- [ ] Add structured logging for QUIC connection lifecycle (handshake failures, congestion, migration) and integrate with existing metrics.
  - [ ] Instrument MsQuic/Kestrel events and forward them to logging pipeline.
  - [ ] Create dashboards visualizing key QUIC lifecycle metrics.
  - [ ] Train on-call staff on interpreting new logs/metrics.
- [ ] Emit protocol-level metrics (per-protocol request counts, handshake RTT) so we can compare HTTP/3 vs HTTP/2/1.1 performance.
  - [ ] Update metrics instrumentation to tag protocol version on requests.
  - [ ] Add percentile latency/throughput views split by protocol.
  - [ ] Integrate metrics into performance regression alerts.
- [ ] Wire up distributed tracing spans to capture HTTP/3 attributes (QUIC connection id, protocol version) for correlation.
  - [ ] Extend tracing middleware to annotate spans with protocol metadata.
  - [ ] Verify tracing exporters handle added attributes without schema drift.
  - [ ] Update tracing documentation/examples to highlight the new fields.
- [ ] Track fallback rates (HTTP/3 → HTTP/2/1.1) and QUIC-specific failure codes in dashboards to monitor parity drift.
  - [ ] Capture fallback events in logging/metrics pipeline.
  - [ ] Build dashboards with historical trend lines for fallback and failure codes.
  - [ ] Establish alerting thresholds for unacceptable fallback frequency.

## Testing & Validation

- [ ] Add integration tests that exercise HTTP/3 requests using `HttpClient` with `RequestVersionOrHigher` and validate fallback to HTTP/2 when QUIC is disabled.
  - [ ] Implement test fixtures spinning up HTTP/3-enabled Kestrel instances.
  - [ ] Write tests covering success, fallback, and failure paths.
  - [ ] Integrate tests into CI matrix (Windows/Linux).
- [ ] Add gRPC client/server integration tests that force HTTP/3 (using gRPC-dotnet HTTP/3 configuration) and assert interceptor behavior, deadlines, streaming semantics, and metadata parity.
  - [ ] Create gRPC test harness exercising unary/streaming calls over HTTP/3.
  - [ ] Validate interceptors and deadlines behave identically to HTTP/2 baseline.
  - [ ] Add assertions for metadata and status code parity.
- [ ] Update protobuf-generated service tests to cover HTTP/3 call paths and ensure generated clients honor HTTP/3 configuration defaults.
  - [ ] Regenerate sample services with updated templates.
  - [ ] Add tests that instantiate generated clients configured for HTTP/3.
  - [ ] Verify fallbacks to HTTP/2 remain functional when HTTP/3 is disabled.
- [ ] Run soak/performance tests focusing on high connection churn, packet loss, and mobile-like IP migration scenarios.
  - [ ] Expand load test scripts to include QUIC-specific scenarios (packet loss, connection migration).
  - [ ] Monitor resource utilization and latency to establish new baselines.
  - [ ] Document findings and adjust configuration limits where needed.
- [ ] Include automated canary validation (curl `--http3`, browser devtools) in deployment pipelines to confirm `alt-svc` advertisement and successful upgrades.
  - [ ] Add pipeline steps that issue HTTP/3 requests via CLI and browser automation.
  - [ ] Fail pipeline when upgrades do not occur or `alt-svc` missing.
  - [ ] Store canary results for auditing.
- [ ] Test downgrade scenarios (server disables HTTP/3 mid-flight, UDP blocked, mismatched ALPN) to prove resiliency and useful error messages.
  - [ ] Implement chaos test scripts introducing downgrade conditions.
  - [ ] Capture client/server behavior and verify user-facing errors remain actionable.
  - [ ] File follow-up work for any unacceptable degradations.
- [ ] Document a rollback strategy that forces listeners back to HTTP/1.1/2 if HTTP/3 issues arise.
  - [ ] Write rollback runbook including configuration toggles and validation steps.
  - [ ] Conduct game-day exercise to practice rollback procedure.
  - [ ] Ensure monitoring quiets after rollback before closing incident.

## Documentation & Support

- [ ] Update developer docs to explain how to enable HTTP/3 locally (Windows vs. macOS limitations) and in staging/production.
  - [ ] Draft documentation sections covering prerequisites, enabling steps, and troubleshooting.
  - [ ] Add code samples demonstrating HTTP/3 configuration for inbound/outbound paths.
  - [ ] Review docs with dev rel and publish updates.
- [ ] Provide FAQ covering common QUIC troubleshooting findings (ALPN mismatch, port reuse behavior, IPv6 nuances).
  - [ ] Aggregate known issues from troubleshooting guide and internal incidents.
  - [ ] Produce concise Q&A entries with remediation steps.
  - [ ] Host FAQ in docs site and link from runbooks.
- [ ] Outline compatibility matrix of supported client SDKs/protocol features over HTTP/3 to set expectations with consumers.
  - [ ] Gather compatibility data from platform and client teams.
  - [ ] Create matrix covering languages, versions, and feature caveats.
  - [ ] Publish matrix and keep it versioned alongside release notes.
- [ ] Add gRPC-specific HTTP/3 guidance (client configuration snippets, known limitations, fallback instructions) for service owners.
  - [ ] Write example configurations for .NET, Java, Go gRPC clients.
  - [ ] Highlight limitations (e.g., language library status) and suggested fallbacks.
  - [ ] Distribute guidance via internal communication channels.
- [ ] Refresh CLI help/docs to include HTTP/3 deployment steps, toggle flags, and troubleshooting guidance.
  - [ ] Update CLI `--help` output and markdown docs.
  - [ ] Provide step-by-step examples for enabling HTTP/3 via CLI workflows.
  - [ ] Align CLI release notes with new capabilities.
- [ ] Document protobuf codegen updates so generated client/server stubs surface HTTP/3 configuration options.
  - [ ] Update codegen README and inline docs with new parameters.
  - [ ] Supply migration guidance for teams regenerating clients.
  - [ ] Announce changes through release communications.
- [x] Add runtime guardrails to enforce HTTP/3 only on HTTPS endpoints and emit actionable warnings/logs when falling back to HTTP/1.1/2.
