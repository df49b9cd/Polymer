using Hugo;

namespace OmniRelay.Authorization;

/// <summary>Represents the outcome of mesh authorization evaluation.</summary>
public readonly record struct MeshAuthorizationDecision(bool IsAllowed, string? Reason)
{
    public static MeshAuthorizationDecision Allowed { get; } = new(true, null);
}

/// <summary>Evaluator contract for mesh authorization decisions.</summary>
public interface IMeshAuthorizationEvaluator
{
    MeshAuthorizationDecision Evaluate(string transport, string endpoint, object context);

    Result<MeshAuthorizationDecision> EvaluateResult(string transport, string endpoint, object context);
}
