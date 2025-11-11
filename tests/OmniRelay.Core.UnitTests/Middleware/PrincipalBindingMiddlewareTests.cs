using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading.Channels;
using Hugo;
using OmniRelay.Core;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public sealed class PrincipalBindingMiddlewareTests
{
    [Fact]
    public async Task PrincipalHeader_PromotesCallerAndMetadata()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = ImmutableArray<string>.Empty
        });

        var meta = new RequestMeta(
            service: "svc",
            headers: new Dictionary<string, string>
            {
                { "x-client-principal", "subject-a" }
            });

        RequestMeta? observed = null;

        await middleware.InvokeAsync(
            new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None,
            (request, _) =>
            {
                observed = request.Meta;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        Assert.Equal("subject-a", observed!.Caller);
        Assert.True(observed.TryGetHeader("rpc.principal", out var principal));
        Assert.Equal("subject-a", principal);
    }

    [Fact]
    public async Task AuthorizationHeader_BindsBearerToken()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ImmutableArray<string>.Empty,
            AuthorizationHeaderNames = ["authorization"]
        });

        var meta = new RequestMeta(
            service: "svc",
            headers: new Dictionary<string, string>
            {
                { "authorization", "Bearer abc.def" }
            });

        RequestMeta? observed = null;

        await middleware.InvokeAsync(
            new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None,
            (request, _) =>
            {
                observed = request.Meta;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        Assert.Equal("abc.def", observed!.Caller);
        Assert.Equal("abc.def", observed.Headers["rpc.principal"]);
    }

    [Fact]
    public async Task MutualTlsAuthorizationHeader_BindsSubject()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ImmutableArray<string>.Empty,
            AuthorizationHeaderNames = ["authorization"],
            AcceptBearerTokens = false,
            AcceptMutualTlsSubjects = true
        });

        var meta = new RequestMeta(
            service: "svc",
            headers: new Dictionary<string, string>
            {
                { "authorization", "mTLS CN=client-app" }
            });

        RequestMeta? observed = null;

        await middleware.InvokeAsync(
            new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None,
            (request, _) =>
            {
                observed = request.Meta;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        Assert.Equal("CN=client-app", observed!.Caller);
        Assert.Equal("CN=client-app", observed.Headers["rpc.principal"]);
    }

    [Fact]
    public async Task ClientStreamContext_UpdatesMetadata()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = ImmutableArray<string>.Empty
        });

        var meta = new RequestMeta(
            service: "svc",
            headers: new Dictionary<string, string>
            {
                { "x-client-principal", "streaming-user" }
            });

        ClientStreamRequestContext? observed = null;
        var reader = Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader;
        var context = new ClientStreamRequestContext(meta, reader);

        await middleware.InvokeAsync(
            context,
            CancellationToken.None,
            (ctx, _) =>
            {
                observed = ctx;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        var updated = observed!.Value;
        Assert.Equal("streaming-user", updated.Meta.Caller);
    }

    [Fact]
    public async Task ThumbprintHeader_IsCapturedWhenEnabled()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-mtls-subject"],
            AuthorizationHeaderNames = ImmutableArray<string>.Empty,
            IncludeThumbprint = true,
            ThumbprintHeaderName = "x-mtls-thumbprint"
        });

        var meta = new RequestMeta(
            service: "svc",
            headers: new Dictionary<string, string>
            {
                { "x-mtls-subject", "subject-thumb" },
                { "x-mtls-thumbprint", "THUMBPRINT123" }
            });

        RequestMeta? observed = null;

        await middleware.InvokeAsync(
            new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None,
            (request, _) =>
            {
                observed = request.Meta;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        Assert.Equal("subject-thumb", observed!.Caller);
        Assert.True(observed.TryGetHeader("rpc.principal_thumbprint", out var thumbprint));
        Assert.Equal("THUMBPRINT123", thumbprint);
    }

    [Fact]
    public async Task PromoteToCallerDisabled_DoesNotOverrideExistingCaller()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = ImmutableArray<string>.Empty,
            PromoteToCaller = false
        });

        var meta = new RequestMeta(
            service: "svc",
            caller: "existing-caller",
            headers: new Dictionary<string, string>
            {
                { "x-client-principal", "new-principal" }
            });

        RequestMeta? observed = null;

        await middleware.InvokeAsync(
            new Request<ReadOnlyMemory<byte>>(meta, ReadOnlyMemory<byte>.Empty),
            CancellationToken.None,
            (request, _) =>
            {
                observed = request.Meta;
                return ValueTask.FromResult(Ok(Response<ReadOnlyMemory<byte>>.Create(ReadOnlyMemory<byte>.Empty)));
            });

        Assert.NotNull(observed);
        Assert.Equal("existing-caller", observed!.Caller);
        Assert.Equal("new-principal", observed.Headers["rpc.principal"]);
    }
}
