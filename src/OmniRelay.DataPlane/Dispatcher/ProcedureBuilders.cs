using System.Collections.Immutable;
using Hugo;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.Dispatcher;

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

    internal Result<ImmutableArray<string>> GetAliasSnapshot()
    {
        if (_aliases.Count == 0)
        {
            return Ok(ImmutableArray<string>.Empty);
        }

        var aliases = ProcedureSpec.ValidateAliases(_aliases);
        return aliases;
    }
}

/// <summary>
/// Fluent builder for unary procedure registration.
/// </summary>
public sealed class UnaryProcedureBuilder : ProcedureBuilderBase<UnaryProcedureBuilder, IUnaryInboundMiddleware>
{
    private UnaryInboundHandler? _handler;

    public UnaryProcedureBuilder()
    {
    }

    internal UnaryProcedureBuilder(UnaryInboundHandler handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the unary handler. Must be supplied exactly once.
    /// </summary>
    public UnaryProcedureBuilder Handle(UnaryInboundHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    internal Result<UnaryProcedureSpec> Build(string service, string name)
    {
        if (_handler is null)
        {
            return Err<UnaryProcedureSpec>(ProcedureErrors.HandlerMissing(service, name, ProcedureKind.Unary.ToString()));
        }

        var aliases = GetAliasSnapshot();
        if (aliases.IsFailure)
        {
            return Err<UnaryProcedureSpec>(aliases.Error);
        }

        var specResult = ProcedureSpec.Create(
            name,
            aliases.Value,
            validatedAliases => new UnaryProcedureSpec(
                service,
                name,
                _handler,
                GetEncoding(),
                GetMiddlewareSnapshot(),
                validatedAliases));

        return specResult.IsSuccess
            ? Ok((UnaryProcedureSpec)specResult.Value)
            : Err<UnaryProcedureSpec>(specResult.Error);
    }
}

/// <summary>
/// Fluent builder for oneway procedure registration.
/// </summary>
public sealed class OnewayProcedureBuilder : ProcedureBuilderBase<OnewayProcedureBuilder, IOnewayInboundMiddleware>
{
    private OnewayInboundHandler? _handler;

    public OnewayProcedureBuilder()
    {
    }

    internal OnewayProcedureBuilder(OnewayInboundHandler handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the oneway handler. Must be supplied exactly once.
    /// </summary>
    public OnewayProcedureBuilder Handle(OnewayInboundHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        return this;
    }

    internal Result<OnewayProcedureSpec> Build(string service, string name)
    {
        if (_handler is null)
        {
            return Err<OnewayProcedureSpec>(ProcedureErrors.HandlerMissing(service, name, ProcedureKind.Oneway.ToString()));
        }

        var aliases = GetAliasSnapshot();
        if (aliases.IsFailure)
        {
            return Err<OnewayProcedureSpec>(aliases.Error);
        }

        var specResult = ProcedureSpec.Create(
            name,
            aliases.Value,
            validatedAliases => new OnewayProcedureSpec(
                service,
                name,
                _handler,
                GetEncoding(),
                GetMiddlewareSnapshot(),
                validatedAliases));

        return specResult.IsSuccess
            ? Ok((OnewayProcedureSpec)specResult.Value)
            : Err<OnewayProcedureSpec>(specResult.Error);
    }
}

/// <summary>
/// Fluent builder for server streaming procedure registration.
/// </summary>
public sealed class StreamProcedureBuilder : ProcedureBuilderBase<StreamProcedureBuilder, IStreamInboundMiddleware>
{
    private StreamInboundHandler? _handler;
    private StreamIntrospectionMetadata? _metadata;

    public StreamProcedureBuilder()
    {
    }

    internal StreamProcedureBuilder(StreamInboundHandler handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the stream handler. Must be supplied exactly once.
    /// </summary>
    public StreamProcedureBuilder Handle(StreamInboundHandler handler)
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

    internal Result<StreamProcedureSpec> Build(string service, string name)
    {
        if (_handler is null)
        {
            return Err<StreamProcedureSpec>(ProcedureErrors.HandlerMissing(service, name, ProcedureKind.Stream.ToString()));
        }

        var aliases = GetAliasSnapshot();
        if (aliases.IsFailure)
        {
            return Err<StreamProcedureSpec>(aliases.Error);
        }

        var specResult = ProcedureSpec.Create(
            name,
            aliases.Value,
            validatedAliases => new StreamProcedureSpec(
                service,
                name,
                _handler,
                GetEncoding(),
                GetMiddlewareSnapshot(),
                _metadata,
                validatedAliases));

        return specResult.IsSuccess
            ? Ok((StreamProcedureSpec)specResult.Value)
            : Err<StreamProcedureSpec>(specResult.Error);
    }
}

/// <summary>
/// Fluent builder for client streaming procedure registration.
/// </summary>
public sealed class ClientStreamProcedureBuilder : ProcedureBuilderBase<ClientStreamProcedureBuilder, IClientStreamInboundMiddleware>
{
    private ClientStreamInboundHandler? _handler;
    private ClientStreamIntrospectionMetadata? _metadata;

    public ClientStreamProcedureBuilder()
    {
    }

    internal ClientStreamProcedureBuilder(ClientStreamInboundHandler handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the client stream handler. Must be supplied exactly once.
    /// </summary>
    public ClientStreamProcedureBuilder Handle(ClientStreamInboundHandler handler)
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

    internal Result<ClientStreamProcedureSpec> Build(string service, string name)
    {
        if (_handler is null)
        {
            return Err<ClientStreamProcedureSpec>(ProcedureErrors.HandlerMissing(service, name, ProcedureKind.ClientStream.ToString()));
        }

        var aliases = GetAliasSnapshot();
        if (aliases.IsFailure)
        {
            return Err<ClientStreamProcedureSpec>(aliases.Error);
        }

        var specResult = ProcedureSpec.Create(
            name,
            aliases.Value,
            validatedAliases => new ClientStreamProcedureSpec(
                service,
                name,
                _handler,
                GetEncoding(),
                GetMiddlewareSnapshot(),
                _metadata,
                validatedAliases));

        return specResult.IsSuccess
            ? Ok((ClientStreamProcedureSpec)specResult.Value)
            : Err<ClientStreamProcedureSpec>(specResult.Error);
    }
}

/// <summary>
/// Fluent builder for duplex streaming procedure registration.
/// </summary>
public sealed class DuplexProcedureBuilder : ProcedureBuilderBase<DuplexProcedureBuilder, IDuplexInboundMiddleware>
{
    private DuplexInboundHandler? _handler;
    private DuplexIntrospectionMetadata? _metadata;

    public DuplexProcedureBuilder()
    {
    }

    internal DuplexProcedureBuilder(DuplexInboundHandler handler)
    {
        Handle(handler);
    }

    /// <summary>
    /// Configures the duplex stream handler. Must be supplied exactly once.
    /// </summary>
    public DuplexProcedureBuilder Handle(DuplexInboundHandler handler)
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

    internal Result<DuplexProcedureSpec> Build(string service, string name)
    {
        if (_handler is null)
        {
            return Err<DuplexProcedureSpec>(ProcedureErrors.HandlerMissing(service, name, ProcedureKind.Duplex.ToString()));
        }

        var aliases = GetAliasSnapshot();
        if (aliases.IsFailure)
        {
            return Err<DuplexProcedureSpec>(aliases.Error);
        }

        var specResult = ProcedureSpec.Create(
            name,
            aliases.Value,
            validatedAliases => new DuplexProcedureSpec(
                service,
                name,
                _handler,
                GetEncoding(),
                GetMiddlewareSnapshot(),
                _metadata,
                validatedAliases));

        return specResult.IsSuccess
            ? Ok((DuplexProcedureSpec)specResult.Value)
            : Err<DuplexProcedureSpec>(specResult.Error);
    }
}
