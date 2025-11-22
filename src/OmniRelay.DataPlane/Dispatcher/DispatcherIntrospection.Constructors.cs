using System.Collections.Immutable;
using OmniRelay.Core;

namespace OmniRelay.Dispatcher;

public partial record DispatcherIntrospection
{
    // Parameterless ctor for System.Text.Json deserialization with source-gen.
    public DispatcherIntrospection()
        : this(
            string.Empty,
            DispatcherStatus.Created,
            new ProcedureGroups(
                ImmutableArray<ProcedureDescriptor>.Empty,
                ImmutableArray<ProcedureDescriptor>.Empty,
                ImmutableArray<StreamProcedureDescriptor>.Empty,
                ImmutableArray<ClientStreamProcedureDescriptor>.Empty,
                ImmutableArray<DuplexProcedureDescriptor>.Empty),
            ImmutableArray<LifecycleComponentDescriptor>.Empty,
            ImmutableArray<OutboundDescriptor>.Empty,
            MiddlewareSummary.Empty,
            DeploymentMode.InProc,
            ImmutableArray<string>.Empty)
    {
    }

    // Backward-compatible constructor used by existing tests/tools.
    public DispatcherIntrospection(
        string service,
        DispatcherStatus status,
        ProcedureGroups procedures,
        ImmutableArray<LifecycleComponentDescriptor> components,
        ImmutableArray<OutboundDescriptor> outbounds,
        MiddlewareSummary middleware)
        : this(service, status, procedures, components, outbounds, middleware, DeploymentMode.InProc, ImmutableArray<string>.Empty)
    {
    }
}
