using AwesomeAssertions;
using static AwesomeAssertions.FluentActions;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using Xunit;

namespace OmniRelay.Tests.Dispatcher;

public class ProcedureBuilderTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void UnaryBuilder_HandleRequired()
    {
        var builder = new UnaryProcedureBuilder();
        var result = builder.Build("svc", "proc");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("dispatcher.procedure.handler_missing");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void OnewayBuilder_HandleRequired()
    {
        var builder = new OnewayProcedureBuilder();
        var result = builder.Build("svc", "proc");

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("dispatcher.procedure.handler_missing");
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        var specResult = builder.Build("svc", "name");
        specResult.IsSuccess.Should().BeTrue();
        var spec = specResult.Value;

        spec.Encoding.Should().Be("json");
        spec.Middleware.Should().ContainSingle().Which.Should().BeSameAs(middleware);
        spec.Aliases.Should().Equal("foo::bar", "one", "two");
        spec.Metadata.Should().Be(metadata);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void AddAlias_ThrowsForWhitespace()
    {
        var builder = new UnaryProcedureBuilder().Handle((req, _) => ValueTask.FromResult(Result.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
        Invoking(() => builder.AddAlias(" "))
            .Should().Throw<ArgumentException>();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Use_NullThrows()
    {
        var builder = new UnaryProcedureBuilder().Handle((req, _) => ValueTask.FromResult(Result.Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));
        Invoking(() => builder.Use(null!))
            .Should().Throw<ArgumentNullException>();
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
