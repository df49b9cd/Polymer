namespace OmniRelay.Dispatcher;

/// <summary>
/// Enumerates the RPC shapes supported by the dispatcher.
/// </summary>
public enum ProcedureKind
{
    /// <summary>Unary request-response.</summary>
    Unary = 0,
    /// <summary>Oneway fire-and-forget.</summary>
    Oneway = 1,
    /// <summary>Server-streaming.</summary>
    Stream = 2,
    /// <summary>Client-streaming.</summary>
    ClientStream = 3,
    /// <summary>Bidirectional streaming.</summary>
    Duplex = 4
}
