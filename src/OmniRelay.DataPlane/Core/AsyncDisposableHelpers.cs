using System.Runtime.CompilerServices;

namespace OmniRelay.Core;

/// <summary>
/// Helpers for concise async disposal patterns.
/// </summary>
public static class AsyncDisposableHelpers
{
    /// <summary>
    /// Returns the instance as <see cref="IAsyncDisposable"/> while also assigning it to an out variable.
    /// Useful for composing patterns like <c>await using (obj.AsAsyncDisposable(out var local))</c>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IAsyncDisposable AsAsyncDisposable<T>(this T disposable, out T disposableOut) where T : notnull, IAsyncDisposable =>
        disposableOut = disposable;
}
