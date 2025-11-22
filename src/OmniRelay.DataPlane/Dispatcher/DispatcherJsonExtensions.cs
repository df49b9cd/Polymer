using System.Text.Json;
using System.Text.Json.Serialization;
using Hugo;
using Json.Schema;
using OmniRelay.Core;
using OmniRelay.Core.Clients;
using OmniRelay.Errors;

namespace OmniRelay.Dispatcher;

/// <summary>
/// Convenience helpers for registering JSON procedures and creating JSON clients.
/// </summary>
public static class DispatcherJsonExtensions
{
    /// <summary>
    /// Registers a JSON unary procedure with a typed handler and optional codec/procedure configuration.
    /// </summary>
    public static void RegisterJsonUnary<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string name,
        Func<JsonUnaryContext, TRequest, ValueTask<Response<TResponse>>> handler,
        Action<JsonCodecBuilder<TRequest, TResponse>>? configureCodec = null,
        Action<UnaryProcedureBuilder>? configureProcedure = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(handler);

        var codec = BuildCodec(configureCodec);

        async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> Wrapper(IRequest<ReadOnlyMemory<byte>> rawRequest, CancellationToken cancellationToken)
        {
            var decode = codec.DecodeRequest(rawRequest.Body, rawRequest.Meta);

            var handlerResult = await decode
                .ThenAsync(async (typedRequest, token) =>
                {
                    try
                    {
                        var context = new JsonUnaryContext(dispatcher, rawRequest.Meta, token);
                        var response = await handler(context, typedRequest).ConfigureAwait(false);
                        return Result.Ok(response);
                    }
                    catch (Exception ex)
                    {
                        return OmniRelayErrors.ToResult<Response<TResponse>>(ex, rawRequest.Meta.Transport ?? "json");
                    }
                }, cancellationToken)
                .ConfigureAwait(false);

            return handlerResult.Then(typedResponse =>
            {
                var responseMeta = EnsureEncoding(typedResponse.Meta, codec.Encoding);
                return codec.EncodeResponse(typedResponse.Body, responseMeta)
                    .Map(payload => Response<ReadOnlyMemory<byte>>.Create(payload, responseMeta));
            });
        }

        dispatcher.RegisterUnary(name, builder =>
        {
            builder.Handle(Wrapper);
            builder.WithEncoding(codec.Encoding);
            configureProcedure?.Invoke(builder);
        }).ThrowIfFailure();

        if (dispatcher.TryGetProcedure(name, ProcedureKind.Unary, out var spec) &&
            spec is UnaryProcedureSpec unarySpec)
        {
            dispatcher.Codecs.RegisterInbound(unarySpec.Name, ProcedureKind.Unary, codec, unarySpec.Aliases);
        }
        else
        {
            dispatcher.Codecs.RegisterInbound(name, ProcedureKind.Unary, codec);
        }
    }

    /// <summary>
    /// Registers a JSON unary procedure with a typed handler that returns a response body only.
    /// </summary>
    public static void RegisterJsonUnary<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string name,
        Func<JsonUnaryContext, TRequest, ValueTask<TResponse>> handler,
        Action<JsonCodecBuilder<TRequest, TResponse>>? configureCodec = null,
        Action<UnaryProcedureBuilder>? configureProcedure = null)
    {
        ArgumentNullException.ThrowIfNull(handler);

        dispatcher.RegisterJsonUnary(
            name,
            async (context, request) =>
            {
                var body = await handler(context, request).ConfigureAwait(false);
                return Response<TResponse>.Create(body, new ResponseMeta());
            },
            configureCodec,
            configureProcedure);
    }

    /// <summary>
    /// Creates a JSON unary client for a service/procedure, optionally customizing the codec and outbound key.
    /// </summary>
    public static UnaryClient<TRequest, TResponse> CreateJsonClient<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string service,
        string procedure,
        Action<JsonCodecBuilder<TRequest, TResponse>>? configureCodec = null,
        string? outboundKey = null,
        IEnumerable<string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (dispatcher.Codecs.TryResolve<TRequest, TResponse>(ProcedureCodecScope.Outbound, service, procedure, ProcedureKind.Unary, out var existing))
        {
            return dispatcher.CreateUnaryClient(service, existing, outboundKey);
        }

        var codec = BuildCodec(configureCodec);
        dispatcher.Codecs.RegisterOutbound(service, procedure, ProcedureKind.Unary, codec, aliases);
        return dispatcher.CreateUnaryClient(service, codec, outboundKey);
    }

    private static JsonCodec<TRequest, TResponse> BuildCodec<TRequest, TResponse>(
        Action<JsonCodecBuilder<TRequest, TResponse>>? configureCodec)
    {
        var builder = new JsonCodecBuilder<TRequest, TResponse>();
        configureCodec?.Invoke(builder);
        return builder.Build();
    }

    private static ResponseMeta EnsureEncoding(ResponseMeta meta, string encoding)
    {
        if (!string.IsNullOrWhiteSpace(meta.Encoding))
        {
            return meta;
        }

        return meta with { Encoding = encoding };
    }
}

/// <summary>
/// Provides request metadata and cancellation for JSON unary handlers.
/// </summary>
public readonly record struct JsonUnaryContext(Dispatcher Dispatcher, RequestMeta RequestMeta, CancellationToken CancellationToken)
{
    public RequestMeta Meta => RequestMeta;

    public Dispatcher Dispatcher { get; init; } = Dispatcher;

    public RequestMeta RequestMeta { get; init; } = RequestMeta;

    public CancellationToken CancellationToken { get; init; } = CancellationToken;
}

/// <summary>
/// Builder used by JSON dispatcher helpers to configure codecs.
/// </summary>
public sealed class JsonCodecBuilder<TRequest, TResponse>
{
    public JsonSerializerOptions? SerializerOptions { get; set; }

    public JsonSerializerContext? SerializerContext { get; set; }

    public JsonSchema? RequestSchema { get; set; }

    public string? RequestSchemaId { get; set; }

    public JsonSchema? ResponseSchema { get; set; }

    public string? ResponseSchemaId { get; set; }

    public string Encoding { get; set; } = "json";

    internal JsonCodec<TRequest, TResponse> Build() =>
        new(
            SerializerOptions,
            Encoding,
            SerializerContext,
            RequestSchema,
            RequestSchemaId,
            ResponseSchema,
            ResponseSchemaId);
}
