using System.Collections.Immutable;
using OmniRelay.Cli.Modules;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.UnitTests;

public sealed class IntrospectModuleTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void PrintIntrospectionSummary_WritesExpectedSections()
    {
        var snapshot = new DispatcherIntrospection(
            Service: "orders",
            Status: DispatcherStatus.Running,
            Procedures: new ProcedureGroups(
                [new("orders::ping", "application/json", ImmutableArray<string>.Empty)],
                ImmutableArray<ProcedureDescriptor>.Empty,
                ImmutableArray<StreamProcedureDescriptor>.Empty,
                ImmutableArray<ClientStreamProcedureDescriptor>.Empty,
                ImmutableArray<DuplexProcedureDescriptor>.Empty),
            Components: [new("cache", "Cache", ImmutableArray<string>.Empty)],
            Outbounds:
            [
                new(
                    Service: "billing",
                    Unary: [new("grpc", "GrpcUnaryOutbound", null)],
                    Oneway: ImmutableArray<OutboundBindingDescriptor>.Empty,
                    Stream: ImmutableArray<OutboundBindingDescriptor>.Empty,
                    ClientStream: ImmutableArray<OutboundBindingDescriptor>.Empty,
                    Duplex: ImmutableArray<OutboundBindingDescriptor>.Empty)
            ],
            Middleware: new MiddlewareSummary(
                ImmutableArray.Create("inbound-a"),
                ImmutableArray.Create("outbound-a"),
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty,
                ImmutableArray<string>.Empty));

        var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            ProgramIntrospectModule.PrintIntrospectionSummary(snapshot);
        }
        finally
        {
            Console.SetOut(original);
        }

        var output = writer.ToString();
        output.ShouldContain("Service: orders");
        output.ShouldContain("Status: Running");
        output.ShouldContain("Procedures:");
        output.ShouldContain("Lifecycle components:");
        output.ShouldContain("Outbounds:");
        output.ShouldContain("Middleware (Inbound → Outbound):");
    }
}
