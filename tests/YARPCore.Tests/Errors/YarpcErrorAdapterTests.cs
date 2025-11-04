using Hugo;
using Xunit;
using YARPCore.Errors;

namespace YARPCore.Tests.Errors;

public class PolymerErrorAdapterTests
{
    [Fact]
    public void FromStatus_AttachesMetadata()
    {
        var error = PolymerErrorAdapter.FromStatus(
            PolymerStatusCode.PermissionDenied,
            "denied",
            transport: "grpc");

        Assert.Equal("permission-denied", error.Code);
        Assert.True(error.TryGetMetadata("polymer.status", out string? status));
        Assert.Equal(nameof(PolymerStatusCode.PermissionDenied), status);
        Assert.True(error.TryGetMetadata("polymer.transport", out string? transport));
        Assert.Equal("grpc", transport);
    }

    [Fact]
    public void FromStatus_MergesAdditionalMetadata()
    {
        var error = PolymerErrorAdapter.FromStatus(
            PolymerStatusCode.ResourceExhausted,
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
            .WithMetadata("polymer.status", nameof(PolymerStatusCode.Unavailable));

        var status = PolymerErrorAdapter.ToStatus(error);

        Assert.Equal(PolymerStatusCode.Unavailable, status);
    }

    [Fact]
    public void ToStatus_FallsBackToCode()
    {
        var error = Error.From("internal failure", "internal");

        var status = PolymerErrorAdapter.ToStatus(error);

        Assert.Equal(PolymerStatusCode.Internal, status);
    }

    [Fact]
    public void ToStatus_MapsCancellationCause()
    {
        var error = Error.From("cancelled").WithCause(new OperationCanceledException());

        var status = PolymerErrorAdapter.ToStatus(error);

        Assert.Equal(PolymerStatusCode.Cancelled, status);
    }
}
