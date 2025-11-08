using System;
using Hugo;
using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class StreamCallContextTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Increment_And_Complete_Tracks_State()
    {
        var ctx = new StreamCallContext(StreamDirection.Server);
        Assert.Equal(0, ctx.MessageCount);
        ctx.IncrementMessageCount();
        ctx.IncrementMessageCount();
        Assert.Equal(2, ctx.MessageCount);

        var err = Error.From("oops", "internal");
        Assert.True(ctx.TrySetCompletion(StreamCompletionStatus.Faulted, err));
        Assert.False(ctx.TrySetCompletion(StreamCompletionStatus.Succeeded));
        Assert.Equal(StreamCompletionStatus.Faulted, ctx.CompletionStatus);
        Assert.NotNull(ctx.CompletedAtUtc);
        Assert.NotNull(ctx.CompletionError);
    }
}
