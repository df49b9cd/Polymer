using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Transport;

public class ClientStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask Write_Read_Complete_Response_Success()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ClientStreamCall.Create(meta);

        // Write two messages
        await call.Requests.WriteAsync(new byte[] { 1, 2 }, TestContext.Current.CancellationToken);
        await call.Requests.WriteAsync(new byte[] { 3 }, TestContext.Current.CancellationToken);
        await call.CompleteWriterAsync(null, TestContext.Current.CancellationToken);

        // Read via internal reader
        var read = new List<byte[]>();
        await foreach (var payload in call.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            read.Add(payload.ToArray());
        }
        read.Count.ShouldBe(2);

        var resMeta = new ResponseMeta(encoding: "json");
        call.TryComplete(Ok(Response<ReadOnlyMemory<byte>>.Create(new byte[] { 9 }, resMeta)));
        var result = await call.Response;
        result.IsSuccess.ShouldBeTrue();
        call.ResponseMeta.ShouldBe(resMeta);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompleteWithError_Propagates()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ClientStreamCall.Create(meta);
        var err = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom");
        call.TryCompleteWithError(err);
        var result = await call.Response;
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Internal);
    }
}
