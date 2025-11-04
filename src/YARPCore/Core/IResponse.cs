namespace YARPCore.Core;

public interface IResponse<out T>
{
    ResponseMeta Meta { get; }
    T Body { get; }
}

public sealed record Response<T>(ResponseMeta Meta, T Body) : IResponse<T>
{
    public ResponseMeta Meta { get; } = Meta ?? throw new ArgumentNullException(nameof(Meta));

    public static Response<T> Create(T body, ResponseMeta? meta = null) =>
        new(meta ?? new ResponseMeta(), body);
}
