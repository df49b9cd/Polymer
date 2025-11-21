using System.Threading.Channels;
using OmniRelay.Core.Middleware;
using OmniRelay.Core.Transport;
using Xunit;
using static Hugo.Go;

namespace OmniRelay.Core.UnitTests.Middleware;

public sealed class PrincipalBindingMiddlewareTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PrincipalHeader_PromotesCallerAndMetadata()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = []
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

        observed.ShouldNotBeNull();
        observed!.Caller.ShouldBe("subject-a");
        observed.TryGetHeader("rpc.principal", out var principal).ShouldBeTrue();
        principal.ShouldBe("subject-a");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask AuthorizationHeader_BindsBearerToken()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = [],
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

        observed.ShouldNotBeNull();
        observed!.Caller.ShouldBe("abc.def");
        observed.Headers["rpc.principal"].ShouldBe("abc.def");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask MutualTlsAuthorizationHeader_BindsSubject()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = [],
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

        observed.ShouldNotBeNull();
        observed!.Caller.ShouldBe("CN=client-app");
        observed.Headers["rpc.principal"].ShouldBe("CN=client-app");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ClientStreamContext_UpdatesMetadata()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = []
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

        observed.ShouldNotBeNull();
        var updated = observed!.Value;
        updated.Meta.Caller.ShouldBe("streaming-user");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask ThumbprintHeader_IsCapturedWhenEnabled()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-mtls-subject"],
            AuthorizationHeaderNames = [],
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

        observed.ShouldNotBeNull();
        observed!.Caller.ShouldBe("subject-thumb");
        observed.TryGetHeader("rpc.principal_thumbprint", out var thumbprint).ShouldBeTrue();
        thumbprint.ShouldBe("THUMBPRINT123");
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask PromoteToCallerDisabled_DoesNotOverrideExistingCaller()
    {
        var middleware = new PrincipalBindingMiddleware(new PrincipalBindingOptions
        {
            PrincipalHeaderNames = ["x-client-principal"],
            AuthorizationHeaderNames = [],
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

        observed.ShouldNotBeNull();
        observed!.Caller.ShouldBe("existing-caller");
        observed.Headers["rpc.principal"].ShouldBe("new-principal");
    }
}
