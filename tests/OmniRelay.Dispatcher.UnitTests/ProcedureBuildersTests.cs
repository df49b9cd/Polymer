using NSubstitute;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class ProcedureBuildersTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void UnaryProcedureBuilder_BuildsSpecWithEncodingAndAliases()
    {
        var middleware = Substitute.For<IUnaryInboundMiddleware>();
        var builder = new UnaryProcedureBuilder()
            .Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))))
            .Use(middleware)
            .WithEncoding("json")
            .AddAlias("alias-one")
            .AddAliases(["alias-two", "alias-three"]);

        var specResult = builder.Build("svc", "proc");
        specResult.IsSuccess.ShouldBeTrue();
        var spec = specResult.Value;

        Assert.Equal("svc", spec.Service);
        Assert.Equal("proc", spec.Name);
        Assert.Equal("json", spec.Encoding);
        Assert.Contains(middleware, spec.Middleware);
        Assert.Equal(new[] { "alias-one", "alias-two", "alias-three" }, spec.Aliases);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void OnewayProcedureBuilder_WithoutHandler_Throws()
    {
        var builder = new OnewayProcedureBuilder();

        var result = builder.Build("svc", "name");
        result.IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void StreamProcedureBuilder_WithMetadata_StoresOnSpec()
    {
        var metadata = new StreamIntrospectionMetadata(new StreamChannelMetadata(StreamDirection.Server, "bounded", 5, true));

        var specResult = new StreamProcedureBuilder()
            .Handle((_, _, _) => ValueTask.FromResult(Ok<IStreamCall>(new TestHelpers.DummyStreamCall())))
            .WithMetadata(metadata)
            .Build("svc", "stream");
        specResult.IsSuccess.ShouldBeTrue();
        Assert.Equal(metadata, specResult.Value.Metadata);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ClientStreamProcedureBuilder_WithMetadata_StoresOnSpec()
    {
        var metadata = new ClientStreamIntrospectionMetadata(new StreamChannelMetadata(StreamDirection.Client, "bounded", 10, false), false);

        var specResult = new ClientStreamProcedureBuilder()
            .Handle((_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))))
            .WithMetadata(metadata)
            .Build("svc", "client");
        specResult.IsSuccess.ShouldBeTrue();
        Assert.Equal(metadata, specResult.Value.Metadata);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void DuplexProcedureBuilder_WithMetadata_StoresOnSpec()
    {
        var metadata = new DuplexIntrospectionMetadata(
            new StreamChannelMetadata(StreamDirection.Client, "bounded", 1, true),
            new StreamChannelMetadata(StreamDirection.Server, "bounded", 1, true));

        var specResult = new DuplexProcedureBuilder()
            .Handle((_, _) => ValueTask.FromResult(Ok<IDuplexStreamCall>(new TestHelpers.DummyDuplexStreamCall())))
            .WithMetadata(metadata)
            .Build("svc", "duplex");
        specResult.IsSuccess.ShouldBeTrue();
        Assert.Equal(metadata, specResult.Value.Metadata);
    }
}
