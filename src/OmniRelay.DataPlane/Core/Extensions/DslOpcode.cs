namespace OmniRelay.Core.Extensions;

/// <summary>
/// Opcodes supported by the built-in DSL interpreter.
/// </summary>
internal enum DslOpcode : byte
{
    /// <summary>Return the current buffer as-is and stop execution.</summary>
    Ret,

    /// <summary>Replace the buffer with a literal string argument.</summary>
    Set,

    /// <summary>Append a literal string argument to the buffer.</summary>
    Append,

    /// <summary>Uppercase the current buffer using invariant culture.</summary>
    Upper,

    /// <summary>Lowercase the current buffer using invariant culture.</summary>
    Lower,

    /// <summary>Truncate the buffer to the provided length argument.</summary>
    Truncate
}
