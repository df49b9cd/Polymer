using Hugo;
using OmniRelay.Errors;
using Xunit;

namespace OmniRelay.Tests.Errors;

public class OmniRelayErrorAdapterTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void FromStatus_AttachesMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.PermissionDenied,
            "denied",
            transport: "grpc");

        error.Code.ShouldBe("permission-denied");
        error.TryGetMetadata("omnirelay.status", out string? status).ShouldBeTrue();
        status.ShouldBe(nameof(OmniRelayStatusCode.PermissionDenied));
        error.TryGetMetadata("omnirelay.transport", out string? transport).ShouldBeTrue();
        transport.ShouldBe("grpc");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FromStatus_MergesAdditionalMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.ResourceExhausted,
            "busy",
            metadata: new Dictionary<string, object?>
            {
                { "retryable", true },
                { "node", "alpha" }
            });

        error.TryGetMetadata("retryable", out bool retryable).ShouldBeTrue();
        retryable.ShouldBeTrue();
        error.TryGetMetadata("node", out string? node).ShouldBeTrue();
        node.ShouldBe("alpha");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ToStatus_UsesMetadataPriority()
    {
        var error = Error.From("denied")
            .WithMetadata("omnirelay.status", nameof(OmniRelayStatusCode.Unavailable));

        var status = OmniRelayErrorAdapter.ToStatus(error);

        status.ShouldBe(OmniRelayStatusCode.Unavailable);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ToStatus_FallsBackToCode()
    {
        var error = Error.From("internal failure", "internal");

        var status = OmniRelayErrorAdapter.ToStatus(error);

        status.ShouldBe(OmniRelayStatusCode.Internal);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ToStatus_MapsCancellationCause()
    {
        var error = Error.From("cancelled").WithCause(new OperationCanceledException());

        var status = OmniRelayErrorAdapter.ToStatus(error);

        status.ShouldBe(OmniRelayStatusCode.Cancelled);
    }
}
