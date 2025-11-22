using OmniRelay.Core;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class ProcedureRegistryTests
{
    private static readonly UnaryInboundHandler UnaryHandler =
        (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryGet_WithRegisteredAlias_ReturnsSpec()
    {
        var registry = new ProcedureRegistry();
        var spec = new UnaryProcedureSpec("svc", "primary", UnaryHandler, aliases: ["alias"]);

        registry.Register(spec);

        Assert.True(registry.TryGet("svc", "alias", ProcedureKind.Unary, out var resolved));
        Assert.Same(spec, resolved);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithConflictingName_Throws()
    {
        var registry = new ProcedureRegistry();
        var first = new UnaryProcedureSpec("svc", "name", UnaryHandler);
        var second = new UnaryProcedureSpec("svc", "name", UnaryHandler);

        registry.Register(first);

        Assert.Throws<InvalidOperationException>(() => registry.Register(second));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithDuplicateAlias_Throws()
    {
        var registry = new ProcedureRegistry();
        var spec = new UnaryProcedureSpec("svc", "name", UnaryHandler, aliases: ["dup", "dup"]);

        Assert.Throws<InvalidOperationException>(() => registry.Register(spec));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Register_WithDuplicateWildcardPatternAcrossProcedures_Throws()
    {
        var registry = new ProcedureRegistry();

        registry.Register(new UnaryProcedureSpec("svc", "first", UnaryHandler, aliases: ["foo*"]));

        Assert.Throws<InvalidOperationException>(() =>
            registry.Register(new UnaryProcedureSpec("svc", "second", UnaryHandler, aliases: ["foo*"])));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryGet_WithWildcardAlias_PrefersMostSpecific()
    {
        var registry = new ProcedureRegistry();

        var general = new UnaryProcedureSpec("svc", "general", UnaryHandler, aliases: ["foo.*"]);
        var specific = new UnaryProcedureSpec("svc", "specific", UnaryHandler, aliases: ["foo.bar*"]);

        registry.Register(general);
        registry.Register(specific);

        Assert.True(registry.TryGet("svc", "foo.bar", ProcedureKind.Unary, out var resolved));
        Assert.Same(specific, resolved);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryGet_IsCaseInsensitive_ForServiceAndAlias()
    {
        var registry = new ProcedureRegistry();
        var spec = new UnaryProcedureSpec("Svc", "Echo", UnaryHandler, aliases: ["Alias"]);

        registry.Register(spec);

        Assert.True(registry.TryGet("svc", "alias", ProcedureKind.Unary, out var resolved));
        Assert.Same(spec, resolved);
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void TryGet_WithEqualSpecificityWildcards_PrefersFirstRegistered()
    {
        var registry = new ProcedureRegistry();

        var first = new UnaryProcedureSpec("svc", "first", UnaryHandler, aliases: ["foo*bar"]);
        var second = new UnaryProcedureSpec("svc", "second", UnaryHandler, aliases: ["foo?bar"]);

        registry.Register(first);
        registry.Register(second);

        Assert.True(registry.TryGet("svc", "fooxbar", ProcedureKind.Unary, out var resolved));
        Assert.Same(first, resolved);
    }
}
