using System.Net;
using System.Text;
using System.Text.Json;
using Hugo;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Bootstrap;

/// <summary>HTTP client for interacting with bootstrap servers.</summary>
public sealed class BootstrapClient
{
    private const string JoinPath = "/omnirelay/bootstrap/join";

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;

    public BootstrapClient(HttpClient httpClient, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<Result<BootstrapJoinResponse>> JoinAsync(
        Uri baseUri,
        BootstrapJoinRequest request,
        TimeSpan? joinTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(request);

        var timeout = ResolveTimeout(joinTimeout);
        if (timeout.HasValue)
        {
            return WithTimeoutValueTaskAsync(
                    token => SendJoinAsync(baseUri, request, token),
                    timeout.Value,
                    _timeProvider,
                    cancellationToken);
        }

        return SendJoinAsync(baseUri, request, cancellationToken);
    }

    private async ValueTask<Result<BootstrapJoinResponse>> SendJoinAsync(
        Uri baseUri,
        BootstrapJoinRequest request,
        CancellationToken cancellationToken)
    {
        var endpoint = new Uri(baseUri, JoinPath);
        var requestPayload = JsonSerializer.Serialize(request, BootstrapJsonContext.Default.BootstrapJoinRequest);
        using var content = new StringContent(requestPayload, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(endpoint, content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return Result.Fail<BootstrapJoinResponse>(
                BuildError(response.StatusCode, response.ReasonPhrase, body));
        }

        var payload = JsonSerializer.Deserialize(body, BootstrapJsonContext.Default.BootstrapJoinResponse);
        return payload is null
            ? Result.Fail<BootstrapJoinResponse>(Error.From("Bootstrap server returned an empty response.", "bootstrap.join.empty_payload"))
            : Ok(payload);
    }

    private TimeSpan? ResolveTimeout(TimeSpan? joinTimeout)
    {
        if (joinTimeout.HasValue && joinTimeout.Value > TimeSpan.Zero && joinTimeout.Value != Timeout.InfiniteTimeSpan)
        {
            return joinTimeout;
        }

        var clientTimeout = _httpClient.Timeout;
        return clientTimeout > TimeSpan.Zero && clientTimeout != Timeout.InfiniteTimeSpan ? clientTimeout : null;
    }

    private static Error BuildError(HttpStatusCode statusCode, string? reason, string payload)
    {
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize(payload, BootstrapJsonContext.Default.BootstrapErrorResponse);
                if (parsed is not null)
                {
                    return Error.From(parsed.Message ?? "Bootstrap server returned an error.", parsed.Code ?? ErrorCodes.Validation);
                }
            }
            catch (JsonException)
            {
                // Fall through to raw error construction.
            }
        }

        return Error.From(
                $"Bootstrap join failed: {(int)statusCode} {reason ?? "unknown"}.",
                ErrorCodes.Validation)
            .WithMetadata("statusCode", (int)statusCode)
            .WithMetadata("response", payload ?? string.Empty);
    }
}
