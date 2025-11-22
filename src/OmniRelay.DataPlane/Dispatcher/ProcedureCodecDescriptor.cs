namespace OmniRelay.Dispatcher;

/// <summary>
/// Stores metadata about a codec registration, including the concrete codec instance and the typed message contracts.
/// </summary>
public sealed record ProcedureCodecDescriptor(
    Type RequestType,
    Type ResponseType,
    object Codec,
    string Encoding)
{
    public Type RequestType { get; init; } = RequestType;

    public Type ResponseType { get; init; } = ResponseType;

    public object Codec { get; init; } = Codec;

    public string Encoding { get; init; } = Encoding;
}

