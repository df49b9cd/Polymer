using AwesomeAssertions;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Dispatcher;
using Xunit;
using static Hugo.Go;
using Unit = Hugo.Go.Unit;

namespace OmniRelay.IntegrationTests;

public sealed class ResourceLeaseIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async ValueTask ResourceLeaseDispatcher_PropagatesPrincipalAndReplicatesEvents()
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

        var componentResult = ResourceLeaseDispatcherComponent.Create(dispatcher, new ResourceLeaseDispatcherOptions
        {
            Replicator = replicator
        });
        componentResult.IsSuccess.Should().BeTrue(componentResult.Error?.ToString());
        await using var component = componentResult.Value;

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

        enqueueResponse.Stats.PendingCount.Should().Be(1);
        enqueueResponse.Stats.ActiveLeaseCount.Should().Be(0);

        var leaseResponse = await InvokeJsonAsync<ResourceLeaseLeaseRequest, ResourceLeaseLeaseResponse>(
            dispatcher,
            "resourcelease::lease",
            new ResourceLeaseLeaseRequest(),
            headers,
            cancellationToken);

        leaseResponse.OwnerPeerId.Should().Be("integration-peer");
        leaseResponse.Payload.ResourceType.Should().Be(payload.ResourceType);
        leaseResponse.Payload.ResourceId.Should().Be(payload.ResourceId);

        var ack = await InvokeJsonAsync<ResourceLeaseCompleteRequest, ResourceLeaseAcknowledgeResponse>(
            dispatcher,
            "resourcelease::complete",
            new ResourceLeaseCompleteRequest(leaseResponse.OwnershipToken),
            headers,
            cancellationToken);

        ack.Success.Should().BeTrue();

        var drainResponse = await InvokeJsonAsync<ResourceLeaseDrainRequest, ResourceLeaseDrainResponse>(
            dispatcher,
            "resourcelease::drain",
            new ResourceLeaseDrainRequest(),
            headers,
            cancellationToken);

        drainResponse.Items.Should().BeEmpty();

        replicationSink.Events.Select(evt => evt.EventType).ToArray().Should().Equal(
            ResourceLeaseReplicationEventType.Enqueue,
            ResourceLeaseReplicationEventType.LeaseGranted,
            ResourceLeaseReplicationEventType.Completed,
            ResourceLeaseReplicationEventType.Heartbeat,
            ResourceLeaseReplicationEventType.DrainSnapshot);

        replicationSink.Events[0].PeerId.Should().Be("integration-peer");
        replicationSink.Events[1].PeerId.Should().Be("integration-peer");
        replicationSink.Events[2].PeerId.Should().Be("integration-peer");
        replicationSink.Events[3].PeerId.Should().Be("integration-peer");
        replicationSink.Events[4].PeerId.Should().BeNull();
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
        var payload = codec.EncodeRequest(request, meta).ValueOrChecked();
        var rawRequest = new Request<ReadOnlyMemory<byte>>(meta, payload);

        var responseResult = await dispatcher.InvokeUnaryAsync(procedure, rawRequest, cancellationToken).ConfigureAwait(false);
        var rawResponse = responseResult.ValueOrChecked();
        return codec.DecodeResponse(rawResponse.Body, rawResponse.Meta).ValueOrChecked();
    }

    private sealed class RecordingReplicationSink : IResourceLeaseReplicationSink
    {
        private readonly List<ResourceLeaseReplicationEvent> _events = [];

        public IReadOnlyList<ResourceLeaseReplicationEvent> Events => _events;

        public ValueTask<Result<Unit>> ApplyAsync(ResourceLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            _events.Add(replicationEvent);
            return ValueTask.FromResult(Ok(Unit.Value));
        }
    }
}
