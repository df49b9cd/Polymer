using System;
using Hugo;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class DuplexStreamCallContextTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Tracks_Request_And_Response()
    {
        var ctx = new DuplexStreamCallContext();
        ctx.IncrementRequestMessageCount();
        ctx.IncrementResponseMessageCount();
        Assert.Equal(1, ctx.RequestMessageCount);
        Assert.Equal(1, ctx.ResponseMessageCount);

        var err = Error.From("bad", "unavailable");
        Assert.True(ctx.TrySetRequestCompletion(StreamCompletionStatus.Faulted, err));
        Assert.False(ctx.TrySetRequestCompletion(StreamCompletionStatus.Succeeded));
        Assert.Equal(StreamCompletionStatus.Faulted, ctx.RequestCompletionStatus);
        Assert.NotNull(ctx.RequestCompletedAtUtc);
        Assert.NotNull(ctx.RequestCompletionError);

        Assert.True(ctx.TrySetResponseCompletion(StreamCompletionStatus.Succeeded));
        Assert.False(ctx.TrySetResponseCompletion(StreamCompletionStatus.Faulted));
        Assert.Equal(StreamCompletionStatus.Succeeded, ctx.ResponseCompletionStatus);
        Assert.NotNull(ctx.ResponseCompletedAtUtc);
        Assert.Null(ctx.ResponseCompletionError);
    }
}
