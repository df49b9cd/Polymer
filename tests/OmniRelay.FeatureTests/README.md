# OmniRelay.FeatureTests

Feature-level coverage for OmniRelay that exercises dispatcher bootstrapping, configuration, runtime wiring, and the seams to external services.

## Scope
- Validate end-to-end user journeys that flow through the public OmniRelay surface (CLI + runtime configuration + dispatcher lifecycle).
- Cover cross-cutting behaviors that span multiple assemblies such as logging, diagnostics, codec wiring, and runtime configuration binding.
- Guard high-risk integrations with infrastructure dependencies (databases, object storage, message bus, event store) via disposable environments.
- Defer unit-level granularity, transport benchmarks, and infrastructure smoke tests to their dedicated suites.

## Goals
- Provide executable documentation so product, reliability, and platform engineers can see the dispatcher behaving like users describe it.
- Catch regressions introduced by refactors to configuration, hosting, or dependency wiring before code reaches integration or staging environments.
- Offer a predictable, hermetic harness that can run locally and in CI in under ~5 minutes, even when optional containers are enabled.

## Reasoning
- Feature-level validation mirrors how stakeholders talk about value, so failures map directly to user impact instead of implementation detail.
- Building on the same DI extensions (`AddOmniRelayDispatcher`) reduces skew between tests and production bootstrapping, keeping assertions honest.
- Testcontainers supplies disposable, production-like dependencies without bespoke mocks, which is critical for transports, codecs, and routing logic that depend on real protocols.
- Making infrastructure opt-in keeps the feedback loop fast for day-to-day development while still enabling high-fidelity runs when needed.

## Methodology
1. **Host Fidelity** - Tests spin up `Host.CreateApplicationBuilder` instances with the same `AddOmniRelayDispatcher` wiring that production uses. Assertions observe behavior only through the resulting service provider and public APIs.
2. **Scenario Vocabulary** - Each test folder maps to a feature slice (e.g., dispatcher bootstrap, routing, shadowing). Within it, use Given/When/Then naming and keep assertions focused on user-visible outcomes.
3. **Infrastructure Isolation** - External dependencies are provisioned through `FeatureTestContainers`, which lazily starts Testcontainers for PostgreSQL, EventStoreDB, MinIO, and NATS. Containers are disabled unless `OMNIRELAY_FEATURETESTS_CONTAINERS=true` is supplied.
4. **Composable Fixtures** - `FeatureTestApplication` acts as the shared collection fixture. Tests can request per-scenario overrides by creating new instances with custom options, but must dispose them eagerly to avoid resource leaks.
5. **Deterministic Inputs** - Configuration flows from `appsettings.featuretests.json` plus optional overrides via `OMNIRELAY_FEATURETESTS_*` environment variables. Avoid relying on ambient machine state (local secrets, running daemons, etc.).

## Best Practices
- **Test public behavior, not wiring detail** – Interact with OmniRelay through published endpoints, DI, and configuration objects. Internal helpers should be covered by unit/integration suites.
- **One scenario per test** – Structure test names as `Feature_Scenario_Outcome`, capture Given/When/Then steps inline, and keep each test under a few assertions to simplify triage.
- **Prefer async lifecycle hooks** – Use `IAsyncLifetime` (as in `FeatureTestApplication`) for setup/teardown so hosts and Testcontainers are started exactly once and always disposed.
- **Share expensive fixtures intentionally** – `[CollectionDefinition]` keeps container startup costs low. Add new collections only when scenarios truly need different environments.
- **Fail fast with deterministic data** – Seed databases or blob stores through explicit helper methods and avoid randomness unless it is seeded. This keeps replaying failures straightforward.
- **Log everything to test output** – Inject `ILoggerFactory` or `ITestOutputHelper` to capture dispatcher and container logs; it shortens time-to-root-cause when CI flakes do appear.
- **Guard external dependencies** – Wrap container usage in `ContainersEnabled` checks so developers without Docker can still run the suite, and skip tests (`Skip = "...`) only as a last resort.
- **CI parity** – Run the suite in the same way locally and in CI (`dotnet test` with the same environment variables) to avoid “works on my machine” drift.

## Technology Base
- **xUnit v3** - released version with asynchronous lifecycle hooks and collection fixtures.
- **Microsoft.AspNetCore.Mvc.Testing** - boots production-style hosts without a standalone web server.
- **Testcontainers for .NET** - orchestrates throwaway instances of databases, event stores, object storage, and message buses.
- **OmniRelay.Configuration** – the same DI extensions production apps call, ensuring feature tests observe realistic dispatcher wiring.
- **Host.CreateApplicationBuilder** – consistent bootstrapping story for generic host scenarios.

## Workflow
1. Write or extend a scenario under `Features/<FeatureName>/` with a descriptive test class name.
2. If a scenario needs infrastructure, call `FeatureTestApplication.Containers.Ensure*Async` and gate execution on `ContainersEnabled` to avoid unwanted Docker requirements.
3. Keep assertions behavioral: verify service names, routing choices, codec registrations, or emitted telemetry – not private types.
4. Run locally with:
   ```powershell
   dotnet test tests/OmniRelay.FeatureTests/OmniRelay.FeatureTests.csproj
   ```
5. To exercise external services:
   ```powershell
   $env:OMNIRELAY_FEATURETESTS_CONTAINERS = 'true'
   dotnet test tests/OmniRelay.FeatureTests/OmniRelay.FeatureTests.csproj
   ```
6. Reset the env var (or set to `false`) when done so regular unit suites do not attempt to start containers.

## Future Enhancements
- Add Playwright-driven smoke flows once the management UI stabilizes.
- Extend `FeatureTestContainers` with reusable database seeding helpers to capture migration/regression scenarios.
- Wire the suite into CI gates that run on trunk merges with container support enabled.
