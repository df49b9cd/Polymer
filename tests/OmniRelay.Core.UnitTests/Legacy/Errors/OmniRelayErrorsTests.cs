using Hugo;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Errors;

public class OmniRelayErrorsTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void FromException_CancellationProducesCancelledStatus()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var exception = new OperationCanceledException("cancelled", cts.Token);
        var omnirelayException = OmniRelayErrors.FromException(exception);

        omnirelayException.StatusCode.ShouldBe(OmniRelayStatusCode.Cancelled);
        OmniRelayErrors.IsStatus(exception, OmniRelayStatusCode.Cancelled).ShouldBeTrue();

        var result = OmniRelayErrors.ToResult<int>(exception);
        result.IsFailure.ShouldBeTrue();
        OmniRelayErrorAdapter.ToStatus(result.Error!).ShouldBe(OmniRelayStatusCode.Cancelled);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FromError_PreservesMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.PermissionDenied,
            "denied",
            metadata: new Dictionary<string, object?>
            {
                { "scope", "read" }
            });

        var omnirelayException = OmniRelayErrors.FromError(error);

        omnirelayException.StatusCode.ShouldBe(OmniRelayStatusCode.PermissionDenied);
        omnirelayException.Error.TryGetMetadata("scope", out string? scope).ShouldBeTrue();
        scope.ShouldBe("read");
    }

    [Theory(Timeout = TestTimeouts.Default)]
    [InlineData(OmniRelayStatusCode.InvalidArgument, OmniRelayFaultType.Client)]
    [InlineData(OmniRelayStatusCode.Internal, OmniRelayFaultType.Server)]
    [InlineData(OmniRelayStatusCode.Unimplemented, OmniRelayFaultType.Server)]
    [InlineData(OmniRelayStatusCode.Aborted, OmniRelayFaultType.Client)]
    [InlineData(OmniRelayStatusCode.Unknown, OmniRelayFaultType.Unknown)]
    public void GetFaultType_ReturnsExpectedClassification(OmniRelayStatusCode status, OmniRelayFaultType expected) => OmniRelayErrors.GetFaultType(status).ShouldBe(expected);

    [Fact(Timeout = TestTimeouts.Default)]
    public void ToResult_StatusProducesFailure()
    {
        var result = OmniRelayErrors.ToResult<string>(OmniRelayStatusCode.Unavailable, "service unavailable");

        result.IsFailure.ShouldBeTrue();
        var error = result.Error!;
        OmniRelayErrorAdapter.ToStatus(error).ShouldBe(OmniRelayStatusCode.Unavailable);
        error.Message.ShouldBe("service unavailable");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void EnsureTransport_RewritesMetadata()
    {
        var exception = OmniRelayErrors.FromException(new TimeoutException("deadline"), transport: "http");
        exception.Transport.ShouldBe("http");
        exception.Error.TryGetMetadata("omnirelay.transport", out string? transport).ShouldBeTrue();
        transport.ShouldBe("http");

        var rewritten = OmniRelayErrors.FromException(exception, transport: "grpc");
        rewritten.Transport.ShouldBe("grpc");
        rewritten.Error.TryGetMetadata("omnirelay.transport", out string? newTransport).ShouldBeTrue();
        newTransport.ShouldBe("grpc");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FromStatus_PopulatesFaultAndRetryableMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(OmniRelayStatusCode.Unavailable, "unavailable");

        error.TryGetMetadata(OmniRelayErrorAdapter.FaultMetadataKey, out string? fault).ShouldBeTrue();
        fault.ShouldBe(nameof(OmniRelayFaultType.Server));
        error.TryGetMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, out bool retryable).ShouldBeTrue();
        retryable.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void IsRetryable_RespectsErrorMetadataOverride()
    {
        var error = Error.From("unavailable", "unavailable")
            .WithMetadata(OmniRelayErrorAdapter.RetryableMetadataKey, false);
        error = OmniRelayErrorAdapter.WithStatusMetadata(error, OmniRelayStatusCode.Unavailable);

        OmniRelayErrors.IsRetryable(error).ShouldBeFalse();
    }
}
