using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Dispatcher;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.IntegrationTests;

public sealed class TableLeaseIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task TableLeaseDispatcher_PropagatesPrincipalAndReplicatesEvents()
    {
        var dispatcherOptions = new DispatcherOptions("tablelease-endpoint");
        dispatcherOptions.UnaryInboundMiddleware.Add(new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ImmutableArray.Create("x-client-principal"),
            AuthorizationHeaderNames = ImmutableArray<string>.Empty,
            ThumbprintHeaderName = "x-mtls-thumbprint",
            IncludeThumbprint = true
        }));

        var dispatcher = new Dispatcher.Dispatcher(dispatcherOptions);

        var replicationSink = new RecordingReplicationSink();
        var replicator = new InMemoryTableLeaseReplicator(new[] { replicationSink });

        await using var component = new TableLeaseDispatcherComponent(dispatcher, new TableLeaseDispatcherOptions
        {
            Replicator = replicator
        });

        var cancellationToken = TestContext.Current.CancellationToken;
        var headers = new Dictionary<string, string>
        {
            { "x-client-principal", "integration-peer" },
            { "x-mtls-thumbprint", "thumb-42" }
        };

        var payload = new TableLeaseItemPayload(
            Namespace: "lakeview",
            Table: "leases",
            PartitionKey: "partition-1",
            PayloadEncoding: "json",
            Body: Encoding.UTF8.GetBytes("{\"job\":\"sync\"}"),
            Attributes: new Dictionary<string, string> { ["owner"] = "integration" },
            RequestId: "request-1");

        var enqueueResponse = await InvokeJsonAsync<TableLeaseEnqueueRequest, TableLeaseEnqueueResponse>(
            dispatcher,
            "tablelease::enqueue",
            new TableLeaseEnqueueRequest(payload),
            headers,
            cancellationToken);

        Assert.Equal(1, enqueueResponse.Stats.PendingCount);
        Assert.Equal(0, enqueueResponse.Stats.ActiveLeaseCount);

        var leaseResponse = await InvokeJsonAsync<TableLeaseLeaseRequest, TableLeaseLeaseResponse>(
            dispatcher,
            "tablelease::lease",
            new TableLeaseLeaseRequest(),
            headers,
            cancellationToken);

        Assert.Equal("integration-peer", leaseResponse.OwnerPeerId);
        Assert.Equal(payload.Namespace, leaseResponse.Payload.Namespace);
        Assert.Equal(payload.Table, leaseResponse.Payload.Table);

        var ack = await InvokeJsonAsync<TableLeaseCompleteRequest, TableLeaseAcknowledgeResponse>(
            dispatcher,
            "tablelease::complete",
            new TableLeaseCompleteRequest(leaseResponse.OwnershipToken),
            headers,
            cancellationToken);

        Assert.True(ack.Success);

        var drainResponse = await InvokeJsonAsync<TableLeaseDrainRequest, TableLeaseDrainResponse>(
            dispatcher,
            "tablelease::drain",
            new TableLeaseDrainRequest(),
            headers,
            cancellationToken);

        Assert.Empty(drainResponse.Items);

        Assert.Equal(
            new[]
            {
                TableLeaseReplicationEventType.Enqueue,
                TableLeaseReplicationEventType.LeaseGranted,
                TableLeaseReplicationEventType.Completed,
                TableLeaseReplicationEventType.Heartbeat,
                TableLeaseReplicationEventType.DrainSnapshot
            },
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

    private sealed class RecordingReplicationSink : ITableLeaseReplicationSink
    {
        private readonly List<TableLeaseReplicationEvent> _events = new();

        public IReadOnlyList<TableLeaseReplicationEvent> Events => _events;

        public ValueTask ApplyAsync(TableLeaseReplicationEvent replicationEvent, CancellationToken cancellationToken)
        {
            _events.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }
}
