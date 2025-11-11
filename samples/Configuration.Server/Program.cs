using System.Runtime.CompilerServices;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Http;
using static Hugo.Go;

namespace OmniRelay.Samples.Configuration;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureLogging(logging =>
            {
                logging.AddConsole();
                logging.SetMinimumLevel(LogLevel.Information);
            })
            .ConfigureServices((context, services) =>
            {
                services.AddSingleton<WeatherService>();
                services.AddSingleton<TelemetrySink>();
                services.AddSingleton<RequestLoggingMiddleware>();
                services.AddSingleton<ICustomOutboundSpec, AuditFanoutOutboundSpec>();
                services.AddOmniRelayDispatcher(context.Configuration.GetSection("omnirelay"));
                services.AddHostedService<OmniRelayRegistrationHostedService>();
                services.AddHostedService<StartupBannerHostedService>();
            })
            .Build();

        await host.RunAsync().ConfigureAwait(false);
    }
}

internal sealed class OmniRelayRegistrationHostedService(
    Dispatcher.Dispatcher dispatcher,
    WeatherService weather,
    TelemetrySink telemetry,
    ILogger<OmniRelayRegistrationHostedService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Touch DI-constructed WeatherService to avoid CS9113 (parameter is unread) and ensure instantiation in samples.
        _ = weather;
        RegisterWeatherEndpoints();
        RegisterTelemetryEndpoint();

        logger.LogInformation("Registered configuration-driven OmniRelay procedures for {Service}.", dispatcher.ServiceName);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private void RegisterWeatherEndpoints()
    {
        if (!dispatcher.Codecs.TryResolve<WeatherRequest, WeatherResponse>(
                ProcedureCodecScope.Inbound,
                dispatcher.ServiceName,
                "weather::current",
                ProcedureKind.Unary,
                out var weatherCodec))
        {
            throw new InvalidOperationException("Inbound JSON codec for 'weather::current' was not registered. Ensure appsettings.json encodings are applied.");
        }

        if (!dispatcher.Codecs.TryResolve<WeatherStreamRequest, WeatherObservation>(
                ProcedureCodecScope.Inbound,
                dispatcher.ServiceName,
                "weather::stream",
                ProcedureKind.Stream,
                out var streamCodec))
        {
            throw new InvalidOperationException("Inbound JSON codec for 'weather::stream' was not registered. Ensure appsettings.json encodings are applied.");
        }

        dispatcher.RegisterUnary("weather::current", builder =>
        {
            builder.WithEncoding(weatherCodec.Encoding);
            builder.Handle(async (request, cancellationToken) =>
            {
                var decode = weatherCodec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return Err<Response<ReadOnlyMemory<byte>>>(decode.Error!);
                }

                var forecast = WeatherService.GenerateForecast(decode.Value);
                var responseMeta = new ResponseMeta(encoding: weatherCodec.Encoding);
                var encode = weatherCodec.EncodeResponse(forecast, responseMeta);
                return encode.IsSuccess
                    ? Ok(Response<ReadOnlyMemory<byte>>.Create(new ReadOnlyMemory<byte>(encode.Value), responseMeta))
                    : Err<Response<ReadOnlyMemory<byte>>>(encode.Error!);
            });
        });

        dispatcher.RegisterStream("weather::stream", builder =>
        {
            builder.WithEncoding(streamCodec.Encoding);
            builder.Handle((request, _, cancellationToken) =>
                HandleWeatherStreamAsync(streamCodec, request, cancellationToken));
        });
    }

    private void RegisterTelemetryEndpoint()
    {
        if (!dispatcher.Codecs.TryResolve<TelemetryEvent, object>(
                ProcedureCodecScope.Inbound,
                dispatcher.ServiceName,
                "telemetry::ingest",
                ProcedureKind.Oneway,
                out var telemetryCodec))
        {
            throw new InvalidOperationException("Inbound JSON codec for 'telemetry::ingest' was not registered. Ensure appsettings.json encodings are applied.");
        }

        dispatcher.RegisterOneway("telemetry::ingest", builder =>
        {
            builder.WithEncoding(telemetryCodec.Encoding);
            builder.Handle((request, cancellationToken) =>
            {
                var decode = telemetryCodec.DecodeRequest(request.Body, request.Meta);
                if (decode.IsFailure)
                {
                    return ValueTask.FromResult<Result<OnewayAck>>(Err<OnewayAck>(decode.Error!));
                }

                telemetry.Record(request.Meta, decode.Value);
                return ValueTask.FromResult<Result<OnewayAck>>(Ok(OnewayAck.Ack(new ResponseMeta(encoding: telemetryCodec.Encoding))));
            });
        });
    }

    private static async ValueTask<Result<IStreamCall>> HandleWeatherStreamAsync(
        ICodec<WeatherStreamRequest, WeatherObservation> codec,
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken)
    {
        var decode = codec.DecodeRequest(request.Body, request.Meta);
        if (decode.IsFailure)
        {
            return Err<IStreamCall>(decode.Error!);
        }

        var responseMeta = new ResponseMeta(encoding: codec.Encoding);
        var call = ServerStreamCall.Create(request.Meta, responseMeta);

        _ = Task.Run(
            () => PumpWeatherStreamAsync(codec, call, decode.Value, request.Meta.Transport ?? "grpc", cancellationToken),
            CancellationToken.None);

        return Ok((IStreamCall)call);
    }

    private static async Task PumpWeatherStreamAsync(
        ICodec<WeatherStreamRequest, WeatherObservation> codec,
        ServerStreamCall call,
        WeatherStreamRequest streamRequest,
        string transport,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var observation in WeatherService.StreamObservationsAsync(streamRequest, cancellationToken).ConfigureAwait(false))
            {
                var encode = codec.EncodeResponse(observation, call.ResponseMeta);
                if (encode.IsFailure)
                {
                    await call.CompleteAsync(encode.Error!, cancellationToken).ConfigureAwait(false);
                    return;
                }

                await call.WriteAsync(new ReadOnlyMemory<byte>(encode.Value), cancellationToken).ConfigureAwait(false);
            }

            await call.CompleteAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await call.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var error = OmniRelayErrorAdapter.FromStatus(
                OmniRelayStatusCode.Internal,
                string.IsNullOrWhiteSpace(ex.Message) ? "Weather stream failed." : ex.Message,
                transport);
            await call.CompleteAsync(error, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class WeatherService
{
    private static readonly string[] Summaries =
    [
        "Sunny",
        "Cloudy",
        "Overcast",
        "Rain",
        "Storm",
        "Windy",
        "Humid",
        "Foggy",
        "Chilly"
    ];

    public static WeatherResponse GenerateForecast(WeatherRequest request)
    {
        var temperature = Random.Shared.Next(-10, 40);
        var summary = Summaries[Math.Abs(temperature) % Summaries.Length];
        var detail = request.Detailed
            ? $"{summary} with wind chill {temperature - 5}C"
            : summary;

        return new WeatherResponse(
            request.City,
            temperature,
            detail,
            DateTimeOffset.UtcNow);
    }

    public static async IAsyncEnumerable<WeatherObservation> StreamObservationsAsync(
        WeatherStreamRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = Math.Max(1, request.Count);
        var delay = TimeSpan.FromSeconds(Math.Max(1, request.IntervalSeconds));

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var temperature = Random.Shared.Next(-15, 42);
            var summary = Summaries[Math.Abs(temperature + index) % Summaries.Length];
            yield return new WeatherObservation(
                request.City,
                index + 1,
                summary,
                temperature,
                DateTimeOffset.UtcNow);

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }
}

internal sealed class TelemetrySink(ILogger<TelemetrySink> logger)
{
    public void Record(RequestMeta meta, TelemetryEvent evt) => logger.LogInformation(
            "[telemetry] {Level} {Area} ({Transport}/{Procedure}): {Message}",
            evt.Level.ToUpperInvariant(),
            evt.Area,
            meta.Transport ?? "unknown",
            meta.Procedure ?? "telemetry::ingest",
            evt.Message);
}

internal sealed class StartupBannerHostedService(
    IConfiguration configuration,
    Dispatcher.Dispatcher dispatcher,
    ILogger<StartupBannerHostedService> logger)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var httpUrls = string.Join(
            ", ",
            configuration.GetSection("omnirelay:inbounds:http").GetChildren()
                .SelectMany(section => section.GetSection("urls").Get<string[]>() ?? []));

        var grpcUrls = string.Join(
            ", ",
            configuration.GetSection("omnirelay:inbounds:grpc").GetChildren()
                .SelectMany(section => section.GetSection("urls").Get<string[]>() ?? []));

        logger.LogInformation("OmniRelay configuration sample running as {Service}.", dispatcher.ServiceName);
        if (!string.IsNullOrWhiteSpace(httpUrls))
        {
            logger.LogInformation(" HTTP inbound bindings: {Urls}", httpUrls);
        }
        if (!string.IsNullOrWhiteSpace(grpcUrls))
        {
            logger.LogInformation(" gRPC inbound bindings: {Urls}", grpcUrls);
        }

        logger.LogInformation("Press Ctrl+C to exit.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RequestLoggingMiddleware(ILogger<RequestLoggingMiddleware> logger) :
    IUnaryInboundMiddleware,
    IOnewayInboundMiddleware,
    IStreamInboundMiddleware
{
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        UnaryInboundDelegate next)
    {
        logger.LogInformation("--> unary {Procedure}", request.Meta.Procedure);
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            logger.LogInformation("<-- unary {Procedure} OK", request.Meta.Procedure);
        }
        else
        {
            logger.LogWarning("<-- unary {Procedure} ERROR {Message}", request.Meta.Procedure, response.Error?.Message ?? "unknown");
        }

        return response;
    }

    public async ValueTask<Result<OnewayAck>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken,
        OnewayInboundDelegate next)
    {
        logger.LogInformation("--> oneway {Procedure}", request.Meta.Procedure);
        var response = await next(request, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            logger.LogInformation("<-- oneway {Procedure} ack", request.Meta.Procedure);
        }
        else
        {
            logger.LogWarning("<-- oneway {Procedure} ERROR {Message}", request.Meta.Procedure, response.Error?.Message ?? "unknown");
        }

        return response;
    }

    public async ValueTask<Result<IStreamCall>> InvokeAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        StreamCallOptions options,
        CancellationToken cancellationToken,
        StreamInboundDelegate next)
    {
        logger.LogInformation("--> stream {Procedure} ({Direction})", request.Meta.Procedure, options.Direction);
        var response = await next(request, options, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            logger.LogInformation("<-- stream {Procedure} started", request.Meta.Procedure);
        }
        else
        {
            logger.LogWarning("<-- stream {Procedure} ERROR {Message}", request.Meta.Procedure, response.Error?.Message ?? "unknown");
        }

        return response;
    }
}

internal sealed class AuditFanoutOutboundSpec : ICustomOutboundSpec
{
    public string Name => "audit-fanout";

    public IUnaryOutbound? CreateUnaryOutbound(IConfigurationSection configuration, IServiceProvider services) => null;

    public IOnewayOutbound CreateOnewayOutbound(IConfigurationSection configuration, IServiceProvider services)
    {
        var url = configuration["url"];
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new OmniRelayConfigurationException("audit-fanout outbound requires a 'url' value.");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new OmniRelayConfigurationException($"'{url}' is not a valid absolute URI for audit-fanout outbound.");
        }

        var client = new HttpClient();
        var headersSection = configuration.GetSection("headers");
        foreach (var header in headersSection.GetChildren())
        {
            if (string.IsNullOrWhiteSpace(header.Value))
            {
                continue;
            }

            client.DefaultRequestHeaders.TryAddWithoutValidation(header.Key, header.Value);
        }

        return new HttpOutbound(client, uri, disposeClient: true);
    }
}

internal sealed record WeatherRequest(string City, bool Detailed = false)
{
    public string City { get; init; } = City;

    public bool Detailed { get; init; } = Detailed;
}

internal sealed record WeatherResponse(string City, int TemperatureC, string Summary, DateTimeOffset IssuedAt)
{
    public string City { get; init; } = City;

    public int TemperatureC { get; init; } = TemperatureC;

    public string Summary { get; init; } = Summary;

    public DateTimeOffset IssuedAt { get; init; } = IssuedAt;
}

internal sealed record WeatherStreamRequest(string City, int Count = 5, int IntervalSeconds = 1)
{
    public string City { get; init; } = City;

    public int Count { get; init; } = Count;

    public int IntervalSeconds { get; init; } = IntervalSeconds;
}

internal sealed record WeatherObservation(string City, int Sequence, string Summary, int TemperatureC, DateTimeOffset Timestamp)
{
    public string City { get; init; } = City;

    public int Sequence { get; init; } = Sequence;

    public string Summary { get; init; } = Summary;

    public int TemperatureC { get; init; } = TemperatureC;

    public DateTimeOffset Timestamp { get; init; } = Timestamp;
}

internal sealed record TelemetryEvent(string Area, string Level, string Message)
{
    public string Area { get; init; } = Area;

    public string Level { get; init; } = Level;

    public string Message { get; init; } = Message;
}
