namespace OmniRelay.Dispatcher;

/// <summary>
/// Indicates the lifecycle state of the dispatcher.
/// </summary>
public enum DispatcherStatus
{
    /// <summary>Constructed but not started.</summary>
    Created = 0,
    /// <summary>Currently starting components.</summary>
    Starting = 1,
    /// <summary>Running and able to process calls.</summary>
    Running = 2,
    /// <summary>Currently stopping components.</summary>
    Stopping = 3,
    /// <summary>Stopped and not processing calls.</summary>
    Stopped = 4
}
// No changes needed here; ProcedureKind is in a different file. Proceed to document ProcedureKind and IDispatcherAware already handled.
