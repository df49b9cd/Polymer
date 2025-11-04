using System.Runtime.CompilerServices;

namespace YARPCore.Core;

public static class AsyncDisposableHelpers
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IAsyncDisposable AsAsyncDisposable<T>(this T disposable, out T disposableOut) where T : notnull, IAsyncDisposable =>
        disposableOut = disposable;
}
