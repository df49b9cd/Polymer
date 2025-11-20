using System.CommandLine;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Configuration;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Configuration-related commands (validate + scaffold).
/// </summary>
internal sealed class ConfigCommandsModule : ICliModule
{
    public Command Build() => CreateConfigCommand();

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Configuration utilities.")
        {
            CreateConfigValidateCommand(),
            CreateConfigScaffoldCommand()
        };
        return command;
    }

    private static Command CreateConfigValidateCommand()
    {
        var command = new Command("validate", "Validate OmniRelay dispatcher configuration.");

        var configOption = new Option<string[]>("--config")
        {
            Description = "Configuration file(s) to load.",
            AllowMultipleArgumentsPerToken = true,
            Required = true,
            Arity = ArgumentArity.OneOrMore
        };
        configOption.Aliases.Add("-c");

        var sectionOption = new Option<string>("--section")
        {
            Description = "Root configuration section name.",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override configuration values (KEY=VALUE).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);

        command.SetAction(parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? [];
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? [];
            return RunConfigValidateAsync(configs, section, overrides).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command CreateConfigScaffoldCommand()
    {
        var command = new Command("scaffold", "Generate an example appsettings.json with optional HTTP/3 toggles.");

        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the appsettings file.",
            DefaultValueFactory = _ => "appsettings.json"
        };
        outputOption.Aliases.Add("-o");

        var sectionOption = new Option<string>("--section")
        {
            Description = "Root configuration section name.",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var serviceOption = new Option<string>("--service")
        {
            Description = "Service name to embed in the scaffolded config.",
            DefaultValueFactory = _ => "sample"
        };

        var enableHttp3HttpOption = new Option<bool>("--http3-http")
        {
            Description = "Include an HTTPS HTTP inbound with HTTP/3 enabled."
        };

        var enableHttp3GrpcOption = new Option<bool>("--http3-grpc")
        {
            Description = "Include an HTTPS gRPC inbound with HTTP/3 enabled."
        };

        var includeOutboundOption = new Option<bool>("--include-outbound")
        {
            Description = "Include an example outbound with endpoints that indicate HTTP/3 support."
        };

        var outboundServiceOption = new Option<string>("--outbound-service")
        {
            Description = "Name for the example outbound service entry.",
            DefaultValueFactory = _ => "ledger"
        };

        command.Add(outputOption);
        command.Add(sectionOption);
        command.Add(serviceOption);
        command.Add(enableHttp3HttpOption);
        command.Add(enableHttp3GrpcOption);
        command.Add(includeOutboundOption);
        command.Add(outboundServiceOption);

        command.SetAction(parseResult =>
        {
            var output = parseResult.GetValue(outputOption) ?? "appsettings.json";
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var service = parseResult.GetValue(serviceOption) ?? "sample";
            var http3Http = parseResult.GetValue(enableHttp3HttpOption);
            var http3Grpc = parseResult.GetValue(enableHttp3GrpcOption);
            var includeOutbound = parseResult.GetValue(includeOutboundOption);
            var outboundService = parseResult.GetValue(outboundServiceOption) ?? "ledger";
            return RunConfigScaffoldAsync(output, section, service, http3Http, http3Grpc, includeOutbound, outboundService).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static async Task<int> RunConfigValidateAsync(string[] configPaths, string section, string[] setOverrides)
    {
        if (!Program.TryBuildConfiguration(configPaths, setOverrides, out var configuration, out var errorMessage))
        {
            await Console.Error.WriteLineAsync(errorMessage ?? "Failed to load configuration.").ConfigureAwait(false);
            return 1;
        }

        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));

        try
        {
            services.AddOmniRelayDispatcher(configuration.GetSection(section ?? Program.DefaultConfigSection));
        }
        catch (OmniRelayConfigurationException ex)
        {
            await Console.Error.WriteLineAsync($"Configuration invalid: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to configure OmniRelay dispatcher: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        try
        {
            var provider = services.BuildServiceProvider();
            await using var providerScope = provider.ConfigureAwait(false);
            var dispatcher = provider.GetRequiredService<Dispatcher.Dispatcher>();
            var summary = dispatcher.Introspect();

            Console.WriteLine($"Configuration valid for service '{summary.Service}'.");
            Console.WriteLine($"Status: {summary.Status}");
            Console.WriteLine("Procedures:");
            Console.WriteLine($"  Unary:        {summary.Procedures.Unary.Length}");
            Console.WriteLine($"  Oneway:       {summary.Procedures.Oneway.Length}");
            Console.WriteLine($"  Stream:       {summary.Procedures.Stream.Length}");
            Console.WriteLine($"  ClientStream: {summary.Procedures.ClientStream.Length}");
            Console.WriteLine($"  Duplex:       {summary.Procedures.Duplex.Length}");

            if (summary.Components.Length <= 0)
            {
                return 0;
            }

            Console.WriteLine("Lifecycle components:");
            foreach (var component in summary.Components)
            {
                Console.WriteLine($"  - {component.Name} ({component.ComponentType})");
            }

            return 0;
        }
        catch (OmniRelayConfigurationException ex)
        {
            await Console.Error.WriteLineAsync($"Configuration validation failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Dispatcher validation threw: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    internal static async Task<int> RunConfigScaffoldAsync(
        string outputPath,
        string section,
        string service,
        bool includeHttp3HttpInbound,
        bool includeHttp3GrpcInbound,
        bool includeOutbound,
        string outboundService)
    {
        try
        {
            var scaffold = BuildScaffoldJson(section, service, includeHttp3HttpInbound, includeHttp3GrpcInbound, includeOutbound, outboundService);
            var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, scaffold, Encoding.UTF8).ConfigureAwait(false);
            Console.WriteLine($"Wrote configuration scaffold to '{outputPath}'.");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to write scaffold: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static string BuildScaffoldJson(
        string section,
        string service,
        bool includeHttp3HttpInbound,
        bool includeHttp3GrpcInbound,
        bool includeOutbound,
        string outboundService)
    {
        // Minimal baseline with optional HTTP/3 inbounds and example outbound endpoints including supportsHttp3 hints
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName(section);
        writer.WriteStartObject();

        writer.WriteString("service", service);

        writer.WritePropertyName("inbounds");
        writer.WriteStartObject();

        writer.WritePropertyName("http");
        writer.WriteStartArray();
        // Always include an HTTP listener (non-HTTP3). Add an HTTPS+HTTP3 if requested.
        writer.WriteStartObject();
        writer.WritePropertyName("urls");
        writer.WriteStartArray();
        writer.WriteStringValue("http://0.0.0.0:8080");
        writer.WriteEndArray();
        writer.WritePropertyName("runtime");
        writer.WriteStartObject();
        writer.WriteNumber("maxRequestBodySize", 8388608);
        writer.WriteEndObject();
        writer.WriteEndObject();

        if (includeHttp3HttpInbound)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("urls");
            writer.WriteStartArray();
            writer.WriteStringValue("https://0.0.0.0:8443");
            writer.WriteEndArray();
            writer.WritePropertyName("runtime");
            writer.WriteStartObject();
            writer.WriteBoolean("enableHttp3", true);
            writer.WritePropertyName("http3");
            writer.WriteStartObject();
            writer.WriteBoolean("enableAltSvc", true);
            writer.WriteNumber("maxBidirectionalStreams", 128);
            writer.WriteNumber("maxUnidirectionalStreams", 32);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WritePropertyName("tls");
            writer.WriteStartObject();
            writer.WriteString("certificatePath", "certs/server.pfx");
            writer.WriteString("certificatePassword", "change-me");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();

        writer.WritePropertyName("grpc");
        writer.WriteStartArray();
        // Always include an HTTP/2-only gRPC listener
        writer.WriteStartObject();
        writer.WritePropertyName("urls");
        writer.WriteStartArray();
        writer.WriteStringValue("http://0.0.0.0:8090");
        writer.WriteEndArray();
        writer.WritePropertyName("runtime");
        writer.WriteStartObject();
        writer.WriteNumber("maxReceiveMessageSize", 8388608);
        writer.WriteNumber("maxSendMessageSize", 8388608);
        writer.WriteEndObject();
        writer.WriteEndObject();

        if (includeHttp3GrpcInbound)
        {
            writer.WriteStartObject();
            writer.WritePropertyName("urls");
            writer.WriteStartArray();
            writer.WriteStringValue("https://0.0.0.0:9091");
            writer.WriteEndArray();
            writer.WritePropertyName("runtime");
            writer.WriteStartObject();
            writer.WriteBoolean("enableHttp3", true);
            writer.WriteEndObject();
            writer.WritePropertyName("tls");
            writer.WriteStartObject();
            writer.WriteString("certificatePath", "certs/server.pfx");
            writer.WriteString("certificatePassword", "change-me");
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        writer.WriteEndArray(); // grpc
        writer.WriteEndObject(); // inbounds

        if (includeOutbound)
        {
            writer.WritePropertyName("outbounds");
            writer.WriteStartObject();
            writer.WritePropertyName(outboundService);
            writer.WriteStartObject();
            writer.WritePropertyName("unary");
            writer.WriteStartObject();
            writer.WritePropertyName("grpc");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("endpoints");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WriteString("address", "https://peer-h3:9091");
            writer.WriteBoolean("supportsHttp3", true);
            writer.WriteEndObject();
            writer.WriteStartObject();
            writer.WriteString("address", "https://peer-h2:9090");
            writer.WriteBoolean("supportsHttp3", false);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("remoteService", outboundService);
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject(); // unary
            writer.WriteEndObject(); // service
            writer.WriteEndObject(); // outbounds
        }

        writer.WriteEndObject(); // section
        writer.WriteEndObject(); // root
        writer.Flush();

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
