using Hugo;

namespace OmniRelay.Dispatcher;

/// <summary>Shared error codes for deterministic coordinator/state-store wiring.</summary>
internal static class ResourceLeaseDeterministicErrors
{
    private const string OptionsRequiredCode = "resourcelease.deterministic.options_required";
    private const string StateStoreRequiredCode = "resourcelease.deterministic.state_store_required";
    private const string RootDirectoryRequiredCode = "resourcelease.deterministic.root_required";
    private const string DispatcherRequiredCode = "resourcelease.dispatcher.dispatcher_required";
    private const string DispatcherOptionsRequiredCode = "resourcelease.dispatcher.options_required";

    public static Error OptionsRequired() =>
        Error.From("Deterministic options are required.", OptionsRequiredCode);

    public static Error StateStoreRequired() =>
        Error.From("A deterministic state store is required.", StateStoreRequiredCode);

    public static Error RootDirectoryRequired() =>
        Error.From("Root directory is required for deterministic state store.", RootDirectoryRequiredCode);

    public static Error DispatcherRequired() =>
        Error.From("Dispatcher instance is required.", DispatcherRequiredCode);

    public static Error DispatcherOptionsRequired() =>
        Error.From("Resource lease dispatcher options are required.", DispatcherOptionsRequiredCode);
}
