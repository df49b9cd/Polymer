using System;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace OmniRelay.Transport.Http;

public sealed class HttpServerTlsOptions
{
    public X509Certificate2? Certificate { get; init; }

    public ClientCertificateMode ClientCertificateMode { get; init; } = ClientCertificateMode.NoCertificate;

    public bool? CheckCertificateRevocation { get; init; }
}

public sealed class HttpServerRuntimeOptions
{
    public bool EnableHttp3 { get; set; }

    public long? MaxRequestBodySize { get; set; }

    public long? MaxInMemoryDecodeBytes { get; set; }

    public int? MaxRequestLineSize { get; set; }

    public int? MaxRequestHeadersTotalSize { get; set; }

    public TimeSpan? KeepAliveTimeout { get; set; }

    public TimeSpan? RequestHeadersTimeout { get; set; }

    public TimeSpan? ServerStreamWriteTimeout { get; set; }

    public TimeSpan? DuplexWriteTimeout { get; set; }

    public int? ServerStreamMaxMessageBytes { get; set; }

    public int? DuplexMaxFrameBytes { get; set; }

    public Http3RuntimeOptions? Http3 { get; set; }
}

public sealed class Http3RuntimeOptions
{
    public bool? EnableAltSvc { get; init; }

    public TimeSpan? IdleTimeout { get; init; }

    public TimeSpan? KeepAliveInterval { get; init; }

    public int? MaxBidirectionalStreams { get; init; }

    public int? MaxUnidirectionalStreams { get; init; }
}
