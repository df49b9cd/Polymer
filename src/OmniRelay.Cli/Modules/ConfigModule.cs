#pragma warning disable IDE0005
using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace OmniRelay.Cli.Modules;

/// <summary>Configuration utilities (validation + scaffold) kept AOT-friendly.</summary>
internal sealed class ConfigCommandsModule : ICliModule
{
    public Command Build()
    {
        var command = new System.CommandLine.Command("config", "Configuration utilities.");
        command.Subcommands.Add(CreateValidateCommand());
        command.Subcommands.Add(CreateScaffoldCommand());
        return command;
    }

    private static Command CreateValidateCommand()
    {
        var command = new Command("validate", "Validate configuration files.");

        var configOption = new Option<string[]>("--config")
        {
            Description = "Configuration file(s) to load.",
            AllowMultipleArgumentsPerToken = true,
            Required = true
        };
        configOption.Aliases.Add("-c");

        var sectionOption = new Option<string>("--section")
        {
            Description = "Configuration section root.",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override configuration values (KEY=VALUE).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);

        command.SetAction(parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? Array.Empty<string>();
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? Array.Empty<string>();

            if (!Program.TryBuildConfiguration(configs, overrides, out var configuration, out var error))
            {
                CliRuntime.Console.WriteError(error ?? "Configuration build failed.");
                return 1;
            }

            var sectionRoot = configuration.GetSection(section);
            if (!sectionRoot.Exists())
            {
                CliRuntime.Console.WriteError($"Section '{section}' not found in configuration.");
                return 1;
            }

            var serviceName = sectionRoot.GetValue<string>("service");
            if (string.IsNullOrWhiteSpace(serviceName))
            {
                CliRuntime.Console.WriteLine($"Configuration valid for section '{section}'.");
            }
            else
            {
                CliRuntime.Console.WriteLine($"Configuration valid for service '{serviceName}' in section '{section}'.");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateScaffoldCommand()
    {
        var command = new Command("scaffold", "Emit a starter configuration document.");

        var outputOption = new Option<string>("--output")
        {
            Description = "Path to write the scaffold JSON.",
            Required = true
        };

        var sectionOption = new Option<string>("--section")
        {
            Description = "Root section name.",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var serviceOption = new Option<string>("--service")
        {
            Description = "Service name for the scaffolded config.",
            DefaultValueFactory = _ => "sample"
        };

        var http3HttpOption = new Option<bool>("--http3-http")
        {
            Description = "Include an HTTPS/HTTP3 inbound listener."
        };

        var http3GrpcOption = new Option<bool>("--http3-grpc")
        {
            Description = "Include an HTTP3-capable gRPC inbound listener."
        };

        var includeOutboundOption = new Option<bool>("--include-outbound")
        {
            Description = "Include a sample outbound pointing at a peer service."
        };

        var outboundServiceOption = new Option<string>("--outbound-service")
        {
            Description = "Remote service name for outbound scaffold entries.",
            DefaultValueFactory = _ => "audit"
        };

        command.Add(outputOption);
        command.Add(sectionOption);
        command.Add(serviceOption);
        command.Add(http3HttpOption);
        command.Add(http3GrpcOption);
        command.Add(includeOutboundOption);
        command.Add(outboundServiceOption);

        command.SetAction(async parseResult =>
        {
            var outputPath = parseResult.GetValue(outputOption) ?? string.Empty;
            var scaffoldOptions = new ScaffoldOptions(
                Section: parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection,
                Service: parseResult.GetValue(serviceOption) ?? "sample",
                IncludeHttp3Http: parseResult.GetValue(http3HttpOption),
                IncludeHttp3Grpc: parseResult.GetValue(http3GrpcOption),
                IncludeOutbound: parseResult.GetValue(includeOutboundOption),
                OutboundService: parseResult.GetValue(outboundServiceOption) ?? "audit");

            try
            {
                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                WriteScaffold(stream, scaffoldOptions);
                CliRuntime.Console.WriteLine($"Wrote configuration scaffold to '{outputPath}'.");
                return 0;
            }
            catch (Exception ex)
            {
                CliRuntime.Console.WriteError($"Failed to write scaffold: {ex.Message}");
                return 1;
            }
        });

        return command;
    }

    private static void WriteScaffold(Stream output, ScaffoldOptions options)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });

        writer.WriteStartObject();
        writer.WritePropertyName(options.Section);
        writer.WriteStartObject();
        writer.WriteString("service", options.Service);

        writer.WritePropertyName("inbounds");
        writer.WriteStartObject();

        writer.WritePropertyName("http");
        writer.WriteStartArray();
        WriteHttpInbound(writer, "http://0.0.0.0:8080", enableHttp3: false);
        if (options.IncludeHttp3Http)
        {
            WriteHttpInbound(writer, "https://0.0.0.0:8443", enableHttp3: true);
        }
        writer.WriteEndArray();

        writer.WritePropertyName("grpc");
        writer.WriteStartArray();
        WriteGrpcInbound(writer, "http://0.0.0.0:8090", enableHttp3: false);
        if (options.IncludeHttp3Grpc)
        {
            WriteGrpcInbound(writer, "https://0.0.0.0:9091", enableHttp3: true);
        }
        writer.WriteEndArray();

        writer.WriteEndObject(); // inbounds

        if (options.IncludeOutbound)
        {
            WriteOutbound(writer, options.OutboundService);
        }

        writer.WriteEndObject(); // section
        writer.WriteEndObject(); // root
    }

    private static void WriteHttpInbound(Utf8JsonWriter writer, string url, bool enableHttp3)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("urls");
        writer.WriteStartArray();
        writer.WriteStringValue(url);
        writer.WriteEndArray();

        writer.WritePropertyName("runtime");
        writer.WriteStartObject();
        if (enableHttp3)
        {
            writer.WriteBoolean("enableHttp3", true);
            writer.WritePropertyName("http3");
            writer.WriteStartObject();
            writer.WriteBoolean("enableAltSvc", true);
            writer.WriteNumber("maxBidirectionalStreams", 128);
            writer.WriteNumber("maxUnidirectionalStreams", 32);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNumber("maxRequestBodySize", 8_388_608);
        }
        writer.WriteEndObject();

        if (enableHttp3)
        {
            WriteTls(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteGrpcInbound(Utf8JsonWriter writer, string url, bool enableHttp3)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("urls");
        writer.WriteStartArray();
        writer.WriteStringValue(url);
        writer.WriteEndArray();

        writer.WritePropertyName("runtime");
        writer.WriteStartObject();
        if (enableHttp3)
        {
            writer.WriteBoolean("enableHttp3", true);
        }
        else
        {
            writer.WriteNumber("maxReceiveMessageSize", 8_388_608);
            writer.WriteNumber("maxSendMessageSize", 8_388_608);
        }
        writer.WriteEndObject();

        if (enableHttp3)
        {
            WriteTls(writer);
        }

        writer.WriteEndObject();
    }

    private static void WriteTls(Utf8JsonWriter writer)
    {
        writer.WritePropertyName("tls");
        writer.WriteStartObject();
        writer.WriteString("certificatePath", "certs/server.pfx");
        writer.WriteString("certificatePassword", "change-me");
        writer.WriteEndObject();
    }

    private static void WriteOutbound(Utf8JsonWriter writer, string outboundService)
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

    private readonly record struct ScaffoldOptions(
        string Section,
        string Service,
        bool IncludeHttp3Http,
        bool IncludeHttp3Grpc,
        bool IncludeOutbound,
        string OutboundService);
}
