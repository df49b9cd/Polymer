using Grpc.Core;
using Microsoft.Extensions.Logging;
using ProtoLeadershipControlService = OmniRelay.Mesh.Control.V1.LeadershipControlService;
using ProtoLeadershipEvent = OmniRelay.Mesh.Control.V1.LeadershipEvent;
using ProtoLeadershipSubscribeRequest = OmniRelay.Mesh.Control.V1.LeadershipSubscribeRequest;

namespace OmniRelay.Core.Leadership;

/// <summary>gRPC control-plane service that streams leadership events.</summary>
public sealed class LeadershipControlGrpcService(
    ILeadershipObserver leadership,
    ILogger<LeadershipControlGrpcService> logger)
    : ProtoLeadershipControlService.LeadershipControlServiceBase
{
    private readonly ILeadershipObserver _leadership = leadership ?? throw new ArgumentNullException(nameof(leadership));
    private readonly ILogger<LeadershipControlGrpcService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public override async Task Subscribe(ProtoLeadershipSubscribeRequest request, IServerStreamWriter<ProtoLeadershipEvent> responseStream, ServerCallContext context)
    {
        var scopeFilter = string.IsNullOrWhiteSpace(request.Scope) ? null : request.Scope!.Trim();
        var httpContext = context.GetHttpContext();
        var transport = httpContext?.Request.Protocol ?? context.Peer;
        var scopeLabel = scopeFilter ?? "*";
        LeadershipControlGrpcServiceLog.StreamOpened(_logger, scopeLabel, transport);

        try
        {
            await foreach (var leadershipEvent in _leadership.SubscribeAsync(scopeFilter, context.CancellationToken).ConfigureAwait(false))
            {
                var proto = leadershipEvent.ToProto();
                await responseStream.WriteAsync(proto).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when clients disconnect.
        }
        finally
        {
            LeadershipControlGrpcServiceLog.StreamClosed(_logger, scopeLabel);
        }
    }
}

internal static partial class LeadershipControlGrpcServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Leadership gRPC stream opened (scope={Scope}, transport={Transport}).")]
    public static partial void StreamOpened(ILogger logger, string scope, string transport);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Leadership gRPC stream closed (scope={Scope}).")]
    public static partial void StreamClosed(ILogger logger, string scope);
}
