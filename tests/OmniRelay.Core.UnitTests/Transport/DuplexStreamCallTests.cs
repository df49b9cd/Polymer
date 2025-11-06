using System;
using System.Threading;
using System.Threading.Tasks;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class DuplexStreamCallTests
{
    [Fact]
    public async Task CompleteRequestsAndResponses_SetStatuses()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = DuplexStreamCall.Create(meta);

        var reqErr = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Cancelled, "cancel");
        await call.CompleteRequestsAsync(reqErr, TestContext.Current.CancellationToken);
        Assert.Equal(StreamCompletionStatus.Cancelled, call.Context.RequestCompletionStatus);
        Assert.NotNull(call.Context.RequestCompletedAtUtc);

        await call.CompleteResponsesAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(StreamCompletionStatus.Succeeded, call.Context.ResponseCompletionStatus);
        Assert.NotNull(call.Context.ResponseCompletedAtUtc);

        await call.DisposeAsync();
    }
}
