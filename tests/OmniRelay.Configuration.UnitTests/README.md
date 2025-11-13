# OmniRelay.Configuration Unit Testing Guide

This document explains why the `OmniRelay.Configuration.UnitTests` project exists, what it protects, and how to extend it confidently.

## Scope & Goals
- Validate the service-registration surface (`AddOmniRelayDispatcher`) so configuration mistakes fail fast before runtime traffic flows.
- Guard runtime diagnostics controls (`DiagnosticsRuntimeState`) to ensure live overrides behave and fall back predictably.
- Exercise JSON/HTTP/peer wiring without spinning up transports by driving configuration data through `ServiceCollection`.
- Offer a repeatable harness for future configuration features (new codecs, peer choosers, transport specs, etc.).

## Reasoning & Approach
- **Configuration is code.** Dispatcher wiring is largely declarative, so tests rehydrate configuration using `ConfigurationBuilder` + in-memory dictionaries to mimic real `appsettings`.
- **Container-first validation.** Each scenario spins up a `ServiceCollection`, calls `AddOmniRelayDispatcher`, and asserts on resolved services or thrown `OmniRelayConfigurationException` instances.
- **Runtime knobs stay safe.** `DiagnosticsRuntimeState` tests focus on validating option monitors and guardrails before exposing runtime toggles to operators.
- **No external dependencies.** Tests rely on first-party projects plus xUnit, `Microsoft.NET.Test.Sdk`, `coverlet.collector`, and the lightweight `Hugo` DSL helper (see `.csproj`).

## Project Layout
- `Configuration/OmniRelayConfigurationTests.cs` - end-to-end container wiring scenarios for `AddOmniRelayDispatcher`.
- `Configuration/DiagnosticsRuntimeStateTests.cs` - focused runtime diagnostics validation.
- `OmniRelay.Configuration.UnitTests.csproj` - targets `net10.0`, brings in test packages, and references `src/OmniRelay` + `src/OmniRelay.Configuration`.

## Setup & Execution
```shell
# Run the full suite
dotnet test tests/OmniRelay.Configuration.UnitTests/OmniRelay.Configuration.UnitTests.csproj

# Focus on dispatcher wiring scenarios
dotnet test tests/OmniRelay.Configuration.UnitTests/OmniRelay.Configuration.UnitTests.csproj \
  --filter FullyQualifiedName~OmniRelayConfigurationTests

# Collect coverage (writes to TestResults/)
dotnet test tests/OmniRelay.Configuration.UnitTests/OmniRelay.Configuration.UnitTests.csproj \
  /p:CollectCoverage=true /p:CoverletOutputFormat=json,cobertura
```

**Prerequisites**
- .NET SDK 10 preview (matches repo-wide `Directory.Build.props` target).
- No additional services are required; everything runs in-process.

## Canonical Scenarios
- `AddOmniRelayDispatcher_BuildsDispatcherFromConfiguration` ensures the dispatcher, HTTP inbounds/outbounds, logging overrides, and hosted service registration materialize from configuration dictionaries (`Configuration/OmniRelayConfigurationTests.cs:29`).
- `AddOmniRelayDispatcher_MissingServiceThrows` asserts `OmniRelayConfigurationException` when `omnirelay:service` is absent, preventing ambiguous deployments (`Configuration/OmniRelayConfigurationTests.cs:72`).
- `AddOmniRelayDispatcher_InvalidPeerChooserThrows` blocks unsupported peer selection strategies before traffic routing occurs (`Configuration/OmniRelayConfigurationTests.cs:85`).
- `AddOmniRelayDispatcher_HttpsInboundWithoutTls_Throws` enforces TLS assets for HTTPS listeners so we never bind insecurely by accident (`Configuration/OmniRelayConfigurationTests.cs:105`).
- `AddOmniRelayDispatcher_InvalidGrpcTlsCertificatePath_Throws` validates TLS certificate paths during DI graph creation (`Configuration/OmniRelayConfigurationTests.cs:125`).
- `AddOmniRelayDispatcher_UsesCustomTransportSpecs` proves transport factories can be plugged in through configuration data (`Configuration/OmniRelayConfigurationTests.cs:146`).
- `AddOmniRelayDispatcher_UsesNamedHttpClientFactoryClients` checks typed/named `HttpClient` wiring for outbound specs (`Configuration/OmniRelayConfigurationTests.cs:184`).
- `AddOmniRelayDispatcher_UsesCustomPeerSpec` verifies bespoke peer definitions can be injected (`Configuration/OmniRelayConfigurationTests.cs:215`).
- `AddOmniRelayDispatcher_ConfiguresJsonCodecs` locks in JSON codec settings (indentation, enums, etc.) used by transports (`Configuration/OmniRelayConfigurationTests.cs:246`).
- `SetMinimumLogLevel_OverridesOptions` guarantees runtime log-level overrides flow through `IOptionsMonitor` and reset cleanly (`Configuration/DiagnosticsRuntimeStateTests.cs:11`).
- `SetTraceSamplingProbability_ValidatesRange` ensures probability input stays within `[0,1]` and clears correctly when null (`Configuration/DiagnosticsRuntimeStateTests.cs:26`).

## Writing New Tests
1. **Model the configuration input.** Use `ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ... }).Build()` so keys match production `omnirelay:` paths.
2. **Build services the way production does.** Call `services.AddLogging(); services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));`.
3. **Assert outcomes.** Resolve `OmniRelayDispatcher`, `IOptions<T>`, or hosted services and assert on the specific behavior. Prefer `Assert.Throws<OmniRelayConfigurationException>` for guardrails.
4. **Isolate custom collaborators.** If you need a bespoke transport/peer, define lightweight inner classes (see existing tests) instead of introducing new files.
5. **Name clearly.** Follow `MethodUnderTest_Expectation` naming so failures read like documentation.

### Example Template
```csharp
[Fact]
public void AddOmniRelayDispatcher_RejectsUnknownCodec()
{
    var configuration = new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["omnirelay:service"] = "gateway",
            ["omnirelay:codecs:custom:type"] = "UnknownCodec"
        }!)
        .Build();

    var services = new ServiceCollection();
    services.AddLogging();

    services.AddOmniRelayDispatcher(configuration.GetSection("omnirelay"));
    using var provider = services.BuildServiceProvider();

    Assert.Throws<OmniRelayConfigurationException>(
        () => provider.GetRequiredService<OmniRelayDispatcher>());
}
```

## Best Practices & Tips
- Keep fixtures self-contained; avoid static/global state between tests.
- Prefer `Assert.Collection`, `Assert.Contains`, and structured assertions over string compare to make intent obvious.
- Use NSubstitute for fakes/mocks and Shouldly for high-signal assertions to keep the suite stylistically consistent.
- Any time configuration accepts URIs, add both happy-path and failure-path coverage (missing TLS, malformed URLs, etc.).
- Use `IOptionsMonitor` helpers (as seen in `DiagnosticsRuntimeStateTests`) to materialize defaults before asserting on overrides.
- When adding new diagnostics knobs, test both the override path and the reset (`null`) path.

## Troubleshooting
- **Type load failures:** Ensure the referencing project builds (`dotnet build OmniRelay.slnx`) before running these tests.
- **File path failures:** TLS and certificate tests rely on temporary paths; prefer `Path.GetTempPath()` + `Guid` if you add new ones.
- **Flaky DI resolution:** Wrap `ServiceProvider` in `using` so tests dispose transports/clients promptly.

## Next Steps
- Add new tests whenever configuration schema grows (new transport kinds, codec options, peer routing strategies).
- Mirror real configuration snippets in `/samples` or `/docs` to avoid drift between documentation and automated coverage.
