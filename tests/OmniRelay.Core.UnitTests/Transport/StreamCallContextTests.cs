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
        ctx.MessageCount.ShouldBe(0);
        ctx.IncrementMessageCount();
        ctx.IncrementMessageCount();
        ctx.MessageCount.ShouldBe(2);

        var err = Error.From("oops", "internal");
        ctx.TrySetCompletion(StreamCompletionStatus.Faulted, err).ShouldBeTrue();
        ctx.TrySetCompletion(StreamCompletionStatus.Succeeded).ShouldBeFalse();
        ctx.CompletionStatus.ShouldBe(StreamCompletionStatus.Faulted);
        ctx.CompletedAtUtc.ShouldNotBeNull();
        ctx.CompletionError.ShouldNotBeNull();
    }
}
