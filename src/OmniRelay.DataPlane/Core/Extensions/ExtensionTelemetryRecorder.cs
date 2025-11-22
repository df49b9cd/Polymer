using Microsoft.Extensions.Logging;

namespace OmniRelay.Core.Extensions;

internal sealed class ExtensionTelemetryRecorder
{
    private readonly ILogger _logger;

    public ExtensionTelemetryRecorder(ILogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void RecordLoaded(ExtensionPackage package) => ExtensionTelemetryLog.ExtensionLoaded(_logger, package.Name, package.Version.ToString(), package.Type.ToString());
    public void RecordRejected(ExtensionPackage package, string reason) => ExtensionTelemetryLog.ExtensionRejected(_logger, package.Name, package.Version.ToString(), reason);
    public void RecordFailed(ExtensionPackage package, string reason) => ExtensionTelemetryLog.ExtensionFailure(_logger, package.Name, package.Version.ToString(), reason);
    public void RecordWatchdogTrip(ExtensionPackage package, string resource) => ExtensionTelemetryLog.ExtensionWatchdogTrip(_logger, package.Name, package.Version.ToString(), resource);
}
