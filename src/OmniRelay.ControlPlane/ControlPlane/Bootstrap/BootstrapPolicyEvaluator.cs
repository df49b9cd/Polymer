using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Evaluates bootstrap join requests against policy documents.</summary>
public sealed partial class BootstrapPolicyEvaluator
{
    private readonly IReadOnlyList<BootstrapPolicyDocument> _documents;
    private readonly ILogger<BootstrapPolicyEvaluator> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _defaultLifetime;
    private readonly bool _requireAttestation;

    public BootstrapPolicyEvaluator(
        IEnumerable<BootstrapPolicyDocument> documents,
        bool requireAttestation,
        TimeSpan defaultLifetime,
        ILogger<BootstrapPolicyEvaluator> logger,
        TimeProvider? timeProvider = null)
    {
        _documents = documents?.ToArray() ?? throw new ArgumentNullException(nameof(documents));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _defaultLifetime = defaultLifetime <= TimeSpan.Zero ? TimeSpan.FromMinutes(30) : defaultLifetime;
        _requireAttestation = requireAttestation;
    }

    public BootstrapPolicyDecision Evaluate(BootstrapJoinRequest request, BootstrapTokenValidationResult token)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(token);

        if (_requireAttestation && request.Attestation is null)
        {
            return BootstrapPolicyDecision.Deny("Attestation evidence is required but was not provided.");
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var document in _documents)
        {
            foreach (var rule in document.Rules)
            {
                if (!Matches(rule, request, token))
                {
                    continue;
                }

                if (!rule.Allow)
                {
                    PolicyLog.RuleDenied(_logger, document.Name, rule.Description ?? string.Empty, token.Role, token.ClusterId);
                    return BootstrapPolicyDecision.Deny($"Policy {document.Name} denied enrollment.");
                }

                var lifetime = rule.Lifetime ?? (token.ExpiresAt - now);
                if (lifetime <= TimeSpan.Zero)
                {
                    lifetime = _defaultLifetime;
                }

                return BootstrapPolicyDecision.Allow(
                    token.ClusterId,
                    token.Role,
                    rule.IdentityTemplate,
                    lifetime,
                    rule.Metadata);
            }

            if (document.DefaultAllow)
            {
                var lifetime = token.ExpiresAt - now;
                if (lifetime <= TimeSpan.Zero)
                {
                    lifetime = _defaultLifetime;
                }

                return BootstrapPolicyDecision.Allow(
                    token.ClusterId,
                    token.Role,
                    identityHint: null,
                    lifetime,
                    metadata: ReadOnlyDictionaryBuilder.EmptyReadOnly);
            }
        }

        return BootstrapPolicyDecision.Deny("No bootstrap policy matched the request.");
    }

    private static bool Matches(BootstrapPolicyRule rule, BootstrapJoinRequest request, BootstrapTokenValidationResult token)
    {
        if (rule.Roles.Count > 0 && !rule.Roles.Contains(token.Role, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Clusters.Count > 0 && !rule.Clusters.Contains(token.ClusterId, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.Environments.Count > 0)
        {
            var environment = request.Environment ?? string.Empty;
            if (!rule.Environments.Contains(environment, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (rule.AttestationProviders.Count > 0)
        {
            var provider = request.Attestation?.Provider;
            if (string.IsNullOrWhiteSpace(provider) || !rule.AttestationProviders.Contains(provider, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!ContainsAll(rule.Labels, request.Labels))
        {
            return false;
        }

        var claims = request.Attestation?.Claims ?? ReadOnlyDictionaryBuilder.EmptyDictionary;
        if (!ContainsAll(rule.Claims, claims))
        {
            return false;
        }

        return true;
    }

    private static bool ContainsAll(IReadOnlyDictionary<string, string> requirements, IDictionary<string, string> values)
    {
        foreach (var pair in requirements)
        {
            if (!values.TryGetValue(pair.Key, out var value) || !string.Equals(pair.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static class ReadOnlyDictionaryBuilder
    {
        public static IReadOnlyDictionary<string, string> EmptyReadOnly { get; } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

        public static IDictionary<string, string> EmptyDictionary { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private static partial class PolicyLog
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bootstrap policy {Policy} rule {Rule} denied role {Role} in cluster {Cluster}.")]
        public static partial void RuleDenied(ILogger logger, string policy, string rule, string role, string cluster);
    }
}

/// <summary>Result of evaluating bootstrap policy.</summary>
public sealed class BootstrapPolicyDecision
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private BootstrapPolicyDecision() { }

    public bool IsAllowed { get; private init; }

    public string? Reason { get; private init; }

    public string ClusterId { get; private init; } = string.Empty;

    public string Role { get; private init; } = string.Empty;

    public string? IdentityHint { get; private init; }

    public TimeSpan Lifetime { get; private init; }

    public IReadOnlyDictionary<string, string> Metadata { get; private init; } = EmptyMetadata;

    public static BootstrapPolicyDecision Allow(
        string clusterId,
        string role,
        string? identityHint,
        TimeSpan lifetime,
        IReadOnlyDictionary<string, string> metadata) => new()
        {
            IsAllowed = true,
            ClusterId = clusterId,
            Role = role,
            IdentityHint = identityHint,
            Lifetime = lifetime,
            Metadata = metadata ?? EmptyMetadata
        };

    public static BootstrapPolicyDecision Deny(string reason) => new()
    {
        IsAllowed = false,
        Reason = reason,
        Lifetime = TimeSpan.Zero,
        Metadata = EmptyMetadata
    };
}

/// <summary>Runtime representation of a bootstrap policy document.</summary>
public sealed class BootstrapPolicyDocument
{
    public BootstrapPolicyDocument(string name, bool defaultAllow, IReadOnlyList<BootstrapPolicyRule> rules)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "default" : name;
        DefaultAllow = defaultAllow;
        Rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    public string Name { get; }

    public bool DefaultAllow { get; }

    public IReadOnlyList<BootstrapPolicyRule> Rules { get; }
}

/// <summary>Runtime representation of a single bootstrap policy rule.</summary>
public sealed class BootstrapPolicyRule
{
    public BootstrapPolicyRule(
        string description,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> clusters,
        IReadOnlyList<string> environments,
        IReadOnlyList<string> attestationProviders,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> claims,
        bool allow,
        string? identityTemplate,
        TimeSpan? lifetime,
        IReadOnlyDictionary<string, string> metadata)
    {
        Description = description;
        Roles = roles;
        Clusters = clusters;
        Environments = environments;
        AttestationProviders = attestationProviders;
        Labels = labels;
        Claims = claims;
        Allow = allow;
        IdentityTemplate = identityTemplate;
        Lifetime = lifetime;
        Metadata = metadata;
    }

    public string Description { get; }

    public IReadOnlyList<string> Roles { get; }

    public IReadOnlyList<string> Clusters { get; }

    public IReadOnlyList<string> Environments { get; }

    public IReadOnlyList<string> AttestationProviders { get; }

    public IReadOnlyDictionary<string, string> Labels { get; }

    public IReadOnlyDictionary<string, string> Claims { get; }

    public bool Allow { get; }

    public string? IdentityTemplate { get; }

    public TimeSpan? Lifetime { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }
}
