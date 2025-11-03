using System.Text.Json;
using Google.Protobuf;
using Hugo;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Transport.Http;
using Polymer.Transport.Grpc;
using Polymer.Errors;
using Polymer.YabInterop.Protos;
using static Hugo.Go;

var httpPort = 8080;
int? grpcPort = null;
var enableHttp = true;
var durationSeconds = 0;

for (var index = 0; index < args.Length; index++)
{
    switch (args[index])
    {
        case "--port" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedPort):
            httpPort = parsedPort;
            enableHttp = true;
            index++;
            break;
        case "--no-http":
            enableHttp = false;
            break;
        case "--grpc-port" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedGrpcPort):
            grpcPort = parsedGrpcPort;
            index++;
            break;
        case "--duration" when index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedDuration):
            durationSeconds = parsedDuration;
            index++;
            break;
    }
}

if (!enableHttp && grpcPort is null)
{
    Console.WriteLine("No inbound transports configured. Enable HTTP or specify --grpc-port.");
    return;
}

var dispatcherOptions = new DispatcherOptions("echo");

JsonCodec<JsonEchoRequest, JsonEchoResponse>? httpCodec = null;
if (enableHttp)
{
    var httpAddress = new Uri($"http://127.0.0.1:{httpPort}");
    var httpInbound = new HttpInbound(new[] { httpAddress.ToString() });
    dispatcherOptions.AddLifecycle("http-inbound", httpInbound);

    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    httpCodec = new JsonCodec<JsonEchoRequest, JsonEchoResponse>(jsonOptions);
}

if (grpcPort is { } gp)
{
    var grpcAddress = new Uri($"http://127.0.0.1:{gp}");
    var grpcInbound = new GrpcInbound(new[] { grpcAddress.ToString() });
    dispatcherOptions.AddLifecycle("grpc-inbound", grpcInbound);
}

var dispatcher = new Polymer.Dispatcher.Dispatcher(dispatcherOptions);

if (httpCodec is not null)
{
    var codecInstance = httpCodec;
    dispatcher.Register(new UnaryProcedureSpec(
        "echo",
        "echo::ping",
        (request, cancellationToken) => HandleHttpEchoAsync(request, codecInstance!)));
}

if (grpcPort is not null)
{
    dispatcher.Register(new UnaryProcedureSpec(
        "echo",
        "Ping",
        (request, cancellationToken) => HandleGrpcEchoAsync(request)));
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

try
{
    await dispatcher.StartAsync(cts.Token).ConfigureAwait(false);

    if (enableHttp)
    {
        Console.WriteLine($"Polymer HTTP echo server listening on http://127.0.0.1:{httpPort}");
    }

    if (grpcPort is { } activeGrpcPort)
    {
        Console.WriteLine($"Polymer gRPC echo server listening on 127.0.0.1:{activeGrpcPort}");
    }

    if (durationSeconds > 0)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown via cancellation
        }
    }
    else
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token).ConfigureAwait(false);
    }
}
catch (OperationCanceledException)
{
    // shutting down
}
finally
{
    await dispatcher.StopAsync(CancellationToken.None).ConfigureAwait(false);
}

static ValueTask<Result<Response<ReadOnlyMemory<byte>>>> HandleHttpEchoAsync(
    IRequest<ReadOnlyMemory<byte>> request,
    JsonCodec<JsonEchoRequest, JsonEchoResponse> codec)
{
    var decode = codec.DecodeRequest(request.Body, request.Meta);
    if (decode.IsFailure)
    {
        return ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(decode.Error!));
    }

    var responsePayload = new JsonEchoResponse { Message = decode.Value.Message };
    var responseMeta = new ResponseMeta(encoding: "application/json");
    var encode = codec.EncodeResponse(responsePayload, responseMeta);
    return encode.IsSuccess
        ? ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(encode.Value, responseMeta)))
        : ValueTask.FromResult(Err<Response<ReadOnlyMemory<byte>>>(encode.Error!));
}

static ValueTask<Result<Response<ReadOnlyMemory<byte>>>> HandleGrpcEchoAsync(
    IRequest<ReadOnlyMemory<byte>> request)
{
    try
    {
        var incoming = EchoRequest.Parser.ParseFrom(request.Body.ToArray());
        var response = new EchoResponse { Message = incoming.Message };
        var responseMeta = new ResponseMeta(encoding: "application/grpc");
        var payload = response.ToByteArray();
        return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta)));
    }
    catch (Exception ex)
    {
        return ValueTask.FromResult(PolymerErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, request.Meta.Transport ?? "grpc"));
    }
}

public sealed record JsonEchoRequest
{
    public string Message { get; init; } = string.Empty;
}

public sealed record JsonEchoResponse
{
    public string Message { get; init; } = string.Empty;
}
