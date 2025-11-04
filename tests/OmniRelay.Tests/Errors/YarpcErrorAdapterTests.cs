using Hugo;
using Xunit;
using OmniRelay.Errors;

namespace OmniRelay.Tests.Errors;

public class YarpcErrorAdapterTests
{
    [Fact]
    public void FromStatus_AttachesMetadata()
    {
        var error = OmniRelayErrorAdapter.FromStatus(
            OmniRelayStatusCode.PermissionDenied,
            "denied",
            transport: "grpc");

        Assert.Equal("permission-denied", error.Code);
        Assert.True(error.TryGetMetadata("yarpcore.status", out string? status));
        Assert.Equal(nameof(OmniRelayStatusCode.PermissionDenied), status);
        Assert.True(error.TryGetMetadata("yarpcore.transport", out string? transport));
        Assert.Equal("grpc", transport);
    }

    [Fact]
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

        Assert.True(error.TryGetMetadata("retryable", out bool retryable));
        Assert.True(retryable);
        Assert.True(error.TryGetMetadata("node", out string? node));
        Assert.Equal("alpha", node);
    }

    [Fact]
    public void ToStatus_UsesMetadataPriority()
    {
        var error = Error.From("denied")
            .WithMetadata("yarpcore.status", nameof(OmniRelayStatusCode.Unavailable));

        var status = OmniRelayErrorAdapter.ToStatus(error);

        Assert.Equal(OmniRelayStatusCode.Unavailable, status);
    }

    [Fact]
    public void ToStatus_FallsBackToCode()
    {
        var error = Error.From("internal failure", "internal");

        var status = OmniRelayErrorAdapter.ToStatus(error);

        Assert.Equal(OmniRelayStatusCode.Internal, status);
    }

    [Fact]
    public void ToStatus_MapsCancellationCause()
    {
        var error = Error.From("cancelled").WithCause(new OperationCanceledException());

        var status = OmniRelayErrorAdapter.ToStatus(error);

        Assert.Equal(OmniRelayStatusCode.Cancelled, status);
    }
}
