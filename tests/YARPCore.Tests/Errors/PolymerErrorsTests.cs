using Hugo;
using Xunit;
using YARPCore.Errors;

namespace YARPCore.Tests.Errors;

public class PolymerErrorsTests
{
    [Fact]
    public void FromException_CancellationProducesCancelledStatus()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = new OperationCanceledException("cancelled", cts.Token);
        var polymerException = PolymerErrors.FromException(exception);

        Assert.Equal(PolymerStatusCode.Cancelled, polymerException.StatusCode);
        Assert.True(PolymerErrors.IsStatus(exception, PolymerStatusCode.Cancelled));

        var result = PolymerErrors.ToResult<int>(exception);
        Assert.True(result.IsFailure);
        Assert.Equal(PolymerStatusCode.Cancelled, PolymerErrorAdapter.ToStatus(result.Error!));
    }

    [Fact]
    public void FromError_PreservesMetadata()
    {
        var error = PolymerErrorAdapter.FromStatus(
            PolymerStatusCode.PermissionDenied,
            "denied",
            metadata: new Dictionary<string, object?>
            {
                { "scope", "read" }
            });

        var polymerException = PolymerErrors.FromError(error);

        Assert.Equal(PolymerStatusCode.PermissionDenied, polymerException.StatusCode);
        Assert.True(polymerException.Error.TryGetMetadata("scope", out string? scope));
        Assert.Equal("read", scope);
    }

    [Theory]
    [InlineData(PolymerStatusCode.InvalidArgument, PolymerFaultType.Client)]
    [InlineData(PolymerStatusCode.Internal, PolymerFaultType.Server)]
    [InlineData(PolymerStatusCode.Unimplemented, PolymerFaultType.Server)]
    [InlineData(PolymerStatusCode.Aborted, PolymerFaultType.Client)]
    [InlineData(PolymerStatusCode.Unknown, PolymerFaultType.Unknown)]
    public void GetFaultType_ReturnsExpectedClassification(PolymerStatusCode status, PolymerFaultType expected)
    {
        Assert.Equal(expected, PolymerErrors.GetFaultType(status));
    }

    [Fact]
    public void ToResult_StatusProducesFailure()
    {
        var result = PolymerErrors.ToResult<string>(PolymerStatusCode.Unavailable, "service unavailable");

        Assert.True(result.IsFailure);
        var error = result.Error!;
        Assert.Equal(PolymerStatusCode.Unavailable, PolymerErrorAdapter.ToStatus(error));
        Assert.Equal("service unavailable", error.Message);
    }

    [Fact]
    public void EnsureTransport_RewritesMetadata()
    {
        var exception = PolymerErrors.FromException(new TimeoutException("deadline"), transport: "http");
        Assert.Equal("http", exception.Transport);
        Assert.True(exception.Error.TryGetMetadata("polymer.transport", out string? transport));
        Assert.Equal("http", transport);

        var rewritten = PolymerErrors.FromException(exception, transport: "grpc");
        Assert.Equal("grpc", rewritten.Transport);
        Assert.True(rewritten.Error.TryGetMetadata("polymer.transport", out string? newTransport));
        Assert.Equal("grpc", newTransport);
    }

    [Fact]
    public void FromStatus_PopulatesFaultAndRetryableMetadata()
    {
        var error = PolymerErrorAdapter.FromStatus(PolymerStatusCode.Unavailable, "unavailable");

        Assert.True(error.TryGetMetadata(PolymerErrorAdapter.FaultMetadataKey, out string? fault));
        Assert.Equal(PolymerFaultType.Server.ToString(), fault);
        Assert.True(error.TryGetMetadata(PolymerErrorAdapter.RetryableMetadataKey, out bool retryable));
        Assert.True(retryable);
    }

    [Fact]
    public void IsRetryable_RespectsErrorMetadataOverride()
    {
        var error = Error.From("unavailable", "unavailable")
            .WithMetadata(PolymerErrorAdapter.RetryableMetadataKey, false);
        error = PolymerErrorAdapter.WithStatusMetadata(error, PolymerStatusCode.Unavailable);

        Assert.False(PolymerErrors.IsRetryable(error));
    }
}
