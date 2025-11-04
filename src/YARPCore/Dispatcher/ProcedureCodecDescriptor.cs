namespace YARPCore.Dispatcher;

/// <summary>
/// Stores metadata about a codec registration, including the concrete codec instance and the typed message contracts.
/// </summary>
public sealed record ProcedureCodecDescriptor(
    Type RequestType,
    Type ResponseType,
    object Codec,
    string Encoding);

