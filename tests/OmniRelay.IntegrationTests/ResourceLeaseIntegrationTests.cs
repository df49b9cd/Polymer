using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class ResourceLeaseIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task ResourceLeaseDispatcher_PropagatesPrincipalAndReplicatesEvents()
    {
        var dispatcherOptions = new DispatcherOptions("resourcelease-endpoint");
        dispatcherOptions.UnaryInboundMiddleware.Add(new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = [],
            ThumbprintHeaderName = "x-mtls-thumbprint",
            IncludeThumbprint = true
        }));

        var dispatcher = new Dispatcher.Dispatcher(dispatcherOptions);

        var replicationSink = new RecordingReplicationSink();
        var replicator = new InMemoryResourceLeaseReplicator([replicationSink]);

        await using var component = new ResourceLeaseDispatcherComponent(dispatcher, new ResourceLeaseDispatcherOptions
        {
            Replicator = replicator
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        var headers = new Dictionary<string, string>
        {
            { "x-client-principal", "integration-peer" },
            { "x-mtls-thumbprint", "thumb-42" }
        };

        var payload = new ResourceLeaseItemPayload(
            ResourceType: "lakeview",
            ResourceId: "leases",
            PartitionKey: "partition-1",
            PayloadEncoding: "json",
            Body: "{\"job\":\"sync\"}"u8.ToArray(),
            Attributes: new Dictionary<string, string> { ["owner"] = "integration" },
            RequestId: "request-1");

        var enqueueResponse = await InvokeJsonAsync<ResourceLeaseEnqueueRequest, ResourceLeaseEnqueueResponse>(
            dispatcher,
            "resourcelease::enqueue",
            new ResourceLeaseEnqueueRequest(payload),
            headers,
            cancellationToken);

        Assert.Equal(1, enqueueResponse.Stats.PendingCount);
        Assert.Equal(0, enqueueResponse.Stats.ActiveLeaseCount);

        var leaseResponse = await InvokeJsonAsync<ResourceLeaseLeaseRequest, ResourceLeaseLeaseResponse>(
            dispatcher,
            "resourcelease::lease",
            new ResourceLeaseLeaseRequest(),
            headers,
            cancellationToken);

        Assert.Equal("integration-peer", leaseResponse.OwnerPeerId);
        Assert.Equal(payload.ResourceType, leaseResponse.Payload.ResourceType);
        Assert.Equal(payload.ResourceId, leaseResponse.Payload.ResourceId);

        var ack = await InvokeJsonAsync<ResourceLeaseCompleteRequest, ResourceLeaseAcknowledgeResponse>(
            dispatcher,
            "resourcelease::complete",
            new ResourceLeaseCompleteRequest(leaseResponse.OwnershipToken),
            headers,
            cancellationToken);

        Assert.True(ack.Success);

        var drainResponse = await InvokeJsonAsync<ResourceLeaseDrainRequest, ResourceLeaseDrainResponse>(
            dispatcher,
            "resourcelease::drain",
            new ResourceLeaseDrainRequest(),
            headers,
            cancellationToken);

        Assert.Empty(drainResponse.Items);

        Assert.Equal(
            [
                ResourceLeaseReplicationEventType.Enqueue,
                ResourceLeaseReplicationEventType.LeaseGranted,
                ResourceLeaseReplicationEventType.Completed,
                ResourceLeaseReplicationEventType.Heartbeat,
                ResourceLeaseReplicationEventType.DrainSnapshot
            ],
            replicationSink.Events.Select(evt => evt.EventType).ToArray());

        Assert.Equal("integration-peer", replicationSink.Events[0].PeerId);
        Assert.Equal("integration-peer", replicationSink.Events[1].PeerId);
        Assert.Equal("integration-peer", replicationSink.Events[2].PeerId);
        Assert.Equal("integration-peer", replicationSink.Events[3].PeerId);
        Assert.Null(replicationSink.Events[4].PeerId);
    }

    private static async Task<TResponse> InvokeJsonAsync<TRequest, TResponse>(
        Dispatcher.Dispatcher dispatcher,
        string procedure,
        TRequest request,
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var meta = new RequestMeta(
            dispatcher.ServiceName,
            procedure: procedure,
            encoding: "json",
            transport: "integration-test",
            headers: headers);

        var codec = new JsonCodec<TRequest, TResponse>();
        var payload = codec.EncodeRequest(request, meta).ValueOrThrow();
        var rawRequest = new Request<ReadOnlyMemory<byte>>(meta, payload);

        var responseResult = await dispatcher.InvokeUnaryAsync(procedure, rawRequest, cancellationToken).ConfigureAwait(false);
        var rawResponse = responseResult.ValueOrThrow();
        return codec.DecodeResponse(rawResponse.Body, rawResponse.Meta).ValueOrThrow();
    }

    private sealed class RecordingReplicationSink : IResourceLeaseReplicationSink
    {
        private readonly List<ResourceLeaseReplicationEvent> _events = [];

        public IReadOnlyList<ResourceLeaseReplicationEvent> Events => _events;

        public ValueTask ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            _events.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }
}
