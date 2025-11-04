using Hugo;
using Xunit;
using OmniRelay.Errors;

namespace OmniRelay.Tests.Errors;

public class OmniRelayErrorsTests
{
    [Fact]
    public void FromException_CancellationProducesCancelledStatus()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = new OperationCanceledException("cancelled", cts.Token);
        var polymerException = OmniRelayErrors.FromException(exception);

        Assert.Equal(OmniRelayStatusCode.Cancelled, polymerException.StatusCode);
        Assert.True(OmniRelayErrors.IsStatus(exception, OmniRelayStatusCode.Cancelled));

        var result = OmniRelayErrors.ToResult<int>(exception);
        Assert.True(result.IsFailure);
        Assert.Equal(OmniRelayStatusCode.Cancelled, OmniRelayErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public void FromError_PreservesMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.PermissionDenied,
            "denied",
            metadata: new Dictionary<string, object?>
            {
                { "scope", "read" }
            });

        var polymerException = OmniRelayErrors.FromError(error);

        Assert.Equal(OmniRelayStatusCode.PermissionDenied, polymerException.StatusCode);
        Assert.True(polymerException.Error.TryGetMetadata("scope", out string? scope));
        Assert.Equal("read", scope);
    }

    [Theory]
    [InlineData(OmniRelayStatusCode.InvalidArgument, OmniRelayFaultType.Client)]
    [InlineData(OmniRelayStatusCode.Internal, OmniRelayFaultType.Server)]
    [InlineData(OmniRelayStatusCode.Unimplemented, OmniRelayFaultType.Server)]
    [InlineData(OmniRelayStatusCode.Aborted, OmniRelayFaultType.Client)]
    [InlineData(OmniRelayStatusCode.Unknown, OmniRelayFaultType.Unknown)]
    public void GetFaultType_ReturnsExpectedClassification(OmniRelayStatusCode status, OmniRelayFaultType expected)
    {
        Assert.Equal(expected, OmniRelayErrors.GetFaultType(status));
    }

    [Fact]
    public void ToResult_StatusProducesFailure()
    {
        var result = OmniRelayErrors.ToResult<string>(OmniRelayStatusCode.Unavailable, "service unavailable");

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(OmniRelayStatusCode.Unavailable, OmniRelayErrorAdapter.ToStatus(error));
        Assert.Equal("service unavailable", error.Message);
    }

    [Fact]
    public void EnsureTransport_RewritesMetadata()
    {
        var exception = OmniRelayErrors.FromException(new TimeoutException("deadline"), transport: "http");
        Assert.Equal("http", exception.Transport);
        Assert.True(exception.Error.TryGetMetadata("yarpcore.transport", out string? transport));
        Assert.Equal("http", transport);

        var rewritten = OmniRelayErrors.FromException(exception, transport: "grpc");
        Assert.Equal("grpc", rewritten.Transport);
        Assert.True(rewritten.Error.TryGetMetadata("yarpcore.transport", out string? newTransport));
        Assert.Equal("grpc", newTransport);
    }

    [Fact]
    public void FromStatus_PopulatesFaultAndRetryableMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "unavailable");

        Assert.True(error.TryGetMetadata(OmniRelayErrorAdapter.FaultMetadataKey, out string? fault));
        Assert.Equal(OmniRelayFaultType.Server.ToString(), fault);
        Assert.True(error.TryGetMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, out bool retryable));
        Assert.True(retryable);
    }

    [Fact]
    public void IsRetryable_RespectsErrorMetadataOverride()
    {
        var error = Error.From("unavailable", "unavailable")
            .WithMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, false);
        error = OmniRelayErrorAdapter.WithStatusMetadata(error, OmniRelayStatusCode.Unavailable);

        Assert.False(OmniRelayErrors.IsRetryable(error));
    }
}
