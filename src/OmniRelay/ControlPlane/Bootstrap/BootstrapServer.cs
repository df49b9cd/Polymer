using System.Security.Cryptography.X509Certificates;
using Hugo;
using Microsoft.Extensions.Logging;
using OmniRelay.ControlPlane.Security;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Handles join requests by validating tokens and packaging bootstrap bundles.</summary>
public sealed partial class BootstrapServer
{
    private readonly BootstrapServerOptions _options;
    private readonly BootstrapTokenService _tokenService;
    private readonly TransportTlsManager _tlsManager;
    private readonly ILogger<BootstrapServer> _logger;
    private readonly TimeProvider _timeProvider;

    public BootstrapServer(
        BootstrapServerOptions options,
        BootstrapTokenService tokenService,
        TransportTlsManager tlsManager,
        ILogger<BootstrapServer> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _tlsManager = tlsManager ?? throw new ArgumentNullException(nameof(tlsManager));
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

    private ValueTask<Result<BootstrapJoinResponse>> ProcessJoinAsync(BootstrapJoinRequest request, CancellationToken cancellationToken)
    {
        var envelope = Ok(request)
            .Ensure(HasToken, BuildMissingTokenError)
            .Map(req => (Request: req, Validation: _tokenService.ValidateToken(req.Token!, _options.ClusterId)))
            .Ensure(tuple => tuple.Validation.IsValid, tuple => BuildValidationError(tuple.Request, tuple.Validation))
            .Tap(tuple => LogRoleOverride(tuple.Request, tuple.Validation.Role))
            .Then(tuple => BuildJoinResponse(tuple, cancellationToken)
                .Map(response => (Response: response, tuple.Validation)));

        if (envelope.TryGetValue(out var success))
        {
            Log.BootstrapBundleIssued(_logger, success.Response.ClusterId, success.Response.Role, success.Validation.TokenId);
        }
        else if (envelope.TryGetError(out var error))
        {
            Log.BootstrapJoinFailed(_logger, error.Message ?? "unknown", error.Code ?? "unknown");
        }

        return ValueTask.FromResult(envelope.Map(tuple => tuple.Response));
    }

    private Result<BootstrapJoinResponse> BuildJoinResponse(
        (BootstrapJoinRequest Request, BootstrapTokenValidationResult Validation) context,
        CancellationToken cancellationToken) =>
        Result.Try(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var certificate = _tlsManager.GetCertificate();
            var exported = ExportCertificate(certificate, _options.BundlePassword);
            return new BootstrapJoinResponse
            {
                ClusterId = context.Validation.ClusterId,
                Role = context.Validation.Role,
                CertificateData = exported,
                CertificatePassword = _options.BundlePassword,
                SeedPeers = _options.SeedPeers.ToArray(),
                ExpiresAt = context.Validation.ExpiresAt
            };
        },
        ex => BuildBundleError(ex, context.Request));

    private static bool HasToken(BootstrapJoinRequest request) =>
        !string.IsNullOrWhiteSpace(request.Token);

    private static Error BuildMissingTokenError(BootstrapJoinRequest request) =>
        Error.From("A join token must be supplied.", ErrorCodes.Validation)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty);

    private Error BuildValidationError(BootstrapJoinRequest request, BootstrapTokenValidationResult validation) =>
        Error.From(validation.FailureReason ?? "Token validation failed.", ErrorCodes.Validation)
            .WithMetadata("nodeId", request.NodeId ?? string.Empty)
            .WithMetadata("clusterId", _options.ClusterId);

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

    private static string ExportCertificate(X509Certificate2 certificate, string? password)
    {
        var bytes = certificate.Export(X509ContentType.Pfx, password);
        return Convert.ToBase64String(bytes);
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Issued bootstrap bundle for cluster {ClusterId} role {Role} (token {TokenId}).")]
        public static partial void BootstrapBundleIssued(ILogger logger, string clusterId, string role, Guid tokenId);

        [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Bootstrap join failed: {Message} (code: {Code}).")]
        public static partial void BootstrapJoinFailed(ILogger logger, string message, string code);

        [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Join request for node {NodeId} attempted to override role '{RequestedRole}' (token role '{TokenRole}').")]
        public static partial void BootstrapRoleOverride(ILogger logger, string nodeId, string? requestedRole, string tokenRole);
    }
}
