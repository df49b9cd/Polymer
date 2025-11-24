using AwesomeAssertions;
using Hugo;
using Xunit;
using static AwesomeAssertions.FluentActions;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class CodecRegistryTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void Constructor_WithWhitespaceService_Throws()
    {
        var result = CodecRegistry.Create(" ", []);
        result.IsFailure.Should().BeTrue();
        result.Error.Should().NotBeNull();
        result.Error!.Code.Should().Be("dispatcher.codec.local_service_required");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterInbound_ThenResolve_ReturnsCodec()
    {
        var codec = new TestHelpers.TestCodec<string, string>();
        var registry = CodecRegistry.Create("svc").ValueOrThrow();

        registry.RegisterInbound("proc", ProcedureKind.Unary, codec).IsSuccess.Should().BeTrue();

        registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out var descriptor).Should().BeTrue();
        descriptor.Should().NotBeNull();
        descriptor!.Codec.Should().BeSameAs(codec);
        registry.TryResolve<string, string>(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out var typed).Should().BeTrue();
        typed.Should().NotBeNull();
        typed!.Should().BeSameAs(codec);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void RegisterOutbound_WithDuplicate_Throws()
    {
        var codec = new TestHelpers.TestCodec<int, int>();
        var registry = CodecRegistry.Create("svc").ValueOrThrow();

        registry.RegisterOutbound("remote", "proc", ProcedureKind.Unary, codec).IsSuccess.Should().BeTrue();

        var duplicate = registry.RegisterOutbound("remote", "proc", ProcedureKind.Unary, codec);
        duplicate.IsFailure.Should().BeTrue();
        duplicate.Error!.Code.Should().Be("dispatcher.codec.duplicate");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithAliases_ResolvesAll()
    {
        var codec = new TestHelpers.TestCodec<int, int>();
        var registry = CodecRegistry.Create("svc").ValueOrThrow();

        registry.RegisterInbound("primary", ProcedureKind.Unary, codec, ["alias-1", "alias-2"]).IsSuccess.Should().BeTrue();

        registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "alias-1", ProcedureKind.Unary, out var first).Should().BeTrue();
        registry.TryResolve(ProcedureCodecScope.Inbound, "svc", "alias-2", ProcedureKind.Unary, out var second).Should().BeTrue();
        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Codec.Should().BeSameAs(codec);
        second!.Codec.Should().BeSameAs(codec);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryResolve_WithTypeMismatch_Throws()
    {
        var codec = new TestHelpers.TestCodec<int, string>();
        var registry = CodecRegistry.Create("svc").ValueOrThrow();

        registry.RegisterInbound("proc", ProcedureKind.Unary, codec).IsSuccess.Should().BeTrue();

        Invoking(() =>
            registry.TryResolve<string, string>(ProcedureCodecScope.Inbound, "svc", "proc", ProcedureKind.Unary, out _))
            .Should().Throw<InvalidOperationException>();
    }

    [Fact(Timeout = TestTimeouts.Default)]
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

        var registry = CodecRegistry.Create("svc", [registration]).ValueOrThrow();

        var snapshot = registry.Snapshot();

        snapshot.Should().ContainSingle();
        var entry = snapshot[0];
        entry.Scope.Should().Be(ProcedureCodecScope.Outbound);
        entry.Service.Should().Be("remote");
        entry.Procedure.Should().Be("proc");
        entry.Kind.Should().Be(ProcedureKind.Unary);
        entry.Descriptor.Codec.Should().BeSameAs(codec);
    }
}
