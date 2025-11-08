using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Errors;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Transport;

public class ClientStreamCallTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async Task Write_Read_Complete_Response_Success()
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
        Assert.Equal(2, read.Count);

        var resMeta = new ResponseMeta(encoding: "json");
        call.TryComplete(Ok(Response<ReadOnlyMemory<byte>>.Create(new byte[] { 9 }, resMeta)));
        var result = await call.Response;
        Assert.True(result.IsSuccess);
        Assert.Equal(resMeta, call.ResponseMeta);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async Task CompleteWithError_Propagates()
    {
        var meta = new RequestMeta(service: "svc", transport: "http");
        var call = ClientStreamCall.Create(meta);
        var err = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Internal, "boom");
        call.TryCompleteWithError(err);
        var result = await call.Response;
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Internal, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }
}
