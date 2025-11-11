namespace OmniRelay.Core;

/// <summary>
/// Represents a typed RPC response with metadata and payload.
/// </summary>
public interface IResponse<out T>
{
    /// <summary>Gets the response metadata.</summary>
    ResponseMeta Meta { get; }
    /// <summary>Gets the response payload.</summary>
    T Body { get; }
}

public sealed record Response<T>(ResponseMeta Meta, T Body) : IResponse<T>
{
    /// <summary>Gets the response metadata.</summary>
    public ResponseMeta Meta { get; } = Meta ?? throw new ArgumentNullException(nameof(Meta));

    public T Body { get; init; } = Body;

    /// <summary>
    /// Creates a response from a payload and optional metadata.
    /// </summary>
    /// <param name="body">The payload body.</param>
    /// <param name="meta">Optional response metadata.</param>
    /// <returns>A new response instance.</returns>
    public static Response<T> Create(T body, ResponseMeta? meta = null) =>
        new(meta ?? new ResponseMeta(), body);
}
