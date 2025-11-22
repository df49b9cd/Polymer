using System.Reflection;
using OmniRelay.Core;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Tests.Transport;

public class GrpcMetadataAdapterTests
{
    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateRequestMetadata_ThrowsForInvalidHeaderCharacters()
    {
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            headers:
            [
                new KeyValuePair<string, string>("custom", "bad\r\nvalue")
            ]);

        Should.Throw<ArgumentException>(() => GrpcMetadataAdapter.CreateRequestMetadata(meta));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ResolveDeadline_UsesSoonerOfDeadlineAndTtl()
    {
        var now = DateTime.UtcNow;
        var ttl = TimeSpan.FromSeconds(30);
        var deadline = now.AddSeconds(10);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            timeToLive: ttl,
            deadline: deadline);

        var resolved = InvokeResolveDeadline(meta);

        resolved.HasValue.ShouldBeTrue();
        var resolvedValue = resolved!.Value;
        resolvedValue.ShouldBeInRange(deadline.AddSeconds(-1), deadline.AddSeconds(1));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ResolveDeadline_UsesTtlWhenDeadlineMissing()
    {
        var ttl = TimeSpan.FromMilliseconds(200);
        var meta = new RequestMeta(
            service: "svc",
            procedure: "echo",
            timeToLive: ttl);

        var before = DateTime.UtcNow;
        var resolved = InvokeResolveDeadline(meta);
        resolved.HasValue.ShouldBeTrue();
        var resolvedValue = resolved!.Value;
        resolvedValue.ShouldBeInRange(before.AddMilliseconds(50), before.AddMilliseconds(600));
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void ResolveDeadline_ReturnsNullWhenNoDeadlineOrTtl()
    {
        var meta = new RequestMeta(service: "svc", procedure: "echo");
        var resolved = InvokeResolveDeadline(meta);
        resolved.ShouldBeNull();
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void CreateResponseHeaders_PreservesRawEncoding()
    {
        var meta = new ResponseMeta(encoding: RawCodec.DefaultEncoding);
        var headers = GrpcMetadataAdapter.CreateResponseHeaders(meta);

        headers.ShouldContain(entry =>
            !entry.IsBinary &&
            string.Equals(entry.Key, GrpcTransportConstants.EncodingTrailer, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(entry.Value, RawCodec.DefaultEncoding, StringComparison.OrdinalIgnoreCase));
    }

    private static DateTime? InvokeResolveDeadline(RequestMeta meta)
    {
        var method = typeof(GrpcOutbound).GetMethod(
            "ResolveDeadline",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing ResolveDeadline.");
        var result = method.Invoke(null, [meta]);
        return (DateTime?)result;
    }
}
