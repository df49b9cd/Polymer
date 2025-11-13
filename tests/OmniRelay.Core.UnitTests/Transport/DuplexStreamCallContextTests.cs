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
        ctx.RequestMessageCount.ShouldBe(1);
        ctx.ResponseMessageCount.ShouldBe(1);

        var err = Error.From("bad", "unavailable");
        ctx.TrySetRequestCompletion(StreamCompletionStatus.Faulted, err).ShouldBeTrue();
        ctx.TrySetRequestCompletion(StreamCompletionStatus.Succeeded).ShouldBeFalse();
        ctx.RequestCompletionStatus.ShouldBe(StreamCompletionStatus.Faulted);
        ctx.RequestCompletedAtUtc.ShouldNotBeNull();
        ctx.RequestCompletionError.ShouldNotBeNull();

        ctx.TrySetResponseCompletion(StreamCompletionStatus.Succeeded).ShouldBeTrue();
        ctx.TrySetResponseCompletion(StreamCompletionStatus.Faulted).ShouldBeFalse();
        ctx.ResponseCompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        ctx.ResponseCompletedAtUtc.ShouldNotBeNull();
        ctx.ResponseCompletionError.ShouldBeNull();
    }
}
