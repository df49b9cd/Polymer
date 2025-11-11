using OmniRelay.Core;

namespace OmniRelay.Transport.Http.Middleware;

/// <summary>
/// Describes the type of HTTP call being made by the outbound transport.
/// </summary>
public enum HttpOutboundCallKind
{
    Unary = 0,
    Oneway = 1
}

/// <summary>
/// Handler representing the continuation of an HTTP middleware pipeline.
/// </summary>
/// <param name="context">The call context that can be inspected or mutated.</param>
/// <param name="cancellationToken">Cancellation token for the call.</param>
/// <returns>The HTTP response message.</returns>
public delegate ValueTask<HttpResponseMessage> HttpClientMiddlewareHandler(
    HttpClientMiddlewareContext context,
    CancellationToken cancellationToken);

/// <summary>
/// Contract implemented by HTTP transport middleware.
/// </summary>
public interface IHttpClientMiddleware
{
    /// <summary>
    /// Invokes the middleware.
    /// </summary>
    /// <param name="context">The HTTP request context.</param>
    /// <param name="nextHandler">Continuation delegate representing the remaining pipeline.</param>
    /// <param name="cancellationToken">Cancellation token for the call.</param>
    ValueTask<HttpResponseMessage> InvokeAsync(
        HttpClientMiddlewareContext context,
        HttpClientMiddlewareHandler nextHandler,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provides access to request metadata and shared state for HTTP outbound middleware.
/// </summary>
public sealed class HttpClientMiddlewareContext(
    HttpRequestMessage request,
    RequestMeta requestMeta,
    HttpOutboundCallKind callKind,
    HttpCompletionOption completionOption)
{
    private Dictionary<string, object?>? _items;

    public HttpRequestMessage Request { get; } = request ?? throw new ArgumentNullException(nameof(request));

    public RequestMeta RequestMeta { get; } = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));

    public HttpOutboundCallKind CallKind { get; } = callKind;

    /// <summary>
    /// Gets or sets the <see cref="HttpCompletionOption"/> to use when sending the request.
    /// </summary>
    public HttpCompletionOption CompletionOption { get; set; } = completionOption;

    /// <summary>
    /// Provides a per-call bag for middleware to share data.
    /// </summary>
    public IDictionary<string, object?> Items =>
        _items ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tries to get a strongly typed item from the context.
    /// </summary>
    public bool TryGetItem<T>(string key, out T? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_items is not null && _items.TryGetValue(key, out var boxed) && boxed is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Sets a strongly typed item on the context.
    /// </summary>
    public void SetItem<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        Items[key] = value;
    }
}

/// <summary>
/// Helper that composes HTTP client middleware into an executable pipeline.
/// </summary>
public static class HttpClientMiddlewareComposer
{
    public static HttpClientMiddlewareHandler Compose(
        IReadOnlyList<IHttpClientMiddleware>? middleware,
        HttpClientMiddlewareHandler terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        if (middleware is null || middleware.Count == 0)
        {
            return terminal;
        }

        var next = terminal;
        for (var index = middleware.Count - 1; index >= 0; index--)
        {
            var current = middleware[index];
            var captured = next;
            next = (context, cancellationToken) =>
                current.InvokeAsync(context, captured, cancellationToken);
        }

        return next;
    }
}
