using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport;

public class ServerStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Write_Then_Complete_Succeeds_And_Tracks_Context()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ServerStreamCall.Create(meta);
        call.Direction.ShouldBe(StreamDirection.Server);
        call.RequestMeta.ShouldBeSameAs(meta);

        await call.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        await call.WriteAsync(new byte[] { 3 }, TestContext.Current.CancellationToken);

        var received = new List<byte[]>();
        await foreach (var payload in call.Responses.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            received.Add(payload.ToArray());
            if (received.Count == 2)
            {
                break;
            }
        }

        received.Count.ShouldBe(2);
        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.None);
        await call.CompleteAsync(null, TestContext.Current.CancellationToken);
        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.Succeeded);
        call.Context.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Complete_With_Error_Faults_Context()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ServerStreamCall.Create(meta);
        var err = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "fail", transport: "http");
        await call.CompleteAsync(err, TestContext.Current.CancellationToken);
        call.Context.CompletionStatus.ShouldBe(StreamCompletionStatus.Faulted);
        call.Context.CompletedAtUtc.ShouldNotBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void SetResponseMeta_Updates()
    {
        var call = ServerStreamCall.Create(new RequestMeta(service: "svc"));
        var meta2 = new ResponseMeta(encoding: "json");
        call.SetResponseMeta(meta2);
        call.ResponseMeta.ShouldBeSameAs(meta2);
    }
}
