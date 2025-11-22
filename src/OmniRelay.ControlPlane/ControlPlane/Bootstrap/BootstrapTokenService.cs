using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>Generates and validates bootstrap tokens with replay protection.</summary>
public sealed partial class BootstrapTokenService
{
    private readonly BootstrapTokenSigningOptions _options;
    private readonly IBootstrapReplayProtector _replayProtector;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<BootstrapTokenService> _logger;

    public BootstrapTokenService(
        BootstrapTokenSigningOptions options,
        IBootstrapReplayProtector replayProtector,
        ILogger<BootstrapTokenService> logger,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _replayProtector = replayProtector ?? throw new ArgumentNullException(nameof(replayProtector));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string CreateToken(BootstrapTokenDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var now = _timeProvider.GetUtcNow();
        var expires = now + descriptor.Lifetime;

        var payload = new BootstrapTokenPayload
        {
            TokenId = Guid.NewGuid(),
            IssuedAt = now,
            ExpiresAt = expires,
            ClusterId = descriptor.ClusterId,
            Role = descriptor.Role,
            MaxUses = descriptor.MaxUses ?? _options.DefaultMaxUses,
            Issuer = _options.Issuer
        };

        var payloadBytes = SerializePayload(payload);
        var signature = ComputeSignature(payloadBytes);
        return $"{Convert.ToBase64String(payloadBytes)}.{Convert.ToBase64String(signature)}";
    }

    public BootstrapTokenValidationResult ValidateToken(string token, string? expectedCluster)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return BootstrapTokenValidationResult.Failed("Token was not provided.");
        }

        var parts = token.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            return BootstrapTokenValidationResult.Failed("Token format is invalid.");
        }

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Convert.FromBase64String(parts[0]);
            providedSignature = Convert.FromBase64String(parts[1]);
        }
        catch (FormatException)
        {
            return BootstrapTokenValidationResult.Failed("Token encoding is not valid Base64.");
        }

        var expectedSignature = ComputeSignature(payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return BootstrapTokenValidationResult.Failed("Token signature mismatch.");
        }

        if (!TryReadPayload(payloadBytes, out var payload))
        {
            return BootstrapTokenValidationResult.Failed("Token payload could not be parsed.");
        }

        if (!string.Equals(payload.Issuer, _options.Issuer, StringComparison.Ordinal))
        {
            return BootstrapTokenValidationResult.Failed("Token issuer mismatch.");
        }

        var now = _timeProvider.GetUtcNow();
        if (payload.ExpiresAt + _options.ClockSkew <= now)
        {
            return BootstrapTokenValidationResult.Failed("Token expired.");
        }

        if (payload.IssuedAt - _options.ClockSkew > now)
        {
            return BootstrapTokenValidationResult.Failed("Token not yet valid.");
        }

        if (!string.IsNullOrWhiteSpace(expectedCluster) &&
            !string.Equals(expectedCluster, payload.ClusterId, StringComparison.OrdinalIgnoreCase))
        {
            return BootstrapTokenValidationResult.Failed("Token was issued for a different cluster.");
        }

        if (!_replayProtector.TryConsume(payload.TokenId, payload.ExpiresAt, payload.MaxUses))
        {
            Log.BootstrapTokenReplayed(_logger, payload.TokenId);
            return BootstrapTokenValidationResult.Failed("Token was already consumed.");
        }

        return BootstrapTokenValidationResult.Success(payload.TokenId, payload.ClusterId, payload.Role, payload.ExpiresAt);
    }

    private byte[] ComputeSignature(byte[] payload)
    {
        using var hmac = new HMACSHA256(_options.SigningKey);
        return hmac.ComputeHash(payload);
    }

    private static byte[] SerializePayload(BootstrapTokenPayload payload)
    {
        var writerBuffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(writerBuffer))
        {
            writer.WriteStartObject();
            writer.WriteString("tokenId", payload.TokenId);
            writer.WriteString("clusterId", payload.ClusterId);
            writer.WriteString("role", payload.Role);
            writer.WriteString("issuedAt", payload.IssuedAt);
            writer.WriteString("expiresAt", payload.ExpiresAt);
            if (payload.MaxUses is int maxUses)
            {
                writer.WriteNumber("maxUses", maxUses);
            }
            else
            {
                writer.WriteNull("maxUses");
            }

            writer.WriteString("issuer", payload.Issuer);
            writer.WriteEndObject();
        }

        return writerBuffer.WrittenMemory.ToArray();
    }

    private static bool TryReadPayload(byte[] payloadBytes, out BootstrapTokenPayload payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadBytes);
            var root = document.RootElement;
            payload = new BootstrapTokenPayload
            {
                TokenId = root.GetProperty("tokenId").GetGuid(),
                ClusterId = root.GetProperty("clusterId").GetString() ?? "default",
                Role = root.GetProperty("role").GetString() ?? "worker",
                IssuedAt = root.GetProperty("issuedAt").GetDateTimeOffset(),
                ExpiresAt = root.GetProperty("expiresAt").GetDateTimeOffset(),
                MaxUses = root.TryGetProperty("maxUses", out var max) && max.ValueKind != JsonValueKind.Null ? max.GetInt32() : null,
                Issuer = root.GetProperty("issuer").GetString() ?? string.Empty
            };

            return true;
        }
        catch
        {
            payload = default!;
            return false;
        }
    }

    private sealed class BootstrapTokenPayload
    {
        public Guid TokenId { get; set; }

        public string ClusterId { get; set; } = "default";

        public string Role { get; set; } = "worker";

        public DateTimeOffset IssuedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public int? MaxUses { get; set; }

        public string Issuer { get; set; } = string.Empty;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Bootstrap token {TokenId} exceeded its usage allowance.")]
        public static partial void BootstrapTokenReplayed(ILogger logger, Guid tokenId);
    }
}
