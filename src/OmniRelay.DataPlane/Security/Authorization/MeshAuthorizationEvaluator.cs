using Microsoft.AspNetCore.Http;

namespace OmniRelay.Security.Authorization;

/// <summary>Evaluates incoming requests against configured authorization policies.</summary>
public sealed class MeshAuthorizationEvaluator
{
    private readonly IReadOnlyList<MeshAuthorizationPolicy> _policies;

    public MeshAuthorizationEvaluator(IReadOnlyList<MeshAuthorizationPolicy> policies)
    {
        _policies = policies;
    }

    public MeshAuthorizationDecision Evaluate(string transport, string endpoint, HttpContext context)
    {
        if (_policies.Count == 0)
        {
            return MeshAuthorizationDecision.Allowed;
        }

        var headers = context.Request.Headers;
        var principal = headers.TryGetValue("x-client-principal", out var p) ? p.ToString() : null;
        var role = headers.TryGetValue("x-mesh-role", out var r) ? r.ToString() : null;
        var cluster = headers.TryGetValue("x-mesh-cluster", out var c) ? c.ToString() : null;

        foreach (var policy in _policies)
        {
            if (!policy.Matches(transport, endpoint))
            {
                continue;
            }

            if (policy.Principals.Count > 0 && (principal is null || !policy.Principals.Contains(principal)))
            {
                continue;
            }

            if (policy.AllowedRoles.Count > 0 && (role is null || !policy.AllowedRoles.Contains(role)))
            {
                continue;
            }

            if (policy.AllowedClusters.Count > 0 && (cluster is null || !policy.AllowedClusters.Contains(cluster)))
            {
                continue;
            }

            if (policy.RequiredLabels.Count > 0 &&
                !policy.RequiredLabels.All(kvp => headers.TryGetValue(kvp.Key, out var value) && string.Equals(value.ToString(), kvp.Value, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (policy.RequireMutualTls && context.Connection.ClientCertificate is null)
            {
                return new MeshAuthorizationDecision(false, $"Client certificate required by policy '{policy.Name}'.");
            }

            return MeshAuthorizationDecision.Allowed;
        }

        return new MeshAuthorizationDecision(false, "Authorization policy did not match.");
    }
}
