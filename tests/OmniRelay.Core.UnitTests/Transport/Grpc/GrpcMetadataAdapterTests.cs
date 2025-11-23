using System.Globalization;
using Grpc.Core;
using OmniRelay.Transport.Grpc;
using Xunit;

namespace OmniRelay.Core.UnitTests.Transport.Grpc;

public class GrpcMetadataAdapterTests
{
    [Fact]
    public void BuildRequestMeta_ParsesHeadersAndAddsProtocol()
    {
        var deadline = DateTimeOffset.Parse("2024-10-05T07:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var metadata = new Metadata
        {
            { GrpcTransportConstants.CallerHeader, "caller-a" },
            { GrpcTransportConstants.ShardKeyHeader, "shard-1" },
            { GrpcTransportConstants.RoutingKeyHeader, "route-1" },
            { GrpcTransportConstants.RoutingDelegateHeader, "delegate-a" },
            { GrpcTransportConstants.TtlHeader, "250" },
            { GrpcTransportConstants.DeadlineHeader, deadline.ToString("O", CultureInfo.InvariantCulture) },
            { "custom-header", "value" },
            new Metadata.Entry("binary-bin", new byte[] { 1, 2, 3 })
        };

        var meta = GrpcMetadataAdapter.BuildRequestMeta(
            service: "orders",
            procedure: "ingest",
            metadata: metadata,
            encoding: "json",
            protocol: "HTTP/3");

        meta.Service.ShouldBe("orders");
        meta.Procedure.ShouldBe("ingest");
        meta.Caller.ShouldBe("caller-a");
        meta.ShardKey.ShouldBe("shard-1");
        meta.RoutingKey.ShouldBe("route-1");
        meta.RoutingDelegate.ShouldBe("delegate-a");
        var ttl = meta.TimeToLive ?? throw new InvalidOperationException("Missing TTL");
        ttl.ShouldBe(TimeSpan.FromMilliseconds(250));
        var metaDeadline = meta.Deadline ?? throw new InvalidOperationException("Missing deadline");
        metaDeadline.ShouldBe(deadline);
        var headers = meta.Headers;
        headers.ShouldNotBeNull();
        headers!["custom-header"].ShouldBe("value");
        headers["rpc.protocol"].ShouldBe("HTTP/3");
        headers.ContainsKey("binary-bin").ShouldBeFalse();
    }

    [Fact]
    public void CreateResponseMeta_MergesHeadersAndTrailers()
    {
        var headers = new Metadata
        {
            { "x-header", "value-1" },
            { GrpcTransportConstants.EncodingTrailer, "json" },
            new Metadata.Entry("ignored-bin-bin", new byte[] { 1 })
        };

        var trailers = new Metadata
        {
            { "x-tail", "value-2" }
        };

        var meta = GrpcMetadataAdapter.CreateResponseMeta(headers, trailers);

        meta.Encoding.ShouldBe("json");
        var responseHeaders = meta.Headers;
        responseHeaders.ShouldNotBeNull();
        responseHeaders!["x-header"].ShouldBe("value-1");
        responseHeaders["x-tail"].ShouldBe("value-2");
        responseHeaders[GrpcTransportConstants.EncodingTrailer].ShouldBe("json");
        responseHeaders.ContainsKey("ignored-bin-bin").ShouldBeFalse();
    }

    [Fact]
    public void BuildRequestMeta_NoHeaders_ReusesSharedEmpty()
    {
        var meta = GrpcMetadataAdapter.BuildRequestMeta(
            service: "svc",
            procedure: "proc",
            metadata: new Metadata(),
            encoding: null);

        meta.Headers.ShouldBeSameAs(RequestMeta.EmptyHeadersInstance);
        meta.Headers.Count.ShouldBe(0);
    }

    [Fact]
    public void BuildRequestMeta_AllowsDuplicateHeaders_LastWins()
    {
        var metadata = new Metadata
        {
            { "custom-header", "first" },
            { "custom-header", "second" }
        };

        var meta = GrpcMetadataAdapter.BuildRequestMeta(
            service: "svc",
            procedure: "proc",
            metadata: metadata,
            encoding: null);

        meta.Headers.ShouldNotBeNull();
        meta.Headers!["custom-header"].ShouldBe("second");
    }

    [Fact]
    public void CreateResponseMeta_UsesTrailerEncodingWhenHeaderMissing()
    {
        var trailers = new Metadata
        {
            { GrpcTransportConstants.EncodingTrailer, "protobuf" },
            { "x-tail", "value" }
        };

        var meta = GrpcMetadataAdapter.CreateResponseMeta(headers: null, trailers: trailers);

        meta.Encoding.ShouldBe("protobuf");
        meta.Headers.ShouldNotBeNull();
        meta.Headers![GrpcTransportConstants.EncodingTrailer].ShouldBe("protobuf");
        meta.Headers["x-tail"].ShouldBe("value");
    }

    [Fact]
    public void CreateResponseMeta_NoHeaders_ReusesSharedEmpty()
    {
        var meta = GrpcMetadataAdapter.CreateResponseMeta(headers: null, trailers: null);

        meta.Headers.ShouldBeSameAs(ResponseMeta.EmptyHeadersInstance);
        meta.Headers.Count.ShouldBe(0);
    }
}
