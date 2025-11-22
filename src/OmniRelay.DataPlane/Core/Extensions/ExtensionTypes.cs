namespace OmniRelay.Core.Extensions;

public enum ExtensionType
{
    Dsl,
    Wasm,
    Native
}

public enum WasmRuntime
{
    V8,
    Wasmtime,
    Wamr
}

public sealed record ExtensionFailurePolicy(bool FailOpen, bool ReloadOnFailure, TimeSpan? Cooldown = null);
