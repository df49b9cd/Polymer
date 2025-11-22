using System.Collections.ObjectModel;
using Hugo;
using Microsoft.Extensions.Logging;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Handles join requests by validating tokens and packaging bootstrap bundles.</summary>
public sealed partial class BootstrapServer
{
    private readonly BootstrapServerOptions _options;
    private readonly BootstrapTokenService _tokenService;
    private readonly IWorkloadIdentityProvider _identityProvider;
    private readonly BootstrapPolicyEvaluator _policyEvaluator;
    private readonly ILogger<BootstrapServer> _logger;
    private readonly TimeProvider _timeProvider;

    public BootstrapServer(
        BootstrapServerOptions options,
        BootstrapTokenService tokenService,
        IWorkloadIdentityProvider identityProvider,
        BootstrapPolicyEvaluator policyEvaluator,
        ILogger<BootstrapServer> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _identityProvider = identityProvider ?? throw new ArgumentNullException(nameof(identityProvider));
        _policyEvaluator = policyEvaluator ?? throw new ArgumentNullException(nameof(policyEvaluator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<Result<BootstrapJoinResponse>> JoinAsync(BootstrapJoinRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!ShouldEnforceTimeout(_options.JoinTimeout))
        {
            return ProcessJoinAsync(request, cancellationToken);
        }

        return WithTimeoutValueTaskAsync(
                token => ProcessJoinAsync(request, token),
                _options.JoinTimeout,
                _timeProvider,
                cancellationToken);
    }

    private async ValueTask<Result<BootstrapJoinResponse>> ProcessJoinAsync(BootstrapJoinRequest request, CancellationToken cancellationToken)
    {
        if (!HasToken(request))
        {
            return Result.Fail<BootstrapJoinResponse>(BuildMissingTokenError(request));
        }

        var validation = _tokenService.ValidateToken(request.Token!, _options.ClusterId);
        if (!validation.IsValid)
        {
            return Result.Fail<BootstrapJoinResponse>(BuildValidationError(request, validation));
        }

        LogRoleOverride(request, validation.Role);
        var decision = _policyEvaluator.Evaluate(request, validation);
        if (!decision.IsAllowed)
        {
            return Result.Fail<BootstrapJoinResponse>(BuildPolicyError(request, decision));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var identityRequest = BuildIdentityRequest(request, validation, decision);
            var bundle = await _identityProvider.IssueAsync(identityRequest, cancellationToken).ConfigureAwait(false);
            var response = BuildJoinResponse(validation, bundle);
            Log.BootstrapBundleIssued(_logger, response.ClusterId, response.Role, bundle.Identity);
            return Result.Ok(response);
        }
        catch (Exception ex)
        {
            var error = BuildBundleError(ex, request);
            Log.BootstrapJoinFailed(_logger, error.Message ?? "unknown", error.Code ?? "unknown");
            return Result.Fail<BootstrapJoinResponse>(error);
        }
    }

    private BootstrapJoinResponse BuildJoinResponse(BootstrapTokenValidationResult validation, WorkloadCertificateBundle bundle)
    {
        return new BootstrapJoinResponse
        {
            ClusterId = validation.ClusterId,
            Role = validation.Role,
            Identity = bundle.Identity,
            IdentityProvider = bundle.Provider,
            CertificateData = Convert.ToBase64String(bundle.CertificateData),
            CertificatePassword = bundle.CertificatePassword,
            TrustBundleData = bundle.TrustBundleData,
            SeedPeers = _options.SeedPeers.ToArray(),
            Metadata = bundle.Metadata,
            IssuedAt = bundle.IssuedAt,
            RenewAfter = bundle.RenewAfter,
            ExpiresAt = bundle.ExpiresAt,
            AuditId = validation.TokenId.ToString("N")
        };
    }

    private WorkloadIdentityRequest BuildIdentityRequest(BootstrapJoinRequest request, BootstrapTokenValidationResult validation, BootstrapPolicyDecision decision)
    {
        var labels = new Dictionary<string, string>(request.Labels, StringComparer.OrdinalIgnoreCase);
        var metadata = decision.Metadata ?? new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
        var lifetime = decision.Lifetime > TimeSpan.Zero
            ? decision.Lifetime
            : validation.ExpiresAt - _timeProvider.GetUtcNow();
        if (lifetime <= TimeSpan.Zero)
        {
            lifetime = TimeSpan.FromMinutes(30);
        }

        return new WorkloadIdentityRequest
        {
            ClusterId = validation.ClusterId,
            Role = validation.Role,
            NodeId = request.NodeId ?? string.Empty,
            Environment = request.Environment,
            Labels = new ReadOnlyDictionary<string, string>(labels),
            Attestation = request.Attestation,
            IdentityHint = decision.IdentityHint,
            DesiredLifetime = lifetime,
            Metadata = metadata
        };
    }

    private static bool HasToken(BootstrapJoinRequest request) =>
        !string.IsNullOrWhiteSpace(request.Token);

    private static Error BuildMissingTokenError(BootstrapJoinRequest request) =>
        Error.From("A join token must be supplied.", ErrorCodes.Validation)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty);

    private Error BuildValidationError(BootstrapJoinRequest request, BootstrapTokenValidationResult validation) =>
        Error.From(validation.FailureReason ?? "Token validation failed.", ErrorCodes.Validation)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty)
            .WithMetadata("clusterId", _options.ClusterId);

    private static Error BuildPolicyError(BootstrapJoinRequest request, BootstrapPolicyDecision decision) =>
        Error.From(decision.Reason ?? "Bootstrap policy denied the request.", ErrorCodes.Validation)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty);

    private Error BuildBundleError(Exception exception, BootstrapJoinRequest request) =>
        Error.From("Unable to create bootstrap bundle.", "bootstrap.join.bundle_failed")
            .WithCause(exception)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty)
            .WithMetadata("clusterId", _options.ClusterId);

    private void LogRoleOverride(BootstrapJoinRequest request, string role)
    {
        if (string.IsNullOrWhiteSpace(request.RequestedRole) ||
            string.Equals(request.RequestedRole, role, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Log.BootstrapRoleOverride(_logger, request.NodeId ?? "unknown", request.RequestedRole ?? "", role);
    }

    private static bool ShouldEnforceTimeout(TimeSpan timeout) =>
        timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan;

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Issued bootstrap bundle for cluster {ClusterId} role {Role} (identity {Identity}).")]
        public static partial void BootstrapBundleIssued(ILogger logger, string clusterId, string role, string identity);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Bootstrap join failed: {Message} (code: {Code}).")]
        public static partial void BootstrapJoinFailed(ILogger logger, string message, string code);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Join request for node {NodeId} attempted to override role '{RequestedRole}' (token role '{TokenRole}').")]
        public static partial void BootstrapRoleOverride(ILogger logger, string nodeId, string? requestedRole, string tokenRole);
    }
}
