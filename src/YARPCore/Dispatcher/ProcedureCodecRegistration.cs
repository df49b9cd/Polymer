using System.Collections.Immutable;

namespace YARPCore.Dispatcher;

internal sealed record ProcedureCodecRegistration(
    ProcedureCodecScope Scope,
    string? Service,
    string Procedure,
    ProcedureKind Kind,
    Type RequestType,
    Type ResponseType,
    object Codec,
    string Encoding,
    ImmutableArray<string> Aliases);

