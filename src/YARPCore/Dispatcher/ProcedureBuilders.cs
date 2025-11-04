using System.Collections.Immutable;
using YARPCore.Core.Middleware;
using YARPCore.Core.Transport;

namespace YARPCore.Dispatcher;

/// <summary>
/// Base class for procedure builders that accumulate middleware, aliases, and encoding metadata.
/// </summary>
/// <typeparam name="TBuilder">Concrete builder type.</typeparam>
/// <typeparam name="TMiddleware">Middleware interface for the procedure.</typeparam>
public abstract class ProcedureBuilderBase<TBuilder, TMiddleware>
    where TBuilder : ProcedureBuilderBase<TBuilder, TMiddleware>
{
    private readonly List<TMiddleware> _middleware = [];
    private readonly List<string> _aliases = [];
    private string? _encoding;

    protected TBuilder Builder => (TBuilder)this;

    /// <summary>
    /// Adds middleware to the per-procedure pipeline. Middleware is executed in registration order after global middleware.
    /// </summary>
    public TBuilder Use(TMiddleware middleware)
    {
        if (middleware is null)
        {
            throw new ArgumentNullException(nameof(middleware));
        }

        _middleware.Add(middleware);
        return Builder;
    }

    /// <summary>
    /// Sets the preferred encoding for the procedure. Passing <c>null</c> clears any previously configured value.
    /// </summary>
    public TBuilder WithEncoding(string? encoding)
    {
        _encoding = encoding;
        return Builder;
    }

    /// <summary>
    /// Adds an alias that resolves to the same procedure. Aliases preserve the order they are registered.
    /// </summary>
    public TBuilder AddAlias(string alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            throw new ArgumentException("Alias cannot be null or whitespace.", nameof(alias));
        }

        _aliases.Add(alias.Trim());
        return Builder;
    }

    /// <summary>
    /// Adds multiple aliases in the order supplied.
    /// </summary>
    public TBuilder AddAliases(IEnumerable<string> aliases)
    {
        ArgumentNullException.ThrowIfNull(aliases);

        foreach (var alias in aliases)
        {
            AddAlias(alias);
        }

        return Builder;
    }

    internal string? GetEncoding() => _encoding;

    internal IReadOnlyList<TMiddleware> GetMiddlewareSnapshot() =>
        _middleware.Count == 0 ? Array.Empty<TMiddleware>() : _middleware.ToImmutableArray();

    internal IReadOnlyList<string> GetAliasSnapshot() =>
        _aliases.Count == 0 ? Array.Empty<string>() : _aliases.ToImmutableArray();
}

/// <summary>
/// Fluent builder for unary procedure registration.
/// </summary>
public sealed class UnaryProcedureBuilder : ProcedureBuilderBase<UnaryProcedureBuilder, IUnaryInboundMiddleware>
{
    private UnaryInboundDelegate? _handler;

    public UnaryProcedureBuilder()
    {
    }

    internal UnaryProcedureBuilder(UnaryInboundDelegate handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the unary handler. Must be supplied exactly once.
    /// </summary>
    public UnaryProcedureBuilder Handle(UnaryInboundDelegate handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    internal UnaryProcedureSpec Build(string service, string name)
    {
        if (_handler is null)
        {
            throw new InvalidOperationException(
                $"Unary procedure '{name}' requires a handler. Call {nameof(Handle)}(...) during registration.");
        }

        return new UnaryProcedureSpec(
            service,
            name,
            _handler,
            GetEncoding(),
            GetMiddlewareSnapshot(),
            GetAliasSnapshot());
    }
}

/// <summary>
/// Fluent builder for oneway procedure registration.
/// </summary>
public sealed class OnewayProcedureBuilder : ProcedureBuilderBase<OnewayProcedureBuilder, IOnewayInboundMiddleware>
{
    private OnewayInboundDelegate? _handler;

    public OnewayProcedureBuilder()
    {
    }

    internal OnewayProcedureBuilder(OnewayInboundDelegate handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the oneway handler. Must be supplied exactly once.
    /// </summary>
    public OnewayProcedureBuilder Handle(OnewayInboundDelegate handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    internal OnewayProcedureSpec Build(string service, string name)
    {
        if (_handler is null)
        {
            throw new InvalidOperationException(
                $"Oneway procedure '{name}' requires a handler. Call {nameof(Handle)}(...) during registration.");
        }

        return new OnewayProcedureSpec(
            service,
            name,
            _handler,
            GetEncoding(),
            GetMiddlewareSnapshot(),
            GetAliasSnapshot());
    }
}

/// <summary>
/// Fluent builder for server streaming procedure registration.
/// </summary>
public sealed class StreamProcedureBuilder : ProcedureBuilderBase<StreamProcedureBuilder, IStreamInboundMiddleware>
{
    private StreamInboundDelegate? _handler;
    private StreamIntrospectionMetadata? _metadata;

    public StreamProcedureBuilder()
    {
    }

    internal StreamProcedureBuilder(StreamInboundDelegate handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the stream handler. Must be supplied exactly once.
    /// </summary>
    public StreamProcedureBuilder Handle(StreamInboundDelegate handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Overrides the introspection metadata reported for the response stream.
    /// </summary>
    public StreamProcedureBuilder WithMetadata(StreamIntrospectionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    internal StreamProcedureSpec Build(string service, string name)
    {
        if (_handler is null)
        {
            throw new InvalidOperationException(
                $"Stream procedure '{name}' requires a handler. Call {nameof(Handle)}(...) during registration.");
        }

        return new StreamProcedureSpec(
            service,
            name,
            _handler,
            GetEncoding(),
            GetMiddlewareSnapshot(),
            _metadata,
            GetAliasSnapshot());
    }
}

/// <summary>
/// Fluent builder for client streaming procedure registration.
/// </summary>
public sealed class ClientStreamProcedureBuilder : ProcedureBuilderBase<ClientStreamProcedureBuilder, IClientStreamInboundMiddleware>
{
    private ClientStreamInboundDelegate? _handler;
    private ClientStreamIntrospectionMetadata? _metadata;

    public ClientStreamProcedureBuilder()
    {
    }

    internal ClientStreamProcedureBuilder(ClientStreamInboundDelegate handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the client stream handler. Must be supplied exactly once.
    /// </summary>
    public ClientStreamProcedureBuilder Handle(ClientStreamInboundDelegate handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Overrides the introspection metadata reported for the request stream.
    /// </summary>
    public ClientStreamProcedureBuilder WithMetadata(ClientStreamIntrospectionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    internal ClientStreamProcedureSpec Build(string service, string name)
    {
        if (_handler is null)
        {
            throw new InvalidOperationException(
                $"Client stream procedure '{name}' requires a handler. Call {nameof(Handle)}(...) during registration.");
        }

        return new ClientStreamProcedureSpec(
            service,
            name,
            _handler,
            GetEncoding(),
            GetMiddlewareSnapshot(),
            _metadata,
            GetAliasSnapshot());
    }
}

/// <summary>
/// Fluent builder for duplex streaming procedure registration.
/// </summary>
public sealed class DuplexProcedureBuilder : ProcedureBuilderBase<DuplexProcedureBuilder, IDuplexInboundMiddleware>
{
    private DuplexInboundDelegate? _handler;
    private DuplexIntrospectionMetadata? _metadata;

    public DuplexProcedureBuilder()
    {
    }

    internal DuplexProcedureBuilder(DuplexInboundDelegate handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the duplex stream handler. Must be supplied exactly once.
    /// </summary>
    public DuplexProcedureBuilder Handle(DuplexInboundDelegate handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    /// <summary>
    /// Overrides the introspection metadata reported for the duplex channels.
    /// </summary>
    public DuplexProcedureBuilder WithMetadata(DuplexIntrospectionMetadata metadata)
    {
        _metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
        return this;
    }

    internal DuplexProcedureSpec Build(string service, string name)
    {
        if (_handler is null)
        {
            throw new InvalidOperationException(
                $"Duplex stream procedure '{name}' requires a handler. Call {nameof(Handle)}(...) during registration.");
        }

        return new DuplexProcedureSpec(
            service,
            name,
            _handler,
            GetEncoding(),
            GetMiddlewareSnapshot(),
            _metadata,
            GetAliasSnapshot());
    }
}
