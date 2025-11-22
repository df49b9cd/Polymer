using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Grpc.Core;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;

#pragma warning disable SYSLIB0058
namespace OmniRelay.Transport.Security;

/// <summary>Normalized view of an HTTP or gRPC connection for policy evaluation.</summary>
public sealed class TransportSecurityContext
{
    public string Transport { get; init; } = "http";

    public string Protocol { get; init; } = "http/1.1";

    public SslProtocols? TlsProtocol { get; init; }

    public CipherAlgorithmType? Cipher { get; init; }

    public int? CipherStrength { get; init; }

    public IPAddress? RemoteAddress { get; init; }

    public string? Host { get; init; }

    public X509Certificate2? ClientCertificate { get; init; }

    public static TransportSecurityContext FromHttpContext(string transport, HttpContext context)
    {
        var tls = context.Features.Get<ITlsHandshakeFeature>();
        return new TransportSecurityContext
        {
            Transport = transport,
            Protocol = context.Request.Protocol,
            TlsProtocol = tls?.Protocol,
            Cipher = tls?.CipherAlgorithm,
            CipherStrength = tls?.CipherStrength,
            RemoteAddress = context.Connection.RemoteIpAddress,
            Host = context.Request.Host.Host,
            ClientCertificate = context.Connection.ClientCertificate
        };
    }

    public static TransportSecurityContext FromServerCallContext(string transport, ServerCallContext context)
    {
        var httpContext = context.GetHttpContext();
        return FromHttpContext(transport, httpContext);
    }
}
#pragma warning restore SYSLIB0058
