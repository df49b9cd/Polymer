using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Tests.Dispatcher;

public class ProcedureBuilderTests
{
    [Fact]
    public void UnaryBuilder_HandleRequired()
    {
        var builder = new UnaryProcedureBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.Build("svc", "proc"));
    }

    [Fact]
    public void OnewayBuilder_HandleRequired()
    {
        var builder = new OnewayProcedureBuilder();
        Assert.Throws<InvalidOperationException>(() => builder.Build("svc", "proc"));
    }

    [Fact]
    public void StreamBuilder_TrimsAliasesAndCapturesMetadata()
    {
        StreamIntrospectionMetadata metadata = new(new StreamChannelMetadata(StreamDirection.Server, "bounded", 32, true));
        var middleware = new RecordingStreamMiddleware();

        var builder = new StreamProcedureBuilder()
            .Handle((req, _, _) => ValueTask.FromResult(Result.Ok<IStreamCall>(ServerStreamCall.Create(req.Meta))))
            .AddAlias("  foo::bar  ")
            .AddAliases(["one", "two"])
            .Use(middleware)
            .WithEncoding("json")
            .WithMetadata(metadata);

        var spec = builder.Build("svc", "name");

        Assert.Equal("json", spec.Encoding);
        Assert.Same(middleware, Assert.Single(spec.Middleware));
        Assert.Equal(new[] { "foo::bar", "one", "two" }, spec.Aliases);
        Assert.Equal(metadata, spec.Metadata);
    }

    [Fact]
    public void AddAlias_ThrowsForWhitespace()
    {
        var builder = new UnaryProcedureBuilder().Handle((req, _) => ValueTask.FromResult(Result.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
        Assert.Throws<ArgumentException>(() => builder.AddAlias(" "));
    }

    [Fact]
    public void Use_NullThrows()
    {
        var builder = new UnaryProcedureBuilder().Handle((req, _) => ValueTask.FromResult(Result.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
        Assert.Throws<ArgumentNullException>(() => builder.Use(null!));
    }

    private sealed class RecordingStreamMiddleware : IStreamInboundMiddleware
    {
        public ValueTask<Result<IStreamCall>> InvokeAsync(
            IRequest<ReadOnlyMemory<byte>> request,
            StreamCallOptions options,
            CancellationToken cancellationToken,
            StreamInboundHandler next) => next(request, options, cancellationToken);
    }
}
