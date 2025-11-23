using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal sealed class DslExtensionHost
{
    private readonly DslExtensionHostOptions _options;
    private readonly ExtensionTelemetryRecorder _telemetry;
    private readonly ExtensionRegistry _registry;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFailure = new(StringComparer.OrdinalIgnoreCase);

    public DslExtensionHost(DslExtensionHostOptions options, ExtensionRegistry registry, ILogger<DslExtensionHost> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _telemetry = new ExtensionTelemetryRecorder(logger);
    }

    public bool TryLoad(ExtensionPackage package, out DslProgram program)
    {
        program = default!;

        if (package.Type != ExtensionType.Dsl)
        {
            _telemetry.RecordRejected(package, "wrong type");
            _registry.RecordRejected(package, "wrong type");
            return false;
        }

        if (!package.IsSigned(_options.SignatureAlgorithm, _options.PublicKey))
        {
            _telemetry.RecordRejected(package, "invalid signature");
            _registry.RecordRejected(package, "invalid signature");
            return false;
        }

        if (!DslProgram.TryParse(package, _options.AllowedOpcodes, _options.MaxInstructions, out program, out var parseError))
        {
            _telemetry.RecordRejected(package, $"parse error: {parseError}");
            _registry.RecordRejected(package, $"parse error: {parseError}");
            return false;
        }

        _telemetry.RecordLoaded(package);
        _registry.RecordLoaded(package);
        return true;
    }

    public bool TryExecute(ref DslProgram program, ReadOnlySpan<byte> input, out byte[] output, bool allowReload = true)
    {
        var sw = ValueStopwatch.StartNew();
        var execResult = program.Execute(input, _options.MaxInstructions, _options.MaxOutputBytes, _options.MaxExecutionTime, sw, out output, out var watchdogReason);
        var durationMs = sw.Elapsed.TotalMilliseconds;

        if (execResult)
        {
            _telemetry.RecordExecuted(program.Package, durationMs);
            _registry.RecordExecuted(program.Package, durationMs);
            return true;
        }

        if (watchdogReason is not null)
        {
            _telemetry.RecordWatchdogTrip(program.Package, watchdogReason);
            _registry.RecordWatchdog(program.Package, watchdogReason);

            if (allowReload && TryReload(ref program))
            {
                return TryExecute(ref program, input, out output, allowReload: false);
            }

            if (_options.FailurePolicy.FailOpen)
            {
                output = input.ToArray();
                return true;
            }
        }
        else
        {
            _telemetry.RecordFailed(program.Package, "execution failed");
            _registry.RecordFailed(program.Package, "execution failed");

            if (allowReload && TryReload(ref program))
            {
                return TryExecute(ref program, input, out output, allowReload: false);
            }
        }

        return false;
    }

    private bool TryReload(ref DslProgram program)
    {
        if (!_options.FailurePolicy.ReloadOnFailure)
        {
            return false;
        }

        if (_options.FailurePolicy.Cooldown is { } cooldown && cooldown > TimeSpan.Zero)
        {
            var lastFailure = _lastFailure.GetOrAdd(program.Package.Name, static _ => DateTimeOffset.MinValue);
            var now = DateTimeOffset.UtcNow;
            if (now - lastFailure < cooldown)
            {
                return false;
            }

            _lastFailure[program.Package.Name] = now;
        }

        if (!DslProgram.TryParse(program.Package, _options.AllowedOpcodes, _options.MaxInstructions, out var reloaded, out var parseError))
        {
            _telemetry.RecordFailed(program.Package, $"reload parse error: {parseError}");
            _registry.RecordFailed(program.Package, $"reload parse error: {parseError}");
            return false;
        }

        _telemetry.RecordReloaded(program.Package);
        _registry.RecordLoaded(program.Package);
        program = reloaded;
        return true;
    }
}
