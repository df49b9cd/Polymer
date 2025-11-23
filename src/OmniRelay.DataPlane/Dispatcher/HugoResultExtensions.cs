using Hugo;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Helper extensions to bridge Hugo <see cref="Result{T}"/> values with exception-based workflows when needed.
/// </summary>
public static class HugoResultExtensions
{
    /// <summary>
    /// Returns the successful value or throws a <see cref="ResultException"/> when the result is failed.
    /// </summary>
    [Obsolete("Avoid throwing; prefer Result.ValueOr/Match or propagate the failure Result.")]
    public static T ValueOrThrow<T>(this Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }

        return result.Value;
    }

    /// <summary>
    /// Throws a <see cref="ResultException"/> when the result is failed.
    /// </summary>
    [Obsolete("Avoid throwing; prefer checking IsFailure or propagating the Result.")]
    public static void ThrowIfFailure(this Result<Unit> result)
    {
        if (result.IsFailure)
        {
            throw new ResultException(result.Error!);
        }
    }
}
