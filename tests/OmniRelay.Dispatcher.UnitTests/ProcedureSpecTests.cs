using AwesomeAssertions;
using OmniRelay.Core;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Dispatcher.UnitTests;

public class ProcedureSpecTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void FullName_ComposesServiceAndName()
    {
        var spec = new UnaryProcedureSpec(
            "svc",
            "proc",
            (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))));

        spec.FullName.Should().Be("svc::proc");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void Create_WithWhitespaceAlias_ReturnsFailure()
    {
        var result = ProcedureSpec.Create(
            "proc",
            ["valid", "  "],
            aliases => new UnaryProcedureSpec(
                "svc",
                "proc",
                (_, _) => ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty))),
                aliases: aliases));

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("dispatcher.procedure.alias_invalid");
    }
}
