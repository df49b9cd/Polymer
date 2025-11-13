using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class ProcedureSpecTests
{
    [Fact]
    public void FullName_ComposesServiceAndName()
    {
        var spec = new UnaryProcedureSpec(
            "svc",
            "proc",
            (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        Assert.Equal("svc::proc", spec.FullName);
    }

    [Fact]
    public void Constructor_WithWhitespaceAlias_Throws()
    {
        var middleware = Array.Empty<IUnaryInboundMiddleware>();

        Assert.Throws<ArgumentException>(() =>
            new UnaryProcedureSpec(
                "svc",
                "proc",
                (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))),
                aliases: ["valid", "  "]));
    }
}
