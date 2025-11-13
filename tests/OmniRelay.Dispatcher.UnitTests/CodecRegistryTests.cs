using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class CodecRegistryTests
{
    [Fact]
    public void Constructor_WithWhitespaceService_Throws()
    {
        Assert.Throws<ArgumentException>(() => new CodecRegistry(" ", []));
    }

    [Fact]
    public void RegisterInbound_ThenResolve_ReturnsCodec()
    {
        var codec = new TestHelpers.TestCodec<string, string>();
        var registry = new CodecRegistry("svc");

        registry.RegisterInbound("proc", ProcedureKind.Unary, codec);

        Assert.True(registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out var descriptor));
        Assert.Same(codec, descriptor.Codec);
        Assert.True(registry.TryResolve<string, string>(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out var typed));
        Assert.Same(codec, typed);
    }

    [Fact]
    public void RegisterOutbound_WithDuplicate_Throws()
    {
        var codec = new TestHelpers.TestCodec<int, int>();
        var registry = new CodecRegistry("svc");

        registry.RegisterOutbound("remote", "proc", ProcedureKind.Unary, codec);

        Assert.Throws<InvalidOperationException>(() =>
            registry.RegisterOutbound("remote", "proc", ProcedureKind.Unary, codec));
    }

    [Fact]
    public void Register_WithAliases_ResolvesAll()
    {
        var codec = new TestHelpers.TestCodec<int, int>();
        var registry = new CodecRegistry("svc");

        registry.RegisterInbound("primary", ProcedureKind.Unary, codec, ["alias-1", "alias-2"]);

        Assert.True(registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "alias-1", ProcedureKind.Unary, out var first));
        Assert.True(registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "alias-2", ProcedureKind.Unary, out var second));
        Assert.Same(codec, first.Codec);
        Assert.Same(codec, second.Codec);
    }

    [Fact]
    public void TryResolve_WithTypeMismatch_Throws()
    {
        var codec = new TestHelpers.TestCodec<int, string>();
        var registry = new CodecRegistry("svc");

        registry.RegisterInbound("proc", ProcedureKind.Unary, codec);

        Assert.Throws<InvalidOperationException>(() =>
            registry.TryResolve<string, string>(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out _));
    }

    [Fact]
    public void Snapshot_ReturnsRegisteredEntries()
    {
        var codec = new TestHelpers.TestCodec<int, int>();
        var registration = new ProcedureCodecRegistration(
            ProcedureCodecScope.Outbound,
            "remote",
            "proc",
            ProcedureKind.Unary,
            typeof(int),
            typeof(int),
            codec,
            codec.Encoding,
            []);

        var registry = new CodecRegistry("svc", [registration]);

        var snapshot = registry.Snapshot();

        Assert.Single(snapshot);
        var entry = snapshot[0];
        Assert.Equal(ProcedureCodecScope.Outbound, entry.Scope);
        Assert.Equal("remote", entry.Service);
        Assert.Equal("proc", entry.Procedure);
        Assert.Equal(ProcedureKind.Unary, entry.Kind);
        Assert.Same(codec, entry.Descriptor.Codec);
    }
}
