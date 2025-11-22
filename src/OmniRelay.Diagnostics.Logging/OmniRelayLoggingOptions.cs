using Microsoft.Extensions.Logging;

namespace OmniRelay.Diagnostics;

/// <summary>Options controlling OmniRelay logging defaults.</summary>
public sealed class OmniRelayLoggingOptions
{
    public LogLevel? MinimumLevel { get; set; }

    public IDictionary<string, LogLevel> CategoryLevels { get; }
        = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);

    public bool EnableConsoleLogger { get; set; } = true;

    public bool UseSingleLineConsole { get; set; } = true;

    public string TimestampFormat { get; set; } = "HH:mm:ss ";
}
