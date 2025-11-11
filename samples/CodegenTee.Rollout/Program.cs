using System.Diagnostics.CodeAnalysis;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelayDispatcher = OmniRelay.Dispatcher.Dispatcher;

namespace OmniRelay.Samples.CodegenTee.Rollout;

internal static class Program
{
    [SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters", Justification = "Sample-only console output.")]
    public static async Task Main()
    {
        await using var primary = await RiskDeployment.StartAsync(new RiskServiceV1("risk-primary")).ConfigureAwait(false);
        await using var shadow = await RiskDeployment.StartAsync(new RiskServiceV2("risk-shadow")).ConfigureAwait(false);
        await using var harness = await RolloutHarness.StartAsync(primary, shadow).ConfigureAwait(false);

        var client = RiskServiceOmniRelay.CreateRiskServiceClient(
            harness.Dispatcher,
            RiskDeployment.ServiceName,
            RolloutHarness.OutboundKey);

        var request = new RiskRequest
        {
            PortfolioId = "ESG-42",
            Exposure = 250_000,
            ClimateScore = 0.68
        };

        var result = await client.ScorePortfolioAsync(request, cancellationToken: CancellationToken.None).ConfigureAwait(false);
        if (result.IsFailure)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Primary evaluation failed: {result.Error!.Message}");
            Console.ResetColor();
            return;
        }

        var response = result.Value.Body;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Primary version ({response.Version}) risk score: {response.RiskScore:F3}, limit usage: {response.LimitUsage:P1}");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine("Shadow feed (latest):");
        if (shadow.Service.LastResponse is { } shadowResponse)
        {
            Console.WriteLine($" - {shadow.Service.Version} risk score {shadowResponse.RiskScore:F3} limit usage {shadowResponse.LimitUsage:P1}");
        }
        else
        {
            Console.WriteLine(" - Shadow deployment has not produced a response yet.");
        }

        Console.WriteLine();
        Console.WriteLine("Run `dotnet build` to regenerate code from Protobuf via the OmniRelay generator.");
    }
}

internal sealed class RiskDeployment : IAsyncDisposable
{
    public const string ServiceName = "risk-service";
    private bool _disposed;

    private RiskDeployment(OmniRelayDispatcher dispatcher, RiskServiceBase service)
    {
        Dispatcher = dispatcher;
        Service = service;
    }

    public RiskServiceBase Service { get; }
    public OmniRelayDispatcher Dispatcher { get; }

    public static async Task<RiskDeployment> StartAsync(RiskServiceBase implementation)
    {
        var options = new DispatcherOptions(ServiceName);
        var dispatcher = new OmniRelayDispatcher(options);
        dispatcher.RegisterRiskService(implementation);
        await dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        return new RiskDeployment(dispatcher, implementation);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
    }
}

internal sealed class RolloutHarness : IAsyncDisposable
{
    public const string OutboundKey = "tee-risk";

    private RolloutHarness(OmniRelayDispatcher dispatcher) => Dispatcher = dispatcher;

    public OmniRelayDispatcher Dispatcher { get; }

    public static async Task<RolloutHarness> StartAsync(RiskDeployment primary, RiskDeployment shadow)
    {
        var options = new DispatcherOptions("samples.codegen-tee");
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddSimpleConsole(o =>
            {
                o.SingleLine = true;
                o.TimestampFormat = "HH:mm:ss ";
            });
        });

        var teeOptions = new TeeOptions
        {
            SampleRate = 1.0,
            ShadowOnSuccessOnly = true,
            ShadowHeaderName = "x-shadow-version",
            ShadowHeaderValue = shadow.Service.Version,
            LoggerFactory = loggerFactory
        };

        options.AddTeeUnaryOutbound(
            RiskDeployment.ServiceName,
            OutboundKey,
            new DispatcherUnaryOutbound(primary.Dispatcher),
            new DispatcherUnaryOutbound(shadow.Dispatcher),
            teeOptions);

        var dispatcher = new OmniRelayDispatcher(options);
        await dispatcher.StartOrThrowAsync().ConfigureAwait(false);
        return new RolloutHarness(dispatcher);
    }

    public async ValueTask DisposeAsync()
    {
        await Dispatcher.StopOrThrowAsync().ConfigureAwait(false);
    }
}

internal sealed class DispatcherUnaryOutbound(OmniRelayDispatcher dispatcher) : IUnaryOutbound
{
    private readonly OmniRelayDispatcher _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

    public ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(IRequest<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken = default)
    {
        var procedure = request.Meta.Procedure ?? throw new InvalidOperationException("Procedure name required for tee outbound.");
        return _dispatcher.InvokeUnaryAsync(procedure, request, cancellationToken);
    }

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}

internal abstract class RiskServiceBase(string version) : RiskServiceOmniRelay.IRiskService
{
    private RiskResponse? _lastResponse;
    public string Version { get; } = version;
    public RiskResponse? LastResponse => Volatile.Read(ref _lastResponse);

    public ValueTask<Response<RiskResponse>> ScorePortfolioAsync(Request<RiskRequest> request, CancellationToken cancellationToken)
    {
        var response = BuildResponse(request.Body);
        Volatile.Write(ref _lastResponse, response);
        Console.WriteLine($"[{Version}] scored {request.Body.PortfolioId} -> {response.RiskScore:F3}");
        return ValueTask.FromResult(Response<RiskResponse>.Create(response));
    }

    protected abstract RiskResponse BuildResponse(RiskRequest request);
}

internal sealed class RiskServiceV1(string version) : RiskServiceBase(version)
{
    protected override RiskResponse BuildResponse(RiskRequest request)
    {
        var score = Math.Clamp(request.ClimateScore * 0.6 + (request.Exposure / 1_000_000), 0, 1);
        return new RiskResponse
        {
            Version = Version,
            RiskScore = score,
            LimitUsage = Math.Min(request.Exposure / 500_000, 1)
        };
    }
}

internal sealed class RiskServiceV2(string version) : RiskServiceBase(version)
{
    protected override RiskResponse BuildResponse(RiskRequest request)
    {
        var adjustment = 0.1 * Math.Sin(request.Exposure / 50_000);
        var score = Math.Clamp(request.ClimateScore * 0.5 + (request.Exposure / 900_000) + adjustment, 0, 1);
        return new RiskResponse
        {
            Version = Version,
            RiskScore = score,
            LimitUsage = Math.Min(request.Exposure / 450_000, 1)
        };
    }
}
