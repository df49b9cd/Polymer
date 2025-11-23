using System.Runtime.CompilerServices;
using Hugo;
using static Hugo.Go;

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

    public static async ValueTask<Result<Unit>> AsResult(this ValueTask task, Func<Exception, Error>? errorFactory = null)
    {
        try
        {
            await task.ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }
        catch (Exception ex)
        {
            var error = errorFactory is null ? Error.FromException(ex) : errorFactory(ex);
            return Result.Fail<Unit>(error);
        }
    }
}
