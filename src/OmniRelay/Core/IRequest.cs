namespace OmniRelay.Core;

/// <summary>
/// Represents a typed RPC request with metadata and payload.
/// </summary>
public interface IRequest<out T>
{
    /// <summary>Gets the request metadata.</summary>
    RequestMeta Meta { get; }
    /// <summary>Gets the request payload.</summary>
    T Body { get; }
}

public sealed record Request<T>(RequestMeta Meta, T Body) : IRequest<T>
{
    /// <summary>Gets the request metadata.</summary>
    public RequestMeta Meta { get; } = Meta ?? throw new ArgumentNullException(nameof(Meta));

    public T Body { get; init; } = Body;

    /// <summary>
    /// Creates a request from a payload and optional metadata.
    /// </summary>
    /// <param name="body">The payload body.</param>
    /// <param name="meta">Optional request metadata.</param>
    /// <returns>A new request instance.</returns>
    public static Request<T> Create(T body, RequestMeta? meta = null) =>
        new(meta ?? new RequestMeta(), body);
}
