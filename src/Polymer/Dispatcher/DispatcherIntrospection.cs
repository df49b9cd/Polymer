using System.Collections.Immutable;

namespace Polymer.Dispatcher;

public sealed record DispatcherIntrospection(
    string Service,
    DispatcherStatus Status,
    ProcedureGroups Procedures,
    ImmutableArray<LifecycleComponentDescriptor> Components,
    ImmutableArray<OutboundDescriptor> Outbounds,
    MiddlewareSummary Middleware);

public sealed record ProcedureGroups(
    ImmutableArray<ProcedureDescriptor> Unary,
    ImmutableArray<ProcedureDescriptor> Oneway,
    ImmutableArray<StreamProcedureDescriptor> Stream,
    ImmutableArray<ClientStreamProcedureDescriptor> ClientStream,
    ImmutableArray<DuplexProcedureDescriptor> Duplex);

public sealed record ProcedureDescriptor(string Name, string? Encoding);

public sealed record StreamProcedureDescriptor(string Name, string? Encoding, StreamIntrospectionMetadata Metadata);

public sealed record ClientStreamProcedureDescriptor(string Name, string? Encoding, ClientStreamIntrospectionMetadata Metadata);

public sealed record DuplexProcedureDescriptor(string Name, string? Encoding, DuplexIntrospectionMetadata Metadata);

public sealed record LifecycleComponentDescriptor(string Name, string ComponentType);

public sealed record OutboundDescriptor(
    string Service,
    ImmutableArray<OutboundBindingDescriptor> Unary,
    ImmutableArray<OutboundBindingDescriptor> Oneway,
    ImmutableArray<OutboundBindingDescriptor> Stream,
    ImmutableArray<OutboundBindingDescriptor> ClientStream,
    ImmutableArray<OutboundBindingDescriptor> Duplex);

public sealed record OutboundBindingDescriptor(string Key, string ImplementationType, object? Metadata);

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
