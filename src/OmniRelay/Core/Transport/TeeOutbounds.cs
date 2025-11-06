using Hugo;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OmniRelay.Core.Transport;

/// <summary>
/// Options for tee/shadow outbounds controlling sampling, predicate, and headers.
/// </summary>
public sealed class TeeOptions
{
    public double SampleRate { get; init; } = 1.0;
    public bool ShadowOnSuccessOnly { get; init; } = true;
    public Func<RequestMeta, bool>? Predicate { get; init; }
    public string ShadowHeaderName { get; init; } = "rpc-shadow";
    public string ShadowHeaderValue { get; init; } = "true";
    public ILoggerFactory? LoggerFactory { get; init; }
}

public sealed record TeeOutboundDiagnostics(
    object? Primary,
    object? Shadow,
    double SampleRate,
    bool ShadowOnSuccessOnly,
    string ShadowHeaderName,
    string ShadowHeaderValue);

/// <summary>
/// Unary outbound that forwards calls to a primary outbound and optionally shadows to a secondary outbound.
/// </summary>
public sealed class TeeUnaryOutbound : IUnaryOutbound, IOutboundDiagnostic
{
    private readonly IUnaryOutbound _primary;
    private readonly IUnaryOutbound _shadow;
    private readonly TeeOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a tee unary outbound given primary and shadow outbounds.
    /// </summary>
    public TeeUnaryOutbound(IUnaryOutbound primary, IUnaryOutbound shadow, TeeOptions? options = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _options = options ?? new TeeOptions();

        if (_options.SampleRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sample rate must be between 0.0 and 1.0 inclusive.");
        }

        var factory = _options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<TeeUnaryOutbound>();
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _primary.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _shadow.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _primary.StopAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _shadow.StopAsync(cancellationToken).ConfigureAwait(false);
        await _primary.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<Response<ReadOnlyMemory<byte>>>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        var primaryResult = await _primary.CallAsync(request, cancellationToken).ConfigureAwait(false);

        if (ShouldShadow(request.Meta, primaryResult.IsSuccess))
        {
            ScheduleUnaryShadow(request);
        }

        return primaryResult;
    }

    /// <inheritdoc />
    public object GetOutboundDiagnostics()
    {
        object? primaryDiagnostics = _primary is IOutboundDiagnostic diagnostic
            ? diagnostic.GetOutboundDiagnostics()
            : null;

        object? shadowDiagnostics = _shadow is IOutboundDiagnostic shadowDiagnostic
            ? shadowDiagnostic.GetOutboundDiagnostics()
            : null;

        return new TeeOutboundDiagnostics(
            primaryDiagnostics,
            shadowDiagnostics,
            _options.SampleRate,
            _options.ShadowOnSuccessOnly,
            _options.ShadowHeaderName,
            _options.ShadowHeaderValue);
    }

    private bool ShouldShadow(RequestMeta meta, bool primarySucceeded)
    {
        if (_options.ShadowOnSuccessOnly && !primarySucceeded)
        {
            return false;
        }

        if (_options.Predicate is { } predicate && !predicate(meta))
        {
            return false;
        }

        var sampleRate = _options.SampleRate;
        if (sampleRate >= 1.0)
        {
            return true;
        }

        if (sampleRate <= 0.0)
        {
            return false;
        }

        return Random.Shared.NextDouble() < sampleRate;
    }

    private void ScheduleUnaryShadow(IRequest<ReadOnlyMemory<byte>> request)
    {
        var teeMeta = PrepareShadowMeta(request.Meta);
        var payloadCopy = request.Body.ToArray();
        var teeRequest = new Request<ReadOnlyMemory<byte>>(teeMeta, payloadCopy);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _shadow.CallAsync(teeRequest, CancellationToken.None).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    _logger.LogDebug(
                        "Shadow unary call failed for {Service}::{Procedure}: {Message}",
                        teeMeta.Service,
                        teeMeta.Procedure,
                        result.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Shadow unary call threw for {Service}::{Procedure}",
                    teeMeta.Service,
                    teeMeta.Procedure);
            }
        });
    }

    private RequestMeta PrepareShadowMeta(RequestMeta meta)
    {
        if (string.IsNullOrWhiteSpace(_options.ShadowHeaderName))
        {
            return meta;
        }

        return meta.WithHeader(_options.ShadowHeaderName, _options.ShadowHeaderValue);
    }
}

/// <summary>
/// Oneway outbound that forwards calls to a primary outbound and optionally shadows to a secondary outbound.
/// </summary>
public sealed class TeeOnewayOutbound : IOnewayOutbound, IOutboundDiagnostic
{
    private readonly IOnewayOutbound _primary;
    private readonly IOnewayOutbound _shadow;
    private readonly TeeOptions _options;
    private readonly ILogger _logger;

    /// <summary>
    /// Creates a tee oneway outbound given primary and shadow outbounds.
    /// </summary>
    public TeeOnewayOutbound(IOnewayOutbound primary, IOnewayOutbound shadow, TeeOptions? options = null)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _shadow = shadow ?? throw new ArgumentNullException(nameof(shadow));
        _options = options ?? new TeeOptions();

        if (_options.SampleRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Sample rate must be between 0.0 and 1.0 inclusive.");
        }

        var factory = _options.LoggerFactory ?? NullLoggerFactory.Instance;
        _logger = factory.CreateLogger<TeeOnewayOutbound>();
    }

    /// <inheritdoc />
    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _primary.StartAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _shadow.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await _primary.StopAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _shadow.StopAsync(cancellationToken).ConfigureAwait(false);
        await _primary.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask<Result<OnewayAck>> CallAsync(
        IRequest<ReadOnlyMemory<byte>> request,
        CancellationToken cancellationToken = default)
    {
        var primaryResult = await _primary.CallAsync(request, cancellationToken).ConfigureAwait(false);

        if (ShouldShadow(request.Meta, primaryResult.IsSuccess))
        {
            ScheduleOnewayShadow(request);
        }

        return primaryResult;
    }

    /// <inheritdoc />
    public object GetOutboundDiagnostics()
    {
        object? primaryDiagnostics = _primary is IOutboundDiagnostic diagnostic
            ? diagnostic.GetOutboundDiagnostics()
            : null;

        object? shadowDiagnostics = _shadow is IOutboundDiagnostic shadowDiagnostic
            ? shadowDiagnostic.GetOutboundDiagnostics()
            : null;

        return new TeeOutboundDiagnostics(
            primaryDiagnostics,
            shadowDiagnostics,
            _options.SampleRate,
            _options.ShadowOnSuccessOnly,
            _options.ShadowHeaderName,
            _options.ShadowHeaderValue);
    }

    private bool ShouldShadow(RequestMeta meta, bool primarySucceeded)
    {
        if (_options.ShadowOnSuccessOnly && !primarySucceeded)
        {
            return false;
        }

        if (_options.Predicate is { } predicate && !predicate(meta))
        {
            return false;
        }

        var sampleRate = _options.SampleRate;
        if (sampleRate >= 1.0)
        {
            return true;
        }

        if (sampleRate <= 0.0)
        {
            return false;
        }

        return Random.Shared.NextDouble() < sampleRate;
    }

    private void ScheduleOnewayShadow(IRequest<ReadOnlyMemory<byte>> request)
    {
        var teeMeta = PrepareShadowMeta(request.Meta);
        var payloadCopy = request.Body.ToArray();
        var teeRequest = new Request<ReadOnlyMemory<byte>>(teeMeta, payloadCopy);

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _shadow.CallAsync(teeRequest, CancellationToken.None).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    _logger.LogDebug(
                        "Shadow oneway call failed for {Service}::{Procedure}: {Message}",
                        teeMeta.Service,
                        teeMeta.Procedure,
                        result.Error?.Message);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Shadow oneway call threw for {Service}::{Procedure}",
                    teeMeta.Service,
                    teeMeta.Procedure);
            }
        });
    }

    private RequestMeta PrepareShadowMeta(RequestMeta meta)
    {
        if (string.IsNullOrWhiteSpace(_options.ShadowHeaderName))
        {
            return meta;
        }

        return meta.WithHeader(_options.ShadowHeaderName, _options.ShadowHeaderValue);
    }
}
