using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class ServerStreamCallTests
{
    [Fact]
    public async Task Write_Then_Complete_Succeeds_And_Tracks_Context()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ServerStreamCall.Create(meta);
        Assert.Equal(StreamDirection.Server, call.Direction);
        Assert.Same(meta, call.RequestMeta);

        await call.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        await call.WriteAsync(new byte[] { 3 }, TestContext.Current.CancellationToken);

        var received = new System.Collections.Generic.List<byte[]>();
        await foreach (var payload in call.Responses.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            received.Add(payload.ToArray());
            if (received.Count == 2) break;
        }

        Assert.Equal(2, received.Count);
        Assert.Equal(StreamCompletionStatus.None, call.Context.CompletionStatus);
        await call.CompleteAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.CompletionStatus);
        Assert.NotNull(call.Context.CompletedAtUtc);
    }

    [Fact]
    public async Task Complete_With_Error_Faults_Context()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ServerStreamCall.Create(meta);
        var err = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http");
        await call.CompleteAsync(err, TestContext.Current.CancellationToken);
        Assert.Equal(StreamCompletionStatus.Faulted, call.Context.CompletionStatus);
        Assert.NotNull(call.Context.CompletedAtUtc);
    }

    [Fact]
    public void SetResponseMeta_Updates()
    {
        var call = ServerStreamCall.Create(new RequestMeta(service: "svc"));
        var meta2 = new ResponseMeta(encoding: "json");
        call.SetResponseMeta(meta2);
        Assert.Same(meta2, call.ResponseMeta);
    }
}
