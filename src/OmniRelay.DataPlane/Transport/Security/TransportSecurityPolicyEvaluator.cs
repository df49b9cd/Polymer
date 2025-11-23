using System.Net;
using Hugo;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

namespace OmniRelay.Transport.Security;

/// <summary>Evaluates requests against the configured transport security policy.</summary>
public sealed partial class TransportSecurityPolicyEvaluator
{
    private readonly TransportSecurityPolicy _policy;
    private readonly ILogger<TransportSecurityPolicyEvaluator> _logger;

    public TransportSecurityPolicyEvaluator(TransportSecurityPolicy policy, ILogger<TransportSecurityPolicyEvaluator> logger)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Result<TransportSecurityDecision> Evaluate(TransportSecurityContext context)
    {
        if (!_policy.Enabled)
        {
            return Ok(TransportSecurityDecision.Allowed);
        }

        var normalizedProtocol = context.Protocol?.ToLowerInvariant() ?? string.Empty;
        if (_policy.AllowedProtocols.Count > 0 && !_policy.AllowedProtocols.Contains(normalizedProtocol))
        {
            var reason = $"Protocol '{context.Protocol}' is not allowed.";
            Log.TransportDenied(_logger, reason);
            return Err<TransportSecurityDecision>(Error.From(reason, "transport.security.protocol_denied"));
        }

        if (_policy.AllowedTlsVersions.Count > 0)
        {
            if (context.TlsProtocol is not { } tls || !_policy.AllowedTlsVersions.Contains(tls))
            {
                var reason = "TLS protocol mismatch.";
                Log.TransportDenied(_logger, reason);
                return Err<TransportSecurityDecision>(Error.From(reason, "transport.security.tls_denied"));
            }
        }

        if (_policy.AllowedCipherAlgorithms.Count > 0)
        {
            if (context.Cipher is not { } cipher || !_policy.AllowedCipherAlgorithms.Contains(cipher))
            {
                var reason = "Cipher suite not permitted.";
                Log.TransportDenied(_logger, reason);
                return Err<TransportSecurityDecision>(Error.From(reason, "transport.security.cipher_denied"));
            }
        }

        if (_policy.RequireClientCertificate && context.ClientCertificate is null)
        {
            var reason = "Client certificate required.";
            Log.TransportDenied(_logger, reason);
            return Err<TransportSecurityDecision>(Error.From(reason, "transport.security.client_certificate_required"));
        }

        if (_policy.AllowedThumbprints.Count > 0 && context.ClientCertificate is not null)
        {
            var thumbprint = context.ClientCertificate.Thumbprint?.ToUpperInvariant();
            if (thumbprint is null || !_policy.AllowedThumbprints.Contains(thumbprint))
            {
                var reason = "Client certificate thumbprint not allowed.";
                Log.TransportDenied(_logger, reason);
                return Err<TransportSecurityDecision>(Error.From(reason, "transport.security.thumbprint_denied"));
            }
        }

        if (_policy.EndpointRules.Length > 0)
        {
            var decision = EvaluateEndpoints(context);
            if (!decision.IsAllowed)
            {
                Log.TransportDenied(_logger, decision.Reason ?? "Endpoint blocked by policy.");
                return Err<TransportSecurityDecision>(Error.From(decision.Reason ?? "Endpoint blocked by policy.", "transport.security.endpoint_denied"));
            }
        }

        return Ok(TransportSecurityDecision.Allowed);
    }

    private TransportSecurityDecision EvaluateEndpoints(TransportSecurityContext context)
    {
        IPAddress? address = context.RemoteAddress;
        var host = context.Host;

        foreach (var rule in _policy.EndpointRules)
        {
            if (rule.Matches(address, host))
            {
                if (rule.Allow)
                {
                    return TransportSecurityDecision.Allowed;
                }

                var reason = "Endpoint blocked by policy.";
                return new TransportSecurityDecision(false, reason);
            }
        }

        // Default deny when rules exist but nothing matched.
        if (_policy.EndpointRules.Length > 0)
        {
            return new TransportSecurityDecision(false, "No endpoint rules matched.");
        }

        return TransportSecurityDecision.Allowed;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Transport security denied connection: {Reason}")]
        public static partial void TransportDenied(ILogger logger, string reason);
    }
}
