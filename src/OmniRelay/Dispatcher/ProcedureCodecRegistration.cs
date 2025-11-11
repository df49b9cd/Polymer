using System.Collections.Immutable;

namespace OmniRelay.Dispatcher;

internal sealed record ProcedureCodecRegistration(
    ProcedureCodecScope Scope,
    string? Service,
    string Procedure,
    ProcedureKind Kind,
    Type RequestType,
    Type ResponseType,
    object Codec,
    string Encoding,
    ImmutableArray<string> Aliases)
{
    public ProcedureCodecScope Scope { get; init; } = Scope;

    public string? Service { get; init; } = Service;

    public string Procedure { get; init; } = Procedure;

    public ProcedureKind Kind { get; init; } = Kind;

    public Type RequestType { get; init; } = RequestType;

    public Type ResponseType { get; init; } = ResponseType;

    public object Codec { get; init; } = Codec;

    public string Encoding { get; init; } = Encoding;

    public ImmutableArray<string> Aliases { get; init; } = Aliases;
}

