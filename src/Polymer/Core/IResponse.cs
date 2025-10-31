using System;

namespace Polymer.Core;

public interface IResponse<out T>
{
    ResponseMeta Meta { get; }
    T Body { get; }
}

public sealed record Response<T> : IResponse<T>
{
    public ResponseMeta Meta { get; }
    public T Body { get; }

    public Response(ResponseMeta meta, T body)
    {
        Meta = meta ?? throw new ArgumentNullException(nameof(meta));
        Body = body;
    }

    public static Response<T> Create(T body, ResponseMeta? meta = null) =>
        new(meta ?? new ResponseMeta(), body);
}
