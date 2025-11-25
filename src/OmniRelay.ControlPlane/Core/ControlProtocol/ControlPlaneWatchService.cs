using System.Collections.Immutable;
using Google.Protobuf;
using Grpc.Core;
using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OmniRelay.Protos.Control;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.ControlProtocol;

/// <summary>
/// Control-plane watch service implementing WORK-006 (capability negotiation, resume/backoff, observability).
/// </summary>
public sealed class ControlPlaneWatchService : ControlPlaneWatch.ControlPlaneWatchBase
{
    private readonly IControlPlaneUpdateSource _updates;
    private readonly ControlProtocolOptions _options;
    private readonly ILogger<ControlPlaneWatchService> _logger;
    private readonly ImmutableHashSet<string> _supportedCaps;

    public ControlPlaneWatchService(
        IControlPlaneUpdateSource updates,
        IOptions<ControlProtocolOptions> options,
        ILogger<ControlPlaneWatchService> logger)
    {
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _supportedCaps = _options.SupportedCapabilities.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public override Task<ControlSnapshotResponse> Snapshot(ControlSnapshotRequest request, ServerCallContext context)
    {
        var snapshot = BuildSnapshot(request);
        if (snapshot.IsFailure)
        {
            throw ToRpcException(snapshot.Error!);
        }

        return Task.FromResult(snapshot.Value);
    }

    public override async Task Watch(ControlWatchRequest request, IServerStreamWriter<ControlWatchResponse> responseStream, ServerCallContext context)
    {
        var handshake = ValidateCapabilities(request.Capabilities);
        if (handshake.IsFailure)
        {
            await responseStream.WriteAsync(CreateErrorResponse(handshake.Error!, _options.UnsupportedCapabilityBackoff)).ConfigureAwait(false);
            return;
        }

        var currentResult = _updates.Current;
        if (currentResult.IsFailure)
        {
            await responseStream.WriteAsync(CreateErrorResponse(currentResult.Error!, _options.DefaultBackoff)).ConfigureAwait(false);
            return;
        }

        var current = currentResult.Value;
        var needsFullSnapshot = RequiresFullSnapshot(request.ResumeToken, current);
        if (!CapabilitiesSatisfied(request.Capabilities, current.RequiredCapabilities))
        {
            var error = ControlProtocolErrors.MissingRequiredCapabilities(current.RequiredCapabilities, request.Capabilities);
            await responseStream.WriteAsync(CreateErrorResponse(error, _options.UnsupportedCapabilityBackoff)).ConfigureAwait(false);
            return;
        }

        var initialResponse = BuildWatchResponse(current, request.NodeId, needsFullSnapshot, _options.DefaultBackoff);
        await responseStream.WriteAsync(initialResponse).ConfigureAwait(false);

        var subscriptionResult = await _updates.SubscribeAsync(context.CancellationToken).ConfigureAwait(false);
        if (subscriptionResult.IsFailure)
        {
            await responseStream.WriteAsync(CreateErrorResponse(subscriptionResult.Error!, _options.DefaultBackoff)).ConfigureAwait(false);
            return;
        }

        var subscription = subscriptionResult.Value;
        try
        {
            var enumerator = subscription.Reader.ReadAllAsync(context.CancellationToken).GetAsyncEnumerator();
            try
            {
                while (await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    var update = enumerator.Current;
                    if (!CapabilitiesSatisfied(request.Capabilities, update.RequiredCapabilities))
                    {
                        var error = ControlProtocolErrors.MissingRequiredCapabilities(update.RequiredCapabilities, request.Capabilities);
                        await responseStream.WriteAsync(CreateErrorResponse(error, _options.UnsupportedCapabilityBackoff)).ConfigureAwait(false);
                        return;
                    }

                    var response = BuildWatchResponse(update, request.NodeId, update.FullSnapshot, _options.DefaultBackoff);
                    await responseStream.WriteAsync(response).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                await enumerator.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private Result<ControlSnapshotResponse> BuildSnapshot(ControlSnapshotRequest request)
    {
        var capabilityCheck = ValidateCapabilities(request.Capabilities);
        if (capabilityCheck.IsFailure)
        {
            return Err<ControlSnapshotResponse>(capabilityCheck.Error!);
        }

        var currentResult = _updates.Current;
        if (currentResult.IsFailure)
        {
            return Err<ControlSnapshotResponse>(currentResult.Error!);
        }

        var current = currentResult.Value;
        if (!CapabilitiesSatisfied(request.Capabilities, current.RequiredCapabilities))
        {
            return Err<ControlSnapshotResponse>(ControlProtocolErrors.MissingRequiredCapabilities(current.RequiredCapabilities, request.Capabilities));
        }

        var response = new ControlSnapshotResponse
        {
            Version = current.Version,
            Epoch = current.Epoch,
            Payload = ByteString.CopyFrom(current.Payload.Span)
        };
        response.RequiredCapabilities.AddRange(current.RequiredCapabilities);
        return Ok(response);
    }

    private Result<Unit> ValidateCapabilities(CapabilitySet? advertised)
    {
        if (advertised?.Items is null || advertised.Items.Count == 0)
        {
            return Ok(Unit.Value);
        }

        var unsupported = new List<string>();
        foreach (var cap in advertised.Items)
        {
            if (!_supportedCaps.Contains(cap))
            {
                unsupported.Add(cap);
            }
        }

        return unsupported.Count > 0
            ? Err<Unit>(ControlProtocolErrors.UnsupportedCapabilities(unsupported, advertised))
            : Ok(Unit.Value);
    }

    private static bool CapabilitiesSatisfied(CapabilitySet? advertised, IReadOnlyList<string> required)
    {
        if (required.Count == 0)
        {
            return true;
        }

        var advertisedSet = advertised?.Items is null
            ? Array.Empty<string>()
            : advertised.Items.ToArray();

        return required.All(capability => advertisedSet.Contains(capability, StringComparer.OrdinalIgnoreCase));
    }

    private static bool RequiresFullSnapshot(WatchResumeToken? resumeToken, ControlPlaneUpdate current)
    {
        if (resumeToken is null)
        {
            return true;
        }

        if (!string.Equals(resumeToken.Version, current.Version, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return resumeToken.Epoch != current.Epoch;
    }

    private static ControlWatchResponse BuildWatchResponse(
        ControlPlaneUpdate update,
        string? nodeId,
        bool fullSnapshot,
        TimeSpan backoff)
    {
        var response = new ControlWatchResponse
        {
            Version = update.Version,
            Epoch = update.Epoch,
            Payload = ByteString.CopyFrom(update.Payload.Span),
            FullSnapshot = fullSnapshot,
            ResumeToken = update.ToResumeToken(nodeId),
            Backoff = new ControlBackoff { Millis = (long)backoff.TotalMilliseconds }
        };

        response.RequiredCapabilities.AddRange(update.RequiredCapabilities);
        return response;
    }

    private static ControlWatchResponse CreateErrorResponse(Error error, TimeSpan backoff)
    {
        var response = new ControlWatchResponse
        {
            Error = new ControlError
            {
                Code = string.IsNullOrWhiteSpace(error.Code) ? ControlProtocolErrors.UnsupportedCapabilityCode : error.Code!,
                Message = error.Message,
                Remediation = TryGetMetadata(error, "remediation")
            },
            Backoff = new ControlBackoff { Millis = (long)backoff.TotalMilliseconds }
        };

        var required = TryParseCapabilities(error, "required");
        if (required.Length > 0)
        {
            response.RequiredCapabilities.AddRange(required);
        }

        var unsupported = TryParseCapabilities(error, "unsupported");
        if (unsupported.Length > 0)
        {
            response.RequiredCapabilities.AddRange(unsupported);
        }

        return response;
    }

    private static string? TryGetMetadata(Error error, string key)
    {
        if (error.Metadata is null || error.Metadata.Count == 0)
        {
            return null;
        }

        return error.Metadata.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;
    }

    private static string[] TryParseCapabilities(Error error, string key)
    {
        if (error.Metadata is null || error.Metadata.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (error.Metadata.TryGetValue(key, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
        {
            return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return Array.Empty<string>();
    }

    private static RpcException ToRpcException(Error error)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrWhiteSpace(error.Code))
        {
            metadata.Add("error-code", error.Code);
        }

        if (error.Metadata is not null)
        {
            foreach (var pair in error.Metadata)
            {
                if (pair.Value is string value)
                {
                    metadata.Add(pair.Key, value);
                }
            }
        }

        var status = new Status(StatusCode.FailedPrecondition, error.Message ?? "control-plane error");
        return new RpcException(status, metadata);
    }
}
