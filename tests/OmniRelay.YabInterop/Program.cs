using System.Text.Json;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;
using OmniRelay.YabInterop;
using static Hugo.Go;

var httpPort = 8080;
int? grpcPort = null;
var enableHttp = true;
var durationSeconds = 0;
string? descriptorOutput = null;

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
        case "--dump-descriptor" when index + 1 < args.Length:
            descriptorOutput = args[index + 1];
            index++;
            break;
    }
}

if (!string.IsNullOrEmpty(descriptorOutput))
{
    await WriteDescriptorAsync(descriptorOutput!);
    return;
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
    var httpInbound = new HttpInbound([httpAddress.ToString()]);
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
    var grpcInbound = new GrpcInbound([grpcAddress.ToString()]);
    dispatcherOptions.AddLifecycle("grpc-inbound", grpcInbound);
}

var dispatcher = new Dispatcher(dispatcherOptions);

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
    await dispatcher.StartAsyncChecked(cts.Token);

    if (enableHttp)
    {
        Console.WriteLine($"OmniRelay HTTP echo server listening on http://127.0.0.1:{httpPort}");
    }

    if (grpcPort is { } activeGrpcPort)
    {
        Console.WriteLine($"OmniRelay gRPC echo server listening on 127.0.0.1:{activeGrpcPort}");
    }

    if (durationSeconds > 0)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(durationSeconds), cts.Token);
        }
        catch (OperationCanceledException)
        {
            // graceful shutdown via cancellation
        }
    }
    else
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cts.Token);
    }
}
catch (OperationCanceledException)
{
    // shutting down
}
finally
{
    await dispatcher.StopAsyncChecked(CancellationToken.None);
}

static async Task WriteDescriptorAsync(string outputPath)
{
    var directory = Path.GetDirectoryName(outputPath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var descriptorSet = new FileDescriptorSet();
    descriptorSet.File.Add(EchoReflection.Descriptor.ToProto());

    await using var stream = File.Create(outputPath);
    descriptorSet.WriteTo(stream);
    await stream.FlushAsync();
    Console.WriteLine($"Wrote protobuf descriptor to {outputPath}");
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
        return ValueTask.FromResult(OmniRelayErrors.ToResult<Response<ReadOnlyMemory<byte>>>(ex, request.Meta.Transport ?? "grpc"));
    }
}

namespace OmniRelay.YabInterop
{
    public sealed record JsonEchoRequest
    {
        public string Message { get; init; } = string.Empty;
    }

    public sealed record JsonEchoResponse
    {
        public string Message { get; init; } = string.Empty;
    }
}
