using System.Collections.Immutable;

namespace Polymer.Dispatcher;

public sealed record DispatcherIntrospection(
    string Service,
    DispatcherStatus Status,
    ImmutableArray<ProcedureDescriptor> Procedures,
    ImmutableArray<LifecycleComponentDescriptor> Components,
    ImmutableArray<OutboundSummary> Outbounds,
    MiddlewareSummary Middleware);

public sealed record ProcedureDescriptor(string Name, ProcedureKind Kind, string? Encoding);

public sealed record LifecycleComponentDescriptor(string Name, string ComponentType);

public sealed record OutboundSummary(string Service, int UnaryCount, int OnewayCount, int StreamCount, int ClientStreamCount, int DuplexCount);

public sealed record MiddlewareSummary(
    ImmutableArray<string> InboundUnary,
    ImmutableArray<string> InboundOneway,
    ImmutableArray<string> InboundStream,
    ImmutableArray<string> InboundClientStream,
    ImmutableArray<string> InboundDuplex,
    ImmutableArray<string> OutboundUnary,
    ImmutableArray<string> OutboundOneway,
    ImmutableArray<string> OutboundStream,
    ImmutableArray<string> OutboundClientStream,
    ImmutableArray<string> OutboundDuplex);
