using System;

namespace Polymer.Core;

public interface IRequest<out T>
{
    RequestMeta Meta { get; }
    T Body { get; }
}

public sealed record Request<T> : IRequest<T>
{
    public RequestMeta Meta { get; }
    public T Body { get; }

    public Request(RequestMeta meta, T body)
    {
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        Body = body;
    }

    public static Request<T> Create(T body, RequestMeta? meta = null) =>
        new(meta ?? new RequestMeta(), body);
}
