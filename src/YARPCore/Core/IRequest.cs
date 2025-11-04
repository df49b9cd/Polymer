namespace YARPCore.Core;

public interface IRequest<out T>
{
    RequestMeta Meta { get; }
    T Body { get; }
}

public sealed record Request<T>(RequestMeta Meta, T Body) : IRequest<T>
{
    public RequestMeta Meta { get; } = Meta ?? throw new ArgumentNullException(nameof(Meta));

    public static Request<T> Create(T body, RequestMeta? meta = null) =>
        new(meta ?? new RequestMeta(), body);
}
