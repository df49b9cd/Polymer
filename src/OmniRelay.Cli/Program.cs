using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Google.Protobuf.WellKnownTypes;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OmniRelay.Configuration;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli;

public static class Program
{
    private const string DefaultConfigSection = "polymer";
    private const string DefaultIntrospectionUrl = "http://127.0.0.1:8080/omnirelay/introspect";
    private static readonly JsonSerializerOptions PrettyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly IReadOnlyDictionary<string, FileDescriptor> WellKnownFileDescriptors = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal)
    {
        ["google/protobuf/any.proto"] = AnyReflection.Descriptor,
        ["google/protobuf/api.proto"] = ApiReflection.Descriptor,
        ["google/protobuf/duration.proto"] = DurationReflection.Descriptor,
        ["google/protobuf/empty.proto"] = EmptyReflection.Descriptor,
        ["google/protobuf/field_mask.proto"] = FieldMaskReflection.Descriptor,
        ["google/protobuf/source_context.proto"] = SourceContextReflection.Descriptor,
        ["google/protobuf/struct.proto"] = StructReflection.Descriptor,
        ["google/protobuf/timestamp.proto"] = TimestampReflection.Descriptor,
        ["google/protobuf/type.proto"] = TypeReflection.Descriptor,
        ["google/protobuf/wrappers.proto"] = WrappersReflection.Descriptor
    };
    private static MethodInfo? _fileDescriptorBuildFrom;
    private static readonly ConcurrentDictionary<string, DescriptorCacheEntry> DescriptorCache = new(StringComparer.Ordinal);

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var root = BuildRootCommand();
            var parseResult = root.Parse(args, new ParserConfiguration());
            return await parseResult.InvokeAsync(new InvocationConfiguration(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Operation cancelled.").ConfigureAwait(false);
            return 2;
        }
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("OmniRelay CLI providing configuration validation, dispatcher introspection, and ad-hoc request tooling.")
        {
            CreateConfigCommand(),
            CreateServeCommand(),
            CreateIntrospectCommand(),
            CreateRequestCommand(),
            CreateBenchmarkCommand(),
            CreateScriptCommand()
        };
        return root;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Configuration utilities.")
        {
            CreateConfigValidateCommand(),
            CreateConfigScaffoldCommand()
        };
        return command;
    }

    private static Command CreateServeCommand()
    {
        var command = new Command("serve", "Run an OmniRelay dispatcher using configuration files.");

        var configOption = new Option<string[]>("--config")
        {
            Description = "Configuration file(s) to load.",
            AllowMultipleArgumentsPerToken = true,
            Required = true,
            Arity = ArgumentArity.OneOrMore
        };

        var sectionOption = new Option<string>("--section")
        {
            Description = "Root configuration section name.",
            DefaultValueFactory = _ => DefaultConfigSection
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override configuration values (KEY=VALUE).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var readyFileOption = new Option<string?>("--ready-file")
        {
            Description = "Touch the specified file once the dispatcher starts."
        };

        var shutdownAfterOption = new Option<string?>("--shutdown-after")
        {
            Description = "Automatically shut down after the specified duration (e.g. 30s)."
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);
        command.Add(readyFileOption);
        command.Add(shutdownAfterOption);

        command.SetAction(parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? [];
            var section = parseResult.GetValue(sectionOption) ?? DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? [];
            var readyFile = parseResult.GetValue(readyFileOption);
            var shutdownAfter = parseResult.GetValue(shutdownAfterOption);
            return RunServeAsync(configs, section, overrides, readyFile, shutdownAfter).GetAwaiter().GetResult();
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
            DefaultValueFactory = _ => DefaultConfigSection
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
            var section = parseResult.GetValue(sectionOption) ?? DefaultConfigSection;
            var service = parseResult.GetValue(serviceOption) ?? "sample";
            var http3Http = parseResult.GetValue(enableHttp3HttpOption);
            var http3Grpc = parseResult.GetValue(enableHttp3GrpcOption);
            var includeOutbound = parseResult.GetValue(includeOutboundOption);
            var outboundService = parseResult.GetValue(outboundServiceOption) ?? "ledger";
            return RunConfigScaffoldAsync(output, section, service, http3Http, http3Grpc, includeOutbound, outboundService).GetAwaiter().GetResult();
        });

        return command;
    }

    private static async Task<int> RunConfigScaffoldAsync(
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

    private static Command CreateScriptCommand()
    {
        var command = new Command("script", "Run scripted OmniRelay CLI automation.");

        var runCommand = new Command("run", "Execute a sequence of actions described in a JSON script.");

        var fileOption = new Option<string>("--file")
        {
            Description = "Path to the automation script (JSON).",
            Required = true
        };
        fileOption.Aliases.Add("-f");

        var dryRunOption = new Option<bool>("--dry-run")
        {
            Description = "Emit the planned steps without executing them."
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Keep executing subsequent steps even if one fails."
        };

        runCommand.Add(fileOption);
        runCommand.Add(dryRunOption);
        runCommand.Add(continueOnErrorOption);

        runCommand.SetAction(parseResult =>
        {
            var file = parseResult.GetValue(fileOption) ?? string.Empty;
            var dryRun = parseResult.GetValue(dryRunOption);
            var continueOnError = parseResult.GetValue(continueOnErrorOption);
            return RunAutomationAsync(file, dryRun, continueOnError).GetAwaiter().GetResult();
        });

        command.Add(runCommand);
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
            DefaultValueFactory = _ => DefaultConfigSection
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
            var section = parseResult.GetValue(sectionOption) ?? DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? [];
            return RunConfigValidateAsync(configs, section, overrides).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command CreateIntrospectCommand()
    {
        var command = new Command("introspect", "Fetch dispatcher introspection over HTTP.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Introspection endpoint to query.",
            DefaultValueFactory = _ => DefaultIntrospectionUrl
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format (text|json).",
            DefaultValueFactory = _ => "text"
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 5s, 00:00:05)."
        };

        command.Add(urlOption);
        command.Add(formatOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultIntrospectionUrl;
            var format = parseResult.GetValue(formatOption) ?? "text";
            var timeout = parseResult.GetValue(timeoutOption);
            return RunIntrospectAsync(url, format, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    private static Command CreateRequestCommand()
    {
        var command = new Command("request", "Issue a unary RPC over HTTP or gRPC.");

        var transportOption = new Option<string>("--transport")
        {
            Description = "Transport to use (http|grpc).",
            DefaultValueFactory = _ => "http"
        };

        var serviceOption = new Option<string>("--service")
        {
            Description = "Remote service name.",
            Required = true
        };

        var procedureOption = new Option<string>("--procedure")
        {
            Description = "Remote procedure name.",
            Required = true
        };

        var callerOption = new Option<string?>("--caller")
        {
            Description = "Caller identifier."
        };

        var encodingOption = new Option<string?>("--encoding")
        {
            Description = "Payload encoding (e.g. application/json)."
        };

        var headerOption = new Option<string[]>("--header")
        {
            Description = "Header key=value pairs.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var profileOption = new Option<string[]>("--profile")
        {
            Description = "Apply request presets (e.g. json:default, protobuf:package.Message).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var shardKeyOption = new Option<string?>("--shard-key")
        {
            Description = "Shard key metadata."
        };

        var routingKeyOption = new Option<string?>("--routing-key")
        {
            Description = "Routing key metadata."
        };

        var routingDelegateOption = new Option<string?>("--routing-delegate")
        {
            Description = "Routing delegate metadata."
        };

        var protoFileOption = new Option<string[]>("--proto-file")
        {
            Description = "Path(s) to FileDescriptorSet binaries used for protobuf encoding.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var protoMessageOption = new Option<string?>("--proto-message")
        {
            Description = "Fully-qualified protobuf message name when using protobuf profiles."
        };

        var ttlOption = new Option<string?>("--ttl")
        {
            Description = "Request time-to-live duration."
        };

        var deadlineOption = new Option<string?>("--deadline")
        {
            Description = "Absolute deadline timestamp (ISO-8601)."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Overall call timeout (e.g. 10s)."
        };

        var bodyOption = new Option<string?>("--body")
        {
            Description = "Inline UTF-8 body."
        };

        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Path to a file to use as payload."
        };

        var bodyBase64Option = new Option<string?>("--body-base64")
        {
            Description = "Base64 encoded payload."
        };

        var httpUrlOption = new Option<string?>("--url")
        {
            Description = "HTTP endpoint to invoke."
        };

        var http3Option = new Option<bool>("--http3")
        {
            Description = "Request HTTP/3 when supported by the server."
        };

        var grpcHttp3Option = new Option<bool>("--grpc-http3")
        {
            Description = "Request HTTP/3 when invoking gRPC services."
        };

        var addressOption = new Option<string[]>("--address")
        {
            Description = "gRPC address(es) to dial.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        command.Add(transportOption);
        command.Add(serviceOption);
        command.Add(procedureOption);
        command.Add(callerOption);
        command.Add(encodingOption);
        command.Add(headerOption);
        command.Add(profileOption);
        command.Add(shardKeyOption);
        command.Add(routingKeyOption);
        command.Add(routingDelegateOption);
        command.Add(protoFileOption);
        command.Add(protoMessageOption);
        command.Add(ttlOption);
        command.Add(deadlineOption);
        command.Add(timeoutOption);
        command.Add(bodyOption);
        command.Add(bodyFileOption);
        command.Add(bodyBase64Option);
        command.Add(httpUrlOption);
        command.Add(http3Option);
        command.Add(grpcHttp3Option);
        command.Add(addressOption);

        command.SetAction(parseResult =>
        {
            var transport = parseResult.GetValue(transportOption) ?? "http";
            var service = parseResult.GetValue(serviceOption) ?? string.Empty;
            var procedure = parseResult.GetValue(procedureOption) ?? string.Empty;
            var caller = parseResult.GetValue(callerOption);
            var encoding = parseResult.GetValue(encodingOption);
            var headers = parseResult.GetValue(headerOption) ?? [];
            var profiles = parseResult.GetValue(profileOption) ?? [];
            var shardKey = parseResult.GetValue(shardKeyOption);
            var routingKey = parseResult.GetValue(routingKeyOption);
            var routingDelegate = parseResult.GetValue(routingDelegateOption);
            var protoFiles = parseResult.GetValue(protoFileOption) ?? [];
            var protoMessage = parseResult.GetValue(protoMessageOption);
            var ttl = parseResult.GetValue(ttlOption);
            var deadline = parseResult.GetValue(deadlineOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var body = parseResult.GetValue(bodyOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);
            var bodyBase64 = parseResult.GetValue(bodyBase64Option);
            var httpUrl = parseResult.GetValue(httpUrlOption);
            var http3 = parseResult.GetValue(http3Option);
            var grpcHttp3 = parseResult.GetValue(grpcHttp3Option);
            var addresses = parseResult.GetValue(addressOption) ?? [];

            return RunRequestAsync(
                    transport,
                    service,
                    procedure,
                    caller,
                    encoding,
                    headers,
                    profiles,
                    shardKey,
                    routingKey,
                    routingDelegate,
                    protoFiles,
                    protoMessage,
                    ttl,
                    deadline,
                    timeout,
                    body,
                    bodyFile,
                    bodyBase64,
                    httpUrl,
                    addresses,
                    http3,
                    grpcHttp3)
                .GetAwaiter()
                .GetResult();
        });

        return command;
    }

    private static Command CreateBenchmarkCommand()
    {
        var command = new Command("benchmark", "Run concurrent unary RPC load tests over HTTP or gRPC.");

        var transportOption = new Option<string>("--transport")
        {
            Description = "Transport to use (http|grpc).",
            DefaultValueFactory = _ => "http"
        };

        var serviceOption = new Option<string>("--service")
        {
            Description = "Remote service name.",
            Required = true
        };

        var procedureOption = new Option<string>("--procedure")
        {
            Description = "Remote procedure name.",
            Required = true
        };

        var callerOption = new Option<string?>("--caller")
        {
            Description = "Caller identifier."
        };

        var encodingOption = new Option<string?>("--encoding")
        {
            Description = "Payload encoding (e.g. application/json)."
        };

        var headerOption = new Option<string[]>("--header")
        {
            Description = "Header key=value pairs.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var profileOption = new Option<string[]>("--profile")
        {
            Description = "Apply request presets (e.g. json:default, protobuf:package.Message).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var shardKeyOption = new Option<string?>("--shard-key")
        {
            Description = "Shard key metadata."
        };

        var routingKeyOption = new Option<string?>("--routing-key")
        {
            Description = "Routing key metadata."
        };

        var routingDelegateOption = new Option<string?>("--routing-delegate")
        {
            Description = "Routing delegate metadata."
        };

        var protoFileOption = new Option<string[]>("--proto-file")
        {
            Description = "Path(s) to FileDescriptorSet binaries used for protobuf encoding.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var protoMessageOption = new Option<string?>("--proto-message")
        {
            Description = "Fully-qualified protobuf message name when using protobuf profiles."
        };

        var ttlOption = new Option<string?>("--ttl")
        {
            Description = "Request time-to-live duration."
        };

        var deadlineOption = new Option<string?>("--deadline")
        {
            Description = "Absolute deadline timestamp (ISO-8601)."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Overall call timeout (e.g. 10s)."
        };

        var bodyOption = new Option<string?>("--body")
        {
            Description = "Inline UTF-8 body."
        };

        var bodyFileOption = new Option<string?>("--body-file")
        {
            Description = "Path to a file to use as payload."
        };

        var bodyBase64Option = new Option<string?>("--body-base64")
        {
            Description = "Base64 encoded payload."
        };

        var httpUrlOption = new Option<string?>("--url")
        {
            Description = "HTTP endpoint to invoke."
        };

        var http3Option = new Option<bool>("--http3")
        {
            Description = "Request HTTP/3 when supported by the server."
        };

        var grpcHttp3Option = new Option<bool>("--grpc-http3")
        {
            Description = "Request HTTP/3 when invoking gRPC services."
        };

        var addressOption = new Option<string[]>("--address")
        {
            Description = "gRPC address(es) to dial.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => []
        };

        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Number of concurrent workers issuing requests.",
            DefaultValueFactory = _ => 25
        };
        concurrencyOption.Aliases.Add("-c");

        var requestsOption = new Option<int>("--requests")
        {
            Description = "Total requests to measure (set to 0 to disable the request cap).",
            DefaultValueFactory = _ => 100
        };
        requestsOption.Aliases.Add("-n");

        var durationOption = new Option<string?>("--duration")
        {
            Description = "Measurement duration (e.g. 30s, 1m)."
        };
        durationOption.Aliases.Add("-d");

        var rpsOption = new Option<double?>("--rps")
        {
            Description = "Global requests-per-second limit."
        };

        var warmupOption = new Option<string?>("--warmup")
        {
            Description = "Warmup duration prior to measurement (e.g. 5s)."
        };

        command.Add(transportOption);
        command.Add(serviceOption);
        command.Add(procedureOption);
        command.Add(callerOption);
        command.Add(encodingOption);
        command.Add(headerOption);
        command.Add(profileOption);
        command.Add(shardKeyOption);
        command.Add(routingKeyOption);
        command.Add(routingDelegateOption);
        command.Add(protoFileOption);
        command.Add(protoMessageOption);
        command.Add(ttlOption);
        command.Add(deadlineOption);
        command.Add(timeoutOption);
        command.Add(bodyOption);
        command.Add(bodyFileOption);
        command.Add(bodyBase64Option);
        command.Add(httpUrlOption);
        command.Add(http3Option);
        command.Add(grpcHttp3Option);
        command.Add(addressOption);
        command.Add(concurrencyOption);
        command.Add(requestsOption);
        command.Add(durationOption);
        command.Add(rpsOption);
        command.Add(warmupOption);

        command.SetAction(parseResult =>
        {
            var transportValue = parseResult.GetValue(transportOption) ?? "http";
            var serviceValue = parseResult.GetValue(serviceOption) ?? string.Empty;
            var procedureValue = parseResult.GetValue(procedureOption) ?? string.Empty;
            var callerValue = parseResult.GetValue(callerOption);
            var encodingValue = parseResult.GetValue(encodingOption);
            var headersValue = parseResult.GetValue(headerOption) ?? [];
            var profilesValue = parseResult.GetValue(profileOption) ?? [];
            var shardKeyValue = parseResult.GetValue(shardKeyOption);
            var routingKeyValue = parseResult.GetValue(routingKeyOption);
            var routingDelegateValue = parseResult.GetValue(routingDelegateOption);
            var protoFilesValue = parseResult.GetValue(protoFileOption) ?? [];
            var protoMessageValue = parseResult.GetValue(protoMessageOption);
            var ttlValue = parseResult.GetValue(ttlOption);
            var deadlineValue = parseResult.GetValue(deadlineOption);
            var timeoutValue = parseResult.GetValue(timeoutOption);
            var bodyValue = parseResult.GetValue(bodyOption);
            var bodyFileValue = parseResult.GetValue(bodyFileOption);
            var bodyBase64Value = parseResult.GetValue(bodyBase64Option);
            var httpUrlValue = parseResult.GetValue(httpUrlOption);
            var http3Value = parseResult.GetValue(http3Option);
            var grpcHttp3Value = parseResult.GetValue(grpcHttp3Option);
            var addressesValue = parseResult.GetValue(addressOption) ?? [];
            var concurrencyValue = parseResult.GetValue(concurrencyOption);
            var requestsValue = parseResult.GetValue(requestsOption);
            var durationValue = parseResult.GetValue(durationOption);
            var rpsValue = parseResult.GetValue(rpsOption);
            var warmupValue = parseResult.GetValue(warmupOption);

            return RunBenchmarkAsync(
                    transportValue,
                    serviceValue,
                    procedureValue,
                    callerValue,
                    encodingValue,
                    headersValue,
                    profilesValue,
                    shardKeyValue,
                    routingKeyValue,
                    routingDelegateValue,
                    protoFilesValue,
                    protoMessageValue,
                    ttlValue,
                    deadlineValue,
                    timeoutValue,
                    bodyValue,
                    bodyFileValue,
                    bodyBase64Value,
                    httpUrlValue,
                    addressesValue,
                    http3Value,
                    grpcHttp3Value,
                    concurrencyValue,
                    requestsValue,
                    durationValue,
                    rpsValue,
                    warmupValue)
                .GetAwaiter()
                .GetResult();
        });

        return command;
    }

    private static async Task<int> RunConfigValidateAsync(string[] configPaths, string section, string[] setOverrides)
    {
        if (!TryBuildConfiguration(configPaths, setOverrides, out var configuration, out var errorMessage))
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
            services.AddOmniRelayDispatcher(configuration.GetSection(section ?? DefaultConfigSection));
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

    private static async Task<int> RunServeAsync(string[] configPaths, string section, string[] setOverrides, string? readyFile, string? shutdownAfterOption)
    {
        if (!TryBuildConfiguration(configPaths, setOverrides, out var configuration, out var errorMessage))
        {
            await Console.Error.WriteLineAsync(errorMessage ?? "Failed to load configuration.").ConfigureAwait(false);
            return 1;
        }

        TimeSpan? shutdownAfter = null;
        if (!string.IsNullOrWhiteSpace(shutdownAfterOption))
        {
            if (!TryParseDuration(shutdownAfterOption!, out var parsed))
            {
                await Console.Error.WriteLineAsync($"Could not parse --shutdown-after value '{shutdownAfterOption}'.").ConfigureAwait(false);
                return 1;
            }

            if (parsed <= TimeSpan.Zero)
            {
                await Console.Error.WriteLineAsync("--shutdown-after duration must be greater than zero.").ConfigureAwait(false);
                return 1;
            }

            shutdownAfter = parsed;
        }

        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddLogging(static logging => logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        }));

        try
        {
            builder.Services.AddOmniRelayDispatcher(builder.Configuration.GetSection(section ?? DefaultConfigSection));
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to configure dispatcher: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        using var host = builder.Build();
        var shutdownSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConsoleCancelEventHandler? cancelHandler = null;

        void RequestShutdown()
        {
            shutdownSignal.TrySetResult(true);
        }

        cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            RequestShutdown();
        };

        Console.CancelKeyPress += cancelHandler;

        if (shutdownAfter.HasValue)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(shutdownAfter.Value).ConfigureAwait(false);
                    RequestShutdown();
                }
                catch
                {
                    RequestShutdown();
                }
            });
        }

        try
        {
            await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            var dispatcher = host.Services.GetRequiredService<Dispatcher.Dispatcher>();
            Console.WriteLine($"OmniRelay dispatcher '{dispatcher.ServiceName}' started.");

            if (!string.IsNullOrWhiteSpace(readyFile))
            {
                TryWriteReadyFile(readyFile!);
            }

            Console.WriteLine(shutdownAfter.HasValue
                ? $"Shutting down automatically after {shutdownAfter.Value:c}."
                : "Press Ctrl+C to stop.");

            await shutdownSignal.Task.ConfigureAwait(false);
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to run dispatcher: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    [RequiresDynamicCode("Calls System.Text.Json.Serialization.JsonStringEnumConverter.JsonStringEnumConverter(JsonNamingPolicy, Boolean)")]
    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.DeserializeAsync<TValue>(Stream, JsonSerializerOptions, CancellationToken)")]
    private static async Task<int> RunIntrospectAsync(string url, string format, string? timeoutOption)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "text" : format.ToLowerInvariant();
        var timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'. Use standard TimeSpan formats or suffixes like 5s/1m.").ConfigureAwait(false);
            return 1;
        }

        using var httpClient = new HttpClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Introspection request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                var options = CreateJsonOptions(configure: static options =>
                {
                    options.PropertyNameCaseInsensitive = true;
                });
                options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

                var snapshot = await JsonSerializer.DeserializeAsync<DispatcherIntrospection>(stream, options, cts.Token).ConfigureAwait(false);
                if (snapshot is null)
                {
                    await Console.Error.WriteLineAsync("Introspection response was empty.").ConfigureAwait(false);
                    return 1;
                }

                if (normalizedFormat is "json" or "raw")
                {
                    var outputOptions = CreateJsonOptions(configure: static options =>
                    {
                        options.WriteIndented = true;
                    });
                    outputOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                    var json = JsonSerializer.Serialize(snapshot, outputOptions);
                    Console.WriteLine(json);
                }
                else if (normalizedFormat is "text" or "summary")
                {
                    PrintIntrospectionSummary(snapshot);
                }
                else
                {
                    await Console.Error.WriteLineAsync($"Unknown format '{format}'. Expected 'text' or 'json'.").ConfigureAwait(false);
                    return 1;
                }
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Introspection request timed out.").ConfigureAwait(false);
            return 2;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Introspection failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    private static bool TryBuildConfiguration(string[] configPaths, string[] setOverrides, out IConfigurationRoot configuration, out string? errorMessage)
    {
        configuration = null!;
        errorMessage = null;

        if (configPaths.Length == 0)
        {
            errorMessage = "No configuration files supplied. Use --config <path> (repeat for layering).";
            return false;
        }

        var builder = new ConfigurationBuilder();
        foreach (var path in configPaths)
        {
            if (!File.Exists(path))
            {
                errorMessage = $"Configuration file '{path}' does not exist.";
                return false;
            }

            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jsn", StringComparison.OrdinalIgnoreCase))
            {
                builder.AddJsonFile(path, optional: false, reloadOnChange: false);
            }
            else
            {
                errorMessage = $"Unsupported configuration format '{extension}'. Only JSON is currently supported.";
                return false;
            }
        }

        if (setOverrides.Length > 0)
        {
            var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in setOverrides)
            {
                if (!TrySplitKeyValue(entry, out var key, out var value))
                {
                    errorMessage = $"Could not parse --set value '{entry}'. Expected KEY=VALUE.";
                    return false;
                }

                overlay[key] = value;
            }

            builder.AddInMemoryCollection(overlay!);
        }

        try
        {
            configuration = builder.Build();
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = $"Failed to build configuration: {ex.Message}";
            return false;
        }
    }

    private static void TryWriteReadyFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write ready file '{path}': {ex.Message}");
        }
    }

    private static async Task<int> RunRequestAsync(
        string transport,
        string service,
        string procedure,
        string? caller,
        string? encoding,
        string[] headerValues,
        string[] profileValues,
        string? shardKey,
        string? routingKey,
        string? routingDelegate,
        string[] protoFiles,
        string? protoMessage,
        string? ttlOption,
        string? deadlineOption,
        string? timeoutOption,
        string? body,
        string? bodyFile,
        string? bodyBase64,
        string? httpUrl,
        string[] addresses,
        bool enableHttp3,
        bool enableGrpcHttp3)
    {
        if (!TryBuildRequestInvocation(
                transport,
                service,
                procedure,
                caller,
                encoding,
                headerValues,
                profileValues,
                shardKey,
                routingKey,
                routingDelegate,
                protoFiles,
                protoMessage,
                ttlOption,
                deadlineOption,
                timeoutOption,
                body,
                bodyFile,
                bodyBase64,
                httpUrl,
                addresses ?? [],
                enableHttp3,
                enableGrpcHttp3,
                out var invocation,
                out var error))
        {
            await Console.Error.WriteLineAsync(error ?? "Failed to prepare request.").ConfigureAwait(false);
            return 1;
        }

        using var cts = invocation.Timeout.HasValue && invocation.Timeout.Value > TimeSpan.Zero
            ? new CancellationTokenSource(invocation.Timeout.Value)
            : new CancellationTokenSource(TimeSpan.FromSeconds(30));

        return invocation.Transport switch
        {
            "http" => await ExecuteHttpRequestAsync(invocation.HttpUrl, invocation.Request, invocation.HttpClientRuntime, cts.Token).ConfigureAwait(false),
            "grpc" => await ExecuteGrpcRequestAsync(invocation.Addresses, invocation.Request.Meta.Service, invocation.Request, invocation.GrpcClientRuntime, cts.Token).ConfigureAwait(false),
            _ => 1
        };
    }

    private static bool TryBuildRequestInvocation(
        string transport,
        string service,
        string procedure,
        string? caller,
        string? encoding,
        string[] headerValues,
        string[] profileValues,
        string? shardKey,
        string? routingKey,
        string? routingDelegate,
        string[] protoFiles,
        string? protoMessage,
        string? ttlOption,
        string? deadlineOption,
        string? timeoutOption,
        string? body,
        string? bodyFile,
        string? bodyBase64,
        string? httpUrl,
        string[] addresses,
        bool enableHttp3,
        bool enableGrpcHttp3,
        out RequestInvocation invocation,
        out string? error)
    {
        invocation = null!;
        error = null;

        var normalizedTransport = string.IsNullOrWhiteSpace(transport) ? "http" : transport.ToLowerInvariant();
        if (normalizedTransport is not ("http" or "grpc"))
        {
            error = $"Unsupported transport '{transport}'. Use 'http' or 'grpc'.";
            return false;
        }

        var headers = headerValues ?? [];
        var profiles = profileValues ?? [];
        var protoDescriptorFiles = protoFiles ?? [];
        var normalizedAddresses = addresses is { Length: > 0 }
            ? addresses.Where(static address => !string.IsNullOrWhiteSpace(address)).ToArray()
            : [];

        TimeSpan? ttl = null;
        if (!string.IsNullOrWhiteSpace(ttlOption))
        {
            if (!TryParseDuration(ttlOption!, out var parsedTtl))
            {
                error = $"Could not parse TTL '{ttlOption}'. Use formats like '00:00:05' or suffixes (e.g. 5s, 1m).";
                return false;
            }

            ttl = parsedTtl;
        }

        DateTimeOffset? deadline = null;
        if (!string.IsNullOrWhiteSpace(deadlineOption))
        {
            if (!DateTimeOffset.TryParse(deadlineOption, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
            {
                error = $"Could not parse deadline '{deadlineOption}'. Provide an ISO-8601 timestamp.";
                return false;
            }

            deadline = parsedDeadline;
        }

        TimeSpan? timeout = null;
        if (!string.IsNullOrWhiteSpace(timeoutOption))
        {
            if (!TryParseDuration(timeoutOption!, out var parsedTimeout))
            {
                error = $"Could not parse timeout '{timeoutOption}'.";
                return false;
            }

            timeout = parsedTimeout;
        }

        if (!TryParseHeaders(headers, out var headerPairs, out var headerError))
        {
            error = headerError ?? "Failed to parse headers.";
            return false;
        }

        var resolvedEncoding = encoding;
        if (!TryPrepareProfiles(
                normalizedTransport,
                profiles,
                protoDescriptorFiles,
                protoMessage,
                headerPairs,
                ref resolvedEncoding,
                out var profileState,
                out var profileError))
        {
            error = profileError ?? "Failed to process profiles.";
            return false;
        }

        if (!TryResolvePayload(body, bodyFile, bodyBase64, out var payload, out var payloadSource, out var payloadError))
        {
            error = payloadError ?? "Failed to resolve payload.";
            return false;
        }

        if (!TryFinalizeProfiles(
                profileState,
                body,
                bodyFile,
                payloadSource,
                ref payload,
                out var finalizeError))
        {
            error = finalizeError ?? "Failed to apply profile transforms.";
            return false;
        }

        if (enableHttp3 && normalizedTransport == "http")
        {
            if (string.IsNullOrWhiteSpace(httpUrl) || !Uri.TryCreate(httpUrl, UriKind.Absolute, out var httpUri) || !httpUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                error = "HTTP/3 requires an HTTPS --url.";
                return false;
            }
        }

        if (enableGrpcHttp3 && normalizedTransport == "grpc")
        {
            foreach (var address in normalizedAddresses)
            {
                if (Uri.TryCreate(address, UriKind.Absolute, out var grpcUri) &&
                    grpcUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                error = $"HTTP/3 requires HTTPS gRPC addresses. Address '{address}' is not HTTPS.";
                return false;
            }
        }

        var meta = new RequestMeta(
            service: service,
            procedure: procedure,
            caller: caller,
            encoding: resolvedEncoding,
            transport: normalizedTransport,
            shardKey: shardKey,
            routingKey: routingKey,
            routingDelegate: routingDelegate,
            timeToLive: ttl,
            deadline: deadline,
            headers: headerPairs);

        HttpClientRuntimeOptions? httpRuntime = null;
        GrpcClientRuntimeOptions? grpcRuntime = null;

        if (enableHttp3 && normalizedTransport == "http")
        {
            httpRuntime = new HttpClientRuntimeOptions
            {
                EnableHttp3 = true
            };
        }

        if (enableGrpcHttp3 && normalizedTransport == "grpc")
        {
            grpcRuntime = new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true
            };
        }

        invocation = new RequestInvocation(
            normalizedTransport,
            new Request<ReadOnlyMemory<byte>>(meta, payload),
            timeout,
            httpUrl,
            normalizedAddresses,
            httpRuntime,
            grpcRuntime);

        return true;
    }

    private static async Task<int> RunBenchmarkAsync(
        string transport,
        string service,
        string procedure,
        string? caller,
        string? encoding,
        string[] headerValues,
        string[] profileValues,
        string? shardKey,
        string? routingKey,
        string? routingDelegate,
        string[] protoFiles,
        string? protoMessage,
        string? ttlOption,
        string? deadlineOption,
        string? timeoutOption,
        string? body,
        string? bodyFile,
        string? bodyBase64,
        string? httpUrl,
        string[] addresses,
        bool enableHttp3,
        bool enableGrpcHttp3,
        int concurrency,
        int requestLimit,
        string? durationOption,
        double? rateLimit,
        string? warmupOption)
    {
        if (!TryBuildRequestInvocation(
                transport,
                service,
                procedure,
                caller,
                encoding,
                headerValues,
                profileValues,
                shardKey,
                routingKey,
                routingDelegate,
                protoFiles,
                protoMessage,
                ttlOption,
                deadlineOption,
                timeoutOption,
                body,
                bodyFile,
                bodyBase64,
                httpUrl,
                addresses ?? [],
                enableHttp3,
                enableGrpcHttp3,
                out var invocation,
                out var buildError))
        {
            await Console.Error.WriteLineAsync(buildError ?? "Failed to prepare request.").ConfigureAwait(false);
            return 1;
        }

        if (concurrency <= 0)
        {
            await Console.Error.WriteLineAsync("Concurrency must be greater than zero.").ConfigureAwait(false);
            return 1;
        }

        long? maxRequests = requestLimit > 0 ? requestLimit : null;

        TimeSpan? duration = null;
        if (!string.IsNullOrWhiteSpace(durationOption))
        {
            if (!TryParseDuration(durationOption!, out var parsedDuration))
            {
                await Console.Error.WriteLineAsync($"Could not parse duration '{durationOption}'.").ConfigureAwait(false);
                return 1;
            }

            if (parsedDuration <= TimeSpan.Zero)
            {
                await Console.Error.WriteLineAsync("Duration must be greater than zero.").ConfigureAwait(false);
                return 1;
            }

            duration = parsedDuration;
        }

        TimeSpan? warmup = null;
        if (!string.IsNullOrWhiteSpace(warmupOption))
        {
            if (!TryParseDuration(warmupOption!, out var parsedWarmup))
            {
                await Console.Error.WriteLineAsync($"Could not parse warmup '{warmupOption}'.").ConfigureAwait(false);
                return 1;
            }

            if (parsedWarmup <= TimeSpan.Zero)
            {
                await Console.Error.WriteLineAsync("Warmup duration must be greater than zero when specified.").ConfigureAwait(false);
                return 1;
            }

            warmup = parsedWarmup;
        }

        if (maxRequests is null && duration is null)
        {
            await Console.Error.WriteLineAsync("Specify a positive --requests value or a --duration for benchmarking.").ConfigureAwait(false);
            return 1;
        }

        if (rateLimit.HasValue && rateLimit.Value <= 0)
        {
            await Console.Error.WriteLineAsync("Rate limit must be greater than zero when specified.").ConfigureAwait(false);
            return 1;
        }

        var perRequestTimeout = invocation.Timeout ?? TimeSpan.FromSeconds(30);

        var options = new BenchmarkRunner.BenchmarkExecutionOptions(
            concurrency,
            maxRequests,
            duration,
            rateLimit,
            warmup,
            perRequestTimeout);

        BenchmarkRunner.BenchmarkSummary summary;
        try
        {
            summary = await BenchmarkRunner.RunAsync(invocation, options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Benchmark failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        PrintBenchmarkSummary(invocation, options, summary);
        return summary.Successes > 0 ? 0 : 1;
    }

    private static async Task<int> ExecuteHttpRequestAsync(string? url, Request<ReadOnlyMemory<byte>> request, HttpClientRuntimeOptions? runtimeOptions, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            await Console.Error.WriteLineAsync("HTTP transport requires --url specifying the request endpoint.").ConfigureAwait(false);
            return 1;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
        {
            await Console.Error.WriteLineAsync($"Invalid URL '{url}'.").ConfigureAwait(false);
            return 1;
        }

        using var httpClient = new HttpClient();
        var outbound = new HttpOutbound(httpClient, requestUri, runtimeOptions: runtimeOptions);

        try
        {
            await outbound.StartAsync(cancellationToken).ConfigureAwait(false);
            var unaryOutbound = (IUnaryOutbound)outbound;
            var result = await unaryOutbound.CallAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                PrintResponse(result.Value);
                return 0;
            }

            PrintError(result.Error!, "http");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"HTTP call failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteGrpcRequestAsync(string[] addresses, string remoteService, Request<ReadOnlyMemory<byte>> request, GrpcClientRuntimeOptions? runtimeOptions, CancellationToken cancellationToken)
    {
        if (addresses.Length == 0)
        {
            await Console.Error.WriteLineAsync("gRPC transport requires at least one --address option (e.g. --address http://127.0.0.1:9090).").ConfigureAwait(false);
            return 1;
        }

        var uris = new List<Uri>(addresses.Length);
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                await Console.Error.WriteLineAsync($"Invalid gRPC address '{address}'.").ConfigureAwait(false);
                return 1;
            }

            uris.Add(uri);
        }

        var outbound = new GrpcOutbound(uris, remoteService, clientRuntimeOptions: runtimeOptions);

        try
        {
            await outbound.StartAsync(cancellationToken).ConfigureAwait(false);
            var result = await outbound.CallAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                PrintResponse(result.Value);
                return 0;
            }

            PrintError(result.Error!, "grpc");
            return 1;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"gRPC call failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.DeserializeAsync<TValue>(Stream, JsonSerializerOptions, CancellationToken)")]
    private static async Task<int> RunAutomationAsync(string scriptPath, bool dryRun, bool continueOnError)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            await Console.Error.WriteLineAsync("Script path was empty.").ConfigureAwait(false);
            return 1;
        }

        if (!File.Exists(scriptPath))
        {
            await Console.Error.WriteLineAsync($"Script file '{scriptPath}' does not exist.").ConfigureAwait(false);
            return 1;
        }

        AutomationScript? script;
        try
        {
            await using (File.OpenRead(scriptPath).AsAsyncDisposable(out var stream))
            {
                var options = CreateJsonOptions(configure: static options =>
                {
                    options.PropertyNameCaseInsensitive = true;
                    options.AllowTrailingCommas = true;
                });
                script = await JsonSerializer.DeserializeAsync<AutomationScript>(stream, options).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to parse script '{scriptPath}': {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        if (script?.Steps is null || script.Steps.Length == 0)
        {
            await Console.Error.WriteLineAsync($"Script '{scriptPath}' does not contain any steps.").ConfigureAwait(false);
            return 1;
        }

        Console.WriteLine($"Loaded script '{scriptPath}' with {script.Steps.Length} step(s).");
        var exitCode = 0;

        for (var index = 0; index < script.Steps.Length; index++)
        {
            var step = script.Steps[index];
            var typeLabel = string.IsNullOrWhiteSpace(step.Type) ? "(unspecified)" : step.Type;
            var description = string.IsNullOrWhiteSpace(AutomationStep.Description) ? string.Empty : $" - {AutomationStep.Description}";
            Console.WriteLine($"[{index + 1}/{script.Steps.Length}] {typeLabel}{description}");

            var normalizedType = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            switch (normalizedType)
            {
                case "request":
                    if (string.IsNullOrWhiteSpace(AutomationStep.Service) || string.IsNullOrWhiteSpace(AutomationStep.Procedure))
                    {
                        await Console.Error.WriteLineAsync("  Request step is missing 'service' or 'procedure'.").ConfigureAwait(false);
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    var headerPairs = AutomationStep.Headers?.Select(static kvp => $"{kvp.Key}={kvp.Value}").ToArray() ?? [];
                    var profiles = AutomationStep.Profiles ?? [];
                    var addresses = AutomationStep.Addresses ?? [];
                    if (addresses.Length == 0 && !string.IsNullOrWhiteSpace(AutomationStep.Address))
                    {
                        addresses = [AutomationStep.Address!];
                    }

                    var targetSummary = !string.IsNullOrWhiteSpace(AutomationStep.Url)
                        ? AutomationStep.Url
                        : (addresses.Length > 0 ? string.Join(", ", addresses) : "(default transport settings)");
                    Console.WriteLine($"  -> {AutomationStep.Transport ?? "http"} {AutomationStep.Service}/{AutomationStep.Procedure} @ {targetSummary}");

                    if (dryRun)
                    {
                        Console.WriteLine("  dry-run: skipping request execution.");
                        continue;
                    }

                    var requestResult = await RunRequestAsync(
                        AutomationStep.Transport ?? "http",
                        AutomationStep.Service,
                        AutomationStep.Procedure,
                        AutomationStep.Caller,
                        AutomationStep.Encoding,
                        headerPairs,
                        profiles,
                        AutomationStep.ShardKey,
                        AutomationStep.RoutingKey,
                        AutomationStep.RoutingDelegate,
                        AutomationStep.ProtoFiles ?? [],
                        AutomationStep.ProtoMessage,
                        AutomationStep.Ttl,
                        AutomationStep.Deadline,
                        AutomationStep.Timeout,
                        AutomationStep.Body,
                        AutomationStep.BodyFile,
                        AutomationStep.BodyBase64,
                        AutomationStep.Url,
                        addresses,
                        enableHttp3: false,
                        enableGrpcHttp3: false).ConfigureAwait(false);

                    if (requestResult != 0)
                    {
                        exitCode = exitCode == 0 ? requestResult : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                    }
                    break;

                case "introspect":
                    var targetUrl = string.IsNullOrWhiteSpace(AutomationStep.Url) ? DefaultIntrospectionUrl : AutomationStep.Url!;
                    Console.WriteLine($"  -> GET {targetUrl} (format={AutomationStep.Format ?? "text"})");

                    if (dryRun)
                    {
                        Console.WriteLine("  dry-run: skipping introspection call.");
                        continue;
                    }

                    var introspectResult = await RunIntrospectAsync(targetUrl, AutomationStep.Format ?? "text", AutomationStep.Timeout).ConfigureAwait(false);
                    if (introspectResult != 0)
                    {
                        exitCode = exitCode == 0 ? introspectResult : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                    }
                    break;

                case "delay":
                case "sleep":
                case "wait":
                    var delayValue = AutomationStep.Duration ?? AutomationStep.Delay;
                    if (string.IsNullOrWhiteSpace(delayValue))
                    {
                        await Console.Error.WriteLineAsync("  Delay step requires a 'duration' or 'delay' value.").ConfigureAwait(false);
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    var delayText = delayValue!;
                    if (!TryParseDuration(delayText, out var delay))
                    {
                        await Console.Error.WriteLineAsync($"  Could not parse delay '{delayValue}'.").ConfigureAwait(false);
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    Console.WriteLine($"  ... waiting for {delay}");
                    if (!dryRun)
                    {
                        try
                        {
                            await Task.Delay(delay).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            await Console.Error.WriteLineAsync($"  Delay interrupted: {ex.Message}").ConfigureAwait(false);
                            exitCode = exitCode == 0 ? 1 : exitCode;
                            if (!continueOnError)
                            {
                                return exitCode;
                            }
                        }
                    }
                    break;

                default:
                    await Console.Error.WriteLineAsync($"  Unknown script step type '{step.Type}'.").ConfigureAwait(false);
                    exitCode = exitCode == 0 ? 1 : exitCode;
                    if (!continueOnError)
                    {
                        return exitCode;
                    }
                    break;
            }
        }

        return exitCode;
    }

    private static void PrintBenchmarkSummary(
        RequestInvocation invocation,
        BenchmarkRunner.BenchmarkExecutionOptions options,
        BenchmarkRunner.BenchmarkSummary summary)
    {
        Console.WriteLine("Benchmark complete.");
        Console.WriteLine($"Transport: {invocation.Transport}");
        Console.WriteLine($"Service: {invocation.Request.Meta.Service}");
        Console.WriteLine($"Procedure: {invocation.Request.Meta.Procedure ?? "(none)"}");
        Console.WriteLine($"Concurrency: {options.Concurrency}");

        if (options.MaxRequests.HasValue)
        {
            Console.WriteLine($"Target requests: {options.MaxRequests.Value.ToString("N0", CultureInfo.InvariantCulture)}");
        }

        if (options.Duration.HasValue)
        {
            Console.WriteLine($"Target duration: {options.Duration.Value.ToString("c", CultureInfo.InvariantCulture)}");
        }

        if (options.RateLimitPerSecond.HasValue)
        {
            Console.WriteLine($"Rate limit: {options.RateLimitPerSecond.Value.ToString("F2", CultureInfo.InvariantCulture)} req/s");
        }

        if (options.WarmupDuration is { TotalMilliseconds: > 0 } warmup)
        {
            Console.WriteLine($"Warmup: {warmup.ToString("c", CultureInfo.InvariantCulture)}");
        }

        Console.WriteLine($"Measured requests: {summary.Attempts.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  Success: {summary.Successes.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"  Failures: {summary.Failures.ToString("N0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Elapsed: {summary.Elapsed.ToString("c", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"Throughput: {summary.RequestsPerSecond.ToString("F2", CultureInfo.InvariantCulture)} req/s");

        if (summary.Latency is { } latency)
        {
            Console.WriteLine("Latency (ms):");
            Console.WriteLine($"  Min : {latency.Min.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  P50 : {latency.P50.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  P90 : {latency.P90.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  P95 : {latency.P95.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  P99 : {latency.P99.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  Max : {latency.Max.ToString("F2", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"  Mean: {latency.Mean.ToString("F2", CultureInfo.InvariantCulture)}");
        }
        else
        {
            Console.WriteLine("Latency (ms): no successful samples.");
        }

        if (summary.Errors.Count <= 0)
        {
            return;
        }

        Console.WriteLine("Top errors:");
        foreach (var entry in summary.Errors
                     .OrderByDescending(static kvp => kvp.Value)
                     .ThenBy(static kvp => kvp.Key, StringComparer.Ordinal)
                     .Take(5))
        {
            Console.WriteLine($"  {entry.Value.ToString("N0", CultureInfo.InvariantCulture)} - {entry.Key}");
        }
    }

    private static void PrintResponse(Response<ReadOnlyMemory<byte>> response)
    {
        Console.WriteLine("Request succeeded.");
        Console.WriteLine($"Transport: {response.Meta.Transport ?? "unknown"}");
        Console.WriteLine($"Encoding: {response.Meta.Encoding ?? "(none)"}");

        if (response.Meta.Headers is { Count: > 0 } headers)
        {
            Console.WriteLine("Headers:");
            foreach (var header in headers.OrderBy(static h => h.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {header.Key}: {header.Value}");
            }
        }

        if (response.Body.IsEmpty)
        {
            Console.WriteLine("Body: <empty>");
            return;
        }

        if (TryDecodeUtf8(response.Body.Span, out var text))
        {
            Console.WriteLine("Body (utf-8):");
            Console.WriteLine(text);
        }
        else
        {
            Console.WriteLine($"Body ({response.Body.Length} bytes, base64):");
            Console.WriteLine(Convert.ToBase64String(response.Body.Span));
        }
    }

    private static void PrintError(Error error, string transport)
    {
        var polymerException = OmniRelayErrors.FromError(error, transport);
        Console.Error.WriteLine($"Request failed with status {polymerException.StatusCode}: {polymerException.Message}");

        if (polymerException.Error.Metadata is { Count: > 0 } metadata)
        {
            Console.Error.WriteLine("Metadata:");
            foreach (var kvp in metadata.OrderBy(static m => m.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        if (polymerException.InnerException is { } inner)
        {
            Console.Error.WriteLine($"Inner exception: {inner.GetType().Name}: {inner.Message}");
        }
    }

    private static void PrintIntrospectionSummary(DispatcherIntrospection snapshot)
    {
        Console.WriteLine($"Service: {snapshot.Service}");
        Console.WriteLine($"Status: {snapshot.Status}");
        Console.WriteLine();

        Console.WriteLine("Procedures:");
        PrintProcedureGroup("Unary", snapshot.Procedures.Unary.Select(static p => p.Name));
        PrintProcedureGroup("Oneway", snapshot.Procedures.Oneway.Select(static p => p.Name));
        PrintProcedureGroup("Stream", snapshot.Procedures.Stream.Select(static p => p.Name));
        PrintProcedureGroup("ClientStream", snapshot.Procedures.ClientStream.Select(static p => p.Name));
        PrintProcedureGroup("Duplex", snapshot.Procedures.Duplex.Select(static p => p.Name));
        Console.WriteLine();

        if (snapshot.Components.Length > 0)
        {
            Console.WriteLine("Lifecycle components:");
            foreach (var component in snapshot.Components)
            {
                Console.WriteLine($"  - {component.Name} ({component.ComponentType})");
            }
            Console.WriteLine();
        }

        if (snapshot.Outbounds.Length > 0)
        {
            Console.WriteLine("Outbounds:");
            foreach (var outbound in snapshot.Outbounds)
            {
                Console.WriteLine($"  • {outbound.Service}");
                PrintOutboundGroup("    Unary", outbound.Unary);
                PrintOutboundGroup("    Oneway", outbound.Oneway);
                PrintOutboundGroup("    Stream", outbound.Stream);
                PrintOutboundGroup("    ClientStream", outbound.ClientStream);
                PrintOutboundGroup("    Duplex", outbound.Duplex);
            }
            Console.WriteLine();
        }

        Console.WriteLine("Middleware (Inbound → Outbound):");
        PrintMiddlewareLine("Unary", snapshot.Middleware.InboundUnary, snapshot.Middleware.OutboundUnary);
        PrintMiddlewareLine("Oneway", snapshot.Middleware.InboundOneway, snapshot.Middleware.OutboundOneway);
        PrintMiddlewareLine("Stream", snapshot.Middleware.InboundStream, snapshot.Middleware.OutboundStream);
        PrintMiddlewareLine("ClientStream", snapshot.Middleware.InboundClientStream, snapshot.Middleware.OutboundClientStream);
        PrintMiddlewareLine("Duplex", snapshot.Middleware.InboundDuplex, snapshot.Middleware.OutboundDuplex);
    }

    private static void PrintProcedureGroup(string label, IEnumerable<string> names)
    {
        var nameList = names.ToList();
        Console.WriteLine($"  {label}: {nameList.Count}");
        if (nameList.Count == 0)
        {
            return;
        }

        var preview = nameList.Take(5).ToList();
        Console.WriteLine($"    {string.Join(", ", preview)}{(nameList.Count > preview.Count ? $" (+{nameList.Count - preview.Count} more)" : string.Empty)}");
    }

    private static void PrintOutboundGroup(string label, IReadOnlyList<OutboundBindingDescriptor> bindings)
    {
        if (bindings.Count == 0)
        {
            return;
        }

        Console.WriteLine(label);
        foreach (var binding in bindings)
        {
            Console.WriteLine($"      - {binding.Key}: {binding.ImplementationType}");
        }
    }

    private static void PrintMiddlewareLine(string label, IReadOnlyList<string> inbound, IReadOnlyList<string> outbound) => Console.WriteLine($"  {label}: inbound[{inbound.Count}] outbound[{outbound.Count}]");

    private static bool TryParseHeaders(IEnumerable<string> values, out List<KeyValuePair<string, string>> headers, out string? error)
    {
        headers = [];
        error = null;
        foreach (var value in values)
        {
            if (!TrySplitKeyValue(value, out var key, out var parsedValue))
            {
                error = $"Could not parse header '{value}'. Expected KEY=VALUE.";
                headers = [];
                return false;
            }

            headers.Add(new KeyValuePair<string, string>(key, parsedValue));
        }

        return true;
    }

    private static bool TryPrepareProfiles(
        string transport,
        string[] profiles,
        string[] protoFiles,
        string? protoMessageOption,
        List<KeyValuePair<string, string>> headers,
        ref string? encoding,
        out ProfileProcessingState state,
        out string? error)
    {
        state = new ProfileProcessingState();
        error = null;

        if (profiles.Length == 0)
        {
            return true;
        }

        foreach (var profile in profiles)
        {
            if (string.IsNullOrWhiteSpace(profile))
            {
                continue;
            }

            var trimmed = profile.Trim();
            var separatorIndex = trimmed.IndexOf(':');
            var kind = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
            var qualifier = separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : string.Empty;
            var normalizedKind = kind.ToLowerInvariant();

            switch (normalizedKind)
            {
                case "json":
                    ApplyJsonProfile(qualifier, headers, ref encoding, state);
                    break;

                case "protobuf":
                case "proto":
                    if (ProfileProcessingState.Proto is not null)
                    {
                        error = "Multiple protobuf profiles specified. Provide a single protobuf profile per request.";
                        return false;
                    }

                    var messageName = string.IsNullOrWhiteSpace(qualifier) ? protoMessageOption : qualifier;
                    if (string.IsNullOrWhiteSpace(messageName))
                    {
                        error = "Protobuf profiles require a message name (e.g. protobuf:package.Message or --proto-message).";
                        return false;
                    }

                    if (protoFiles.Length == 0)
                    {
                        error = "Protobuf profiles require at least one --proto-file pointing to a FileDescriptorSet.";
                        return false;
                    }

                    ProfileProcessingState.Proto = new ProtoProcessingState(protoFiles, messageName.Trim());
                    encoding ??= transport == "grpc" ? "application/grpc" : "application/x-protobuf";

                    if (transport == "http")
                    {
                        EnsureHeader(headers, "Content-Type", encoding);
                    }

                    break;

                default:
                    error = $"Unknown profile '{profile}'.";
                    return false;
            }
        }

        if (encoding is not null)
        {
            EnsureHeader(headers, "Rpc-Encoding", encoding);
        }

        return true;
    }

    private static bool TryFinalizeProfiles(
        ProfileProcessingState state,
        string? inlineBody,
        string? bodyFile,
        PayloadSource payloadSource,
        ref ReadOnlyMemory<byte> payload,
        out string? error)
    {
        error = null;

        if (ProfileProcessingState.Proto is not null)
        {
            if (!TryEncodeProtobufPayload(ProfileProcessingState.Proto, inlineBody, bodyFile, payloadSource, ref payload, out error))
            {
                return false;
            }
        }

        if (ProfileProcessingState.PrettyPrintJson)
        {
            FormatJsonPayload(inlineBody, bodyFile, payloadSource, ref payload);
        }

        return true;
    }

    private static void ApplyJsonProfile(
        string qualifier,
        List<KeyValuePair<string, string>> headers,
        ref string? encoding,
        ProfileProcessingState state)
    {
        var normalized = string.IsNullOrWhiteSpace(qualifier) ? "default" : qualifier.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "default":
                break;
            case "pretty":
                ProfileProcessingState.PrettyPrintJson = true;
                break;
            default:
                Console.Error.WriteLine($"Warning: unknown json profile '{qualifier}'. Falling back to default settings.");
                break;
        }

        encoding ??= "application/json";
        EnsureHeader(headers, "Content-Type", encoding);
        EnsureHeader(headers, "Accept", "application/json");
    }

    [RequiresUnreferencedCode("Calls System.Text.Json.JsonSerializer.Serialize<TValue>(TValue, JsonSerializerOptions)")]
    private static void FormatJsonPayload(
        string? inlineBody,
        string? bodyFile,
        PayloadSource payloadSource,
        ref ReadOnlyMemory<byte> payload)
    {
        if (payloadSource == PayloadSource.Base64)
        {
            Console.Error.WriteLine("Warning: cannot pretty-print JSON when payload is provided via --body-base64.");
            return;
        }

        var jsonText = inlineBody;

        if (string.IsNullOrEmpty(jsonText) && !string.IsNullOrEmpty(bodyFile) && File.Exists(bodyFile))
        {
            try
            {
                jsonText = File.ReadAllText(bodyFile);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to read JSON payload file '{bodyFile}': {ex.Message}");
                return;
            }
        }

        if (string.IsNullOrEmpty(jsonText))
        {
            if (payload.IsEmpty)
            {
                return;
            }

            if (!TryDecodeUtf8(payload.Span, out var decoded))
            {
                Console.Error.WriteLine("Warning: payload is not UTF-8; skipping JSON formatting.");
                return;
            }

            jsonText = decoded;
        }

        try
        {
            using var document = JsonDocument.Parse(jsonText);
            var formatted = JsonSerializer.Serialize(document.RootElement, PrettyJsonOptions);
            payload = Encoding.UTF8.GetBytes(formatted);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not format JSON payload: {ex.Message}");
        }
    }

    private static bool TryEncodeProtobufPayload(
        ProtoProcessingState protoState,
        string? inlineBody,
        string? bodyFile,
        PayloadSource payloadSource,
        ref ReadOnlyMemory<byte> payload,
        out string? error)
    {
        error = null;

        if (payloadSource == PayloadSource.Base64)
        {
            // Caller supplied the binary payload directly.
            return true;
        }

        if (!TryLoadMessageDescriptor(protoState.DescriptorPaths, protoState.MessageName, out var descriptor, out error))
        {
            return false;
        }

        var json = inlineBody;

        if (string.IsNullOrEmpty(json) && payloadSource == PayloadSource.File && !string.IsNullOrEmpty(bodyFile))
        {
            try
            {
                json = File.ReadAllText(bodyFile);
            }
            catch (Exception ex)
            {
                error = $"Failed to read payload file '{bodyFile}' as JSON: {ex.Message}";
                return false;
            }
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            json = "{}";
        }

        try
        {
            var parserSettings = JsonParser.Settings.Default
                .WithIgnoreUnknownFields(true)
                .WithTypeRegistry(TypeRegistry.FromFiles(descriptor.File));
            var parser = new JsonParser(parserSettings);
            var message = parser.Parse(json, descriptor);
            payload = message.ToByteArray();
            return true;
        }
        catch (Exception ex)
        {
            // Fall back to a manual serializer if the runtime lacks dynamic parsing support.
            if (TrySerializeMessage(descriptor, json, out var manualPayload, out var manualError))
            {
                payload = manualPayload;
                return true;
            }

            error = manualError ?? $"Failed to encode protobuf payload: {ex.Message}";
            return false;
        }
    }

    private static bool TrySerializeMessage(MessageDescriptor descriptor, string json, out ReadOnlyMemory<byte> payload, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
        }
        catch (Exception ex)
        {
            error = $"JSON payload could not be parsed: {ex.Message}";
            return false;
        }

        if (!TrySerializeMessage(descriptor, document.RootElement, out var buffer, out error))
        {
            return false;
        }

        payload = buffer;
        return true;
    }

    private static bool TrySerializeMessage(MessageDescriptor descriptor, JsonElement element, out ReadOnlyMemory<byte> payload, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
        error = null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Expected JSON object for message '{descriptor.FullName}'.";
            return false;
        }

        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);

        foreach (var property in element.EnumerateObject())
        {
            var field = FindField(descriptor, property.Name);
            if (field is null)
            {
                continue;
            }

            if (!TryWriteField(field, property.Value, output, out error))
            {
                return false;
            }
        }

        output.Flush();
        payload = stream.ToArray();
        return true;
    }

    private static FieldDescriptor? FindField(MessageDescriptor descriptor, string name)
    {
        var field = descriptor.FindFieldByName(name);
        return field ?? descriptor.Fields.InDeclarationOrder().FirstOrDefault(candidate => string.Equals(candidate.JsonName, name, StringComparison.Ordinal) || string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryWriteField(FieldDescriptor field, JsonElement element, CodedOutputStream output, out string? error)
    {
        error = null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (field.IsMap)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                error = $"Map field '{field.FullName}' expects a JSON object.";
                return false;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (!TrySerializeMapEntry(field, property.Name, property.Value, out var entryBytes, out error))
                {
                    return false;
                }

                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(entryBytes));
            }

            return true;
        }

        if (field.IsRepeated)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                error = $"Repeated field '{field.FullName}' expects a JSON array.";
                return false;
            }

            if (field.IsPacked && IsPackable(field.FieldType))
            {
                using var packedStream = new MemoryStream();
                var packedOutput = new CodedOutputStream(packedStream);

                foreach (var item in element.EnumerateArray())
                {
                    if (!TryWriteValue(field, item, packedOutput, writeTag: false, out error))
                    {
                        return false;
                    }
                }

                packedOutput.Flush();
                output.WriteTag(field.FieldNumber, WireFormat.WireType.LengthDelimited);
                var packedBytes = packedStream.ToArray();
                output.WriteBytes(ByteString.CopyFrom(packedBytes));
                return true;
            }

            foreach (var item in element.EnumerateArray())
            {
                if (!TryWriteValue(field, item, output, writeTag: true, out error))
                {
                    return false;
                }
            }

            return true;
        }

        return TryWriteValue(field, element, output, writeTag: true, out error);
    }

    private static bool TryWriteValue(FieldDescriptor field, JsonElement element, CodedOutputStream output, bool writeTag, out string? error)
    {
        error = null;

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (writeTag)
        {
            output.WriteTag(field.FieldNumber, GetWireType(field));
        }

        switch (field.FieldType)
        {
            case FieldType.String:
                output.WriteString(element.GetString() ?? string.Empty);
                return true;
            case FieldType.Bool:
                output.WriteBool(ReadBoolean(element));
                return true;
            case FieldType.Int32:
                output.WriteInt32(ReadInt32(element, field.FullName));
                return true;
            case FieldType.Int64:
                output.WriteInt64(ReadInt64(element, field.FullName));
                return true;
            case FieldType.SInt32:
                output.WriteSInt32(ReadInt32(element, field.FullName));
                return true;
            case FieldType.SInt64:
                output.WriteSInt64(ReadInt64(element, field.FullName));
                return true;
            case FieldType.UInt32:
                output.WriteUInt32(ReadUInt32(element, field.FullName));
                return true;
            case FieldType.UInt64:
                output.WriteUInt64(ReadUInt64(element, field.FullName));
                return true;
            case FieldType.Fixed32:
                output.WriteFixed32(ReadUInt32(element, field.FullName));
                return true;
            case FieldType.Fixed64:
                output.WriteFixed64(ReadUInt64(element, field.FullName));
                return true;
            case FieldType.SFixed32:
                output.WriteSFixed32(ReadInt32(element, field.FullName));
                return true;
            case FieldType.SFixed64:
                output.WriteSFixed64(ReadInt64(element, field.FullName));
                return true;
            case FieldType.Float:
                output.WriteFloat((float)ReadDouble(element, field.FullName));
                return true;
            case FieldType.Double:
                output.WriteDouble(ReadDouble(element, field.FullName));
                return true;
            case FieldType.Bytes:
                output.WriteBytes(ByteString.CopyFrom(ReadBytes(element, field.FullName)));
                return true;
            case FieldType.Enum:
                var enumValue = ReadEnum(field.EnumType, element, field.FullName);
                output.WriteEnum(enumValue.Number);
                return true;
            case FieldType.Message:
                if (!TrySerializeMessage(field.MessageType, element, out var nestedPayload, out error))
                {
                    return false;
                }

                output.WriteBytes(ByteString.CopyFrom(nestedPayload.ToArray()));
                return true;
            default:
                error = $"Unsupported protobuf field type '{field.FieldType}' for '{field.FullName}'.";
                return false;
        }
    }

    private static bool TrySerializeMapEntry(FieldDescriptor mapField, string keyText, JsonElement valueElement, out byte[] payload, out string? error)
    {
        payload = [];
        error = null;

        var entryDescriptor = mapField.MessageType;
        var keyDescriptor = entryDescriptor.Fields.InDeclarationOrder()[0];
        var valueDescriptor = entryDescriptor.Fields.InDeclarationOrder()[1];

        using var stream = new MemoryStream();
        var output = new CodedOutputStream(stream);

        if (!TryWriteMapKey(keyDescriptor, keyText, output, out error))
        {
            return false;
        }

        if (!TryWriteValue(valueDescriptor, valueElement, output, writeTag: true, out error))
        {
            return false;
        }

        output.Flush();
        payload = stream.ToArray();
        return true;
    }

    private static bool TryWriteMapKey(FieldDescriptor keyDescriptor, string keyText, CodedOutputStream output, out string? error)
    {
        error = null;

        output.WriteTag(keyDescriptor.FieldNumber, GetWireType(keyDescriptor));

        try
        {
            switch (keyDescriptor.FieldType)
            {
                case FieldType.String:
                    output.WriteString(keyText);
                    return true;
                case FieldType.Bool:
                    output.WriteBool(bool.Parse(keyText));
                    return true;
                case FieldType.Int32:
                case FieldType.SInt32:
                case FieldType.SFixed32:
                    output.WriteInt32(int.Parse(keyText, CultureInfo.InvariantCulture));
                    return true;
                case FieldType.Int64:
                case FieldType.SInt64:
                case FieldType.SFixed64:
                    output.WriteInt64(long.Parse(keyText, CultureInfo.InvariantCulture));
                    return true;
                case FieldType.UInt32:
                case FieldType.Fixed32:
                    output.WriteUInt32(uint.Parse(keyText, CultureInfo.InvariantCulture));
                    return true;
                case FieldType.UInt64:
                case FieldType.Fixed64:
                    output.WriteUInt64(ulong.Parse(keyText, CultureInfo.InvariantCulture));
                    return true;
                default:
                    error = $"Unsupported map key type '{keyDescriptor.FieldType}'.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            error = $"Could not parse map key '{keyText}' for '{keyDescriptor.FullName}': {ex.Message}";
            return false;
        }
    }

    private static WireFormat.WireType GetWireType(FieldDescriptor field) => field.FieldType switch
    {
        FieldType.Double => WireFormat.WireType.Fixed64,
        FieldType.Float => WireFormat.WireType.Fixed32,
        FieldType.Int64 or FieldType.UInt64 or FieldType.Int32 => WireFormat.WireType.Varint,
        FieldType.Fixed64 => WireFormat.WireType.Fixed64,
        FieldType.Fixed32 => WireFormat.WireType.Fixed32,
        FieldType.Bool => WireFormat.WireType.Varint,
        FieldType.String => WireFormat.WireType.LengthDelimited,
        FieldType.Group => WireFormat.WireType.StartGroup,
        FieldType.Message or FieldType.Bytes => WireFormat.WireType.LengthDelimited,
        FieldType.UInt32 => WireFormat.WireType.Varint,
        FieldType.SFixed32 => WireFormat.WireType.Fixed32,
        FieldType.SFixed64 => WireFormat.WireType.Fixed64,
        _ => WireFormat.WireType.Varint
    };

    private static bool IsPackable(FieldType fieldType) => fieldType switch
    {
        FieldType.String or FieldType.Bytes or FieldType.Message or FieldType.Group => false,
        _ => true
    };

    private static bool ReadBoolean(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => bool.Parse(element.GetString() ?? "false"),
        _ => throw new InvalidOperationException("Expected boolean value."),
    };

    private static int ReadInt32(JsonElement element, string fieldName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) ||
        element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value)
            ? value
            : throw new InvalidOperationException($"Could not parse int32 value for '{fieldName}'.");

    private static long ReadInt64(JsonElement element, string fieldName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) ||
        element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value)
            ? value
            : throw new InvalidOperationException($"Could not parse int64 value for '{fieldName}'.");

    private static uint ReadUInt32(JsonElement element, string fieldName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetUInt32(out var value) ||
        element.ValueKind == JsonValueKind.String && uint.TryParse(element.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value)
            ? value
            : throw new InvalidOperationException($"Could not parse uint32 value for '{fieldName}'.");

    private static ulong ReadUInt64(JsonElement element, string fieldName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var value) ||
        element.ValueKind == JsonValueKind.String && ulong.TryParse(element.GetString(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out value)
            ? value
            : throw new InvalidOperationException($"Could not parse uint64 value for '{fieldName}'.");

    private static double ReadDouble(JsonElement element, string fieldName) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out var value) ||
        element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(),
            NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value)
            ? value
            : throw new InvalidOperationException($"Could not parse floating point value for '{fieldName}'.");

    private static byte[] ReadBytes(JsonElement element, string fieldName)
    {
        var text = element.GetString();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        try
        {
            return Convert.FromBase64String(text);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Could not parse base64 payload for '{fieldName}': {ex.Message}");
        }
    }

    private static EnumValueDescriptor ReadEnum(EnumDescriptor enumDescriptor, JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            return element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numericValue) ||
                   element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), NumberStyles.Integer,
                       CultureInfo.InvariantCulture, out numericValue)
                ? enumDescriptor.FindValueByNumber(numericValue) ??
                  throw new InvalidOperationException(
                      $"Enum numeric value '{numericValue}' is not defined for '{fieldName}'.")
                : throw new InvalidOperationException($"Could not parse enum value for '{fieldName}'.");
        }

        var name = element.GetString() ?? string.Empty;
        var match = enumDescriptor.FindValueByName(name) ?? enumDescriptor.Values.FirstOrDefault(v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        return match ?? throw new InvalidOperationException($"Enum value '{name}' is not defined for '{fieldName}'.");

    }

    private static bool TryLoadMessageDescriptor(
        string[] descriptorInputs,
        string messageName,
        out MessageDescriptor descriptor,
        out string? error)
    {
        descriptor = null!;
        error = null;

        if (descriptorInputs.Length == 0)
        {
            error = "No protobuf descriptor files supplied.";
            return false;
        }

        var resolvedFiles = ResolveDescriptorFiles(descriptorInputs);
        if (resolvedFiles.Count == 0)
        {
            error = "No descriptor files found at the supplied --proto-file paths.";
            return false;
        }

        var normalizedMessage = NormalizeProtoMessageName(messageName);
        var cacheKey = BuildDescriptorCacheKey(resolvedFiles);

        if (DescriptorCache.TryGetValue(cacheKey, out var cachedEntry) &&
            cachedEntry.Messages.TryGetValue(normalizedMessage, out var cachedDescriptor))
        {
            descriptor = cachedDescriptor;
            return true;
        }

        DescriptorCacheEntry entry;
        try
        {
            entry = BuildDescriptorCacheEntry(resolvedFiles);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        DescriptorCache[cacheKey] = entry;

        if (entry.Messages.TryGetValue(normalizedMessage, out var resolvedDescriptor))
        {
            descriptor = resolvedDescriptor;
            return true;
        }

        error = $"Could not find protobuf message '{messageName}' in provided descriptors.";
        descriptor = null!;
        return false;
    }

    private static DescriptorCacheEntry BuildDescriptorCacheEntry(IReadOnlyList<string> descriptorFiles)
    {
        var descriptorProtos = new List<FileDescriptorProto>();

        foreach (var path in descriptorFiles)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var set = FileDescriptorSet.Parser.ParseFrom(stream);
                descriptorProtos.AddRange(set.File);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read descriptor '{path}': {ex.Message}");
            }
        }

        if (!TryBuildFileDescriptors(descriptorProtos, out var descriptorMap, out var buildError))
        {
            throw new InvalidOperationException(buildError ?? "Failed to build protobuf descriptors.");
        }

        var messageMap = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);
        foreach (var message in descriptorMap.Values.SelectMany(descriptor => EnumerateMessages(descriptor.MessageTypes)))
        {
            messageMap[message.FullName] = message;
        }

        return new DescriptorCacheEntry(descriptorMap, messageMap);
    }

    private static bool TryBuildFileDescriptors(
        IEnumerable<FileDescriptorProto> protos,
        out Dictionary<string, FileDescriptor> descriptors,
        out string? error)
    {
        descriptors = new Dictionary<string, FileDescriptor>(StringComparer.Ordinal);
        error = null;

        foreach (var kvp in WellKnownFileDescriptors)
        {
            descriptors[kvp.Key] = kvp.Value;
        }

        var pending = new Dictionary<string, FileDescriptorProto>(StringComparer.Ordinal);
        foreach (var proto in protos)
        {
            // Avoid rebuilding well-known descriptors if they are embedded in the set.
            if (!descriptors.ContainsKey(proto.Name))
            {
                pending[proto.Name] = proto;
            }
        }

        var progress = true;
        while (pending.Count > 0 && progress)
        {
            progress = false;
            foreach (var (name, proto) in pending.ToList())
            {
                var dependencies = new List<FileDescriptor>(proto.Dependency.Count);
                var unresolved = false;

                foreach (var dependencyName in proto.Dependency)
                {
                    if (descriptors.TryGetValue(dependencyName, out var dependencyDescriptor))
                    {
                        dependencies.Add(dependencyDescriptor);
                    }
                    else if (!pending.ContainsKey(dependencyName))
                    {
                        error = $"Descriptor dependency '{dependencyName}' not found for '{name}'.";
                        return false;
                    }
                    else
                    {
                        unresolved = true;
                        break;
                    }
                }

                if (unresolved)
                {
                    continue;
                }

                try
                {
                    var descriptor = BuildFileDescriptor(proto, [.. dependencies]);
                    descriptors[name] = descriptor;
                    pending.Remove(name);
                    progress = true;
                }
                catch (Exception ex)
                {
                    error = $"Failed to materialize descriptor for '{name}': {ex.Message}";
                    return false;
                }
            }
        }

        if (pending.Count <= 0)
        {
            return true;
        }

        error = $"Could not resolve descriptor dependencies for: {string.Join(", ", pending.Keys)}.";
        return false;

    }

    private static IEnumerable<MessageDescriptor> EnumerateMessages(IEnumerable<MessageDescriptor> rootMessages)
    {
        foreach (var message in rootMessages)
        {
            yield return message;

            foreach (var nested in EnumerateMessages(message.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    private static string NormalizeProtoMessageName(string messageName)
    {
        var trimmed = messageName.Trim();
        return trimmed.StartsWith('.') ? trimmed[1..] : trimmed;
    }

    private static List<string> ResolveDescriptorFiles(IEnumerable<string> inputs)
    {
        var files = new List<string>();
        foreach (var input in inputs)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            if (Directory.Exists(input))
            {
                files.AddRange(Directory.EnumerateFiles(input, "*.pb", SearchOption.AllDirectories).Select(Path.GetFullPath));
                files.AddRange(Directory.EnumerateFiles(input, "*.desc", SearchOption.AllDirectories).Select(Path.GetFullPath));
                files.AddRange(Directory.EnumerateFiles(input, "*.protoset", SearchOption.AllDirectories).Select(Path.GetFullPath));
                files.AddRange(Directory.EnumerateFiles(input, "*.fds", SearchOption.AllDirectories).Select(Path.GetFullPath));
                files.AddRange(Directory.EnumerateFiles(input, "*.bin", SearchOption.AllDirectories).Select(Path.GetFullPath));
                continue;
            }

            if (File.Exists(input))
            {
                files.Add(Path.GetFullPath(input));
            }
        }

        return files;
    }

    private static string BuildDescriptorCacheKey(IReadOnlyList<string> descriptorFiles)
    {
        if (descriptorFiles.Count == 0)
        {
            return string.Empty;
        }

        var fragments = new string[descriptorFiles.Count];
        for (var index = 0; index < descriptorFiles.Count; index++)
        {
            var fullPath = descriptorFiles[index];
            var timestamp = File.GetLastWriteTimeUtc(fullPath).Ticks;
            fragments[index] = $"{fullPath}:{timestamp}";
        }

        Array.Sort(fragments, StringComparer.Ordinal);
        return string.Join('|', fragments);
    }

    private static FileDescriptor BuildFileDescriptor(FileDescriptorProto proto, FileDescriptor[] dependencies)
    {
        if (_fileDescriptorBuildFrom is not null)
        {
            return InvokeBuildFrom(_fileDescriptorBuildFrom, proto, dependencies);
        }

        var candidates = typeof(FileDescriptor)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(static method => string.Equals(method.Name, "BuildFrom", StringComparison.Ordinal))
            .ToArray();

        foreach (var candidate in candidates)
        {
            var arguments = TryCreateBuildFromArguments(candidate.GetParameters(), proto, dependencies);
            if (arguments is null)
            {
                continue;
            }

            try
            {
                var result = candidate.Invoke(null, arguments);
                if (result is not FileDescriptor descriptor)
                {
                    continue;
                }

                _fileDescriptorBuildFrom = candidate;
                return descriptor;
            }
            catch
            {
                // try the next candidate
            }
        }

        throw new InvalidOperationException("Google.Protobuf internal API 'FileDescriptor.BuildFrom' is not available in this runtime.");
    }

    private static FileDescriptor InvokeBuildFrom(MethodInfo method, FileDescriptorProto proto, FileDescriptor[] dependencies)
    {
        var arguments = TryCreateBuildFromArguments(method.GetParameters(), proto, dependencies)
            ?? throw new InvalidOperationException("Cached FileDescriptor.BuildFrom signature is no longer supported.");

        var result = method.Invoke(null, arguments)
            ?? throw new InvalidOperationException("Failed to build descriptor.");

        return (FileDescriptor)result;
    }

    private static object[]? TryCreateBuildFromArguments(ParameterInfo[] parameters, FileDescriptorProto proto, FileDescriptor[] dependencies)
    {
        if (parameters.Length == 0)
        {
            return null;
        }

        var arguments = new object[parameters.Length];

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            if (parameterType == typeof(FileDescriptorProto))
            {
                arguments[index] = proto;
            }
            else if (parameterType == typeof(FileDescriptor[]) || typeof(IEnumerable<FileDescriptor>).IsAssignableFrom(parameterType))
            {
                arguments[index] = dependencies;
            }
            else if (parameterType == typeof(bool))
            {
                arguments[index] = true;
            }
            else if (parameterType == typeof(string))
            {
                arguments[index] = string.Empty;
            }
            else
            {
                arguments[index] = parameterType.IsValueType
                    ? Activator.CreateInstance(parameterType)!
                    : null!;
            }
        }

        return arguments;
    }

    private static void EnsureHeader(List<KeyValuePair<string, string>> headers, string key, string value)
    {
        for (var index = 0; index < headers.Count; index++)
        {
            if (!string.Equals(headers[index].Key, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            headers[index] = new KeyValuePair<string, string>(key, value);
            return;
        }

        headers.Add(new KeyValuePair<string, string>(key, value));
    }

    private static bool TryResolvePayload(string? body, string? bodyFile, string? bodyBase64, out ReadOnlyMemory<byte> payload, out PayloadSource source, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
        source = PayloadSource.None;
        error = null;

        var sources = new[]
        {
            (Name: "--body", HasValue: !string.IsNullOrEmpty(body)),
            (Name: "--body-file", HasValue: !string.IsNullOrEmpty(bodyFile)),
            (Name: "--body-base64", HasValue: !string.IsNullOrEmpty(bodyBase64))
        };

        var active = sources.Count(static s => s.HasValue);
        if (active > 1)
        {
            error = "Specify only one of --body, --body-file, or --body-base64.";
            return false;
        }

        if (!string.IsNullOrEmpty(bodyBase64))
        {
            try
            {
                payload = Convert.FromBase64String(bodyBase64);
                source = PayloadSource.Base64;
                return true;
            }
            catch (FormatException ex)
            {
                error = $"Failed to decode base64 payload: {ex.Message}";
                return false;
            }
        }

        if (!string.IsNullOrEmpty(bodyFile))
        {
            if (!File.Exists(bodyFile))
            {
                error = $"Payload file '{bodyFile}' does not exist.";
                return false;
            }

            payload = File.ReadAllBytes(bodyFile);
            source = PayloadSource.File;
            return true;
        }

        if (!string.IsNullOrEmpty(body))
        {
            payload = Encoding.UTF8.GetBytes(body);
            source = PayloadSource.Inline;
            return true;
        }

        payload = ReadOnlyMemory<byte>.Empty;
        source = PayloadSource.None;
        return true;
    }

    private sealed class ProfileProcessingState
    {
        public static bool PrettyPrintJson { get; set; }

        public static ProtoProcessingState? Proto { get; set; }
    }

    private sealed record ProtoProcessingState(string[] DescriptorPaths, string MessageName);

    private enum PayloadSource
    {
        None,
        Inline,
        File,
        Base64
    }

    private sealed record DescriptorCacheEntry(
        Dictionary<string, FileDescriptor> Files,
        Dictionary<string, MessageDescriptor> Messages);

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> data, out string text)
    {
        try
        {
            text = Encoding.UTF8.GetString(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 2)
        {
            duration = TimeSpan.Zero;
            return false;
        }

        var suffixes = new Dictionary<string, Func<double, TimeSpan>>(StringComparer.OrdinalIgnoreCase)
        {
            ["ms"] = TimeSpan.FromMilliseconds,
            ["s"] = TimeSpan.FromSeconds,
            ["m"] = TimeSpan.FromMinutes,
            ["h"] = TimeSpan.FromHours
        };

        foreach (var (suffix, factory) in suffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var numberPart = trimmed[..^suffix.Length];
                if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
                {
                    duration = factory(scalar);
                    return true;
                }

                duration = TimeSpan.Zero;
                return false;
            }
        }

        duration = TimeSpan.Zero;
        return false;
    }

    private static bool TrySplitKeyValue(string source, out string key, out string value)
    {
        var separatorIndex = source.IndexOf('=');
        if (separatorIndex <= 0 || separatorIndex == source.Length - 1)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = source[..separatorIndex];
        value = source[(separatorIndex + 1)..];
        return true;
    }

    private static JsonSerializerOptions CreateJsonOptions(
        JsonSerializerDefaults defaults = JsonSerializerDefaults.Web,
        Action<JsonSerializerOptions>? configure = null)
    {
        var options = new JsonSerializerOptions(defaults);
        EnsureSerializerTypeInfo(options);
        configure?.Invoke(options);
        return options;
    }

    private static void EnsureSerializerTypeInfo(JsonSerializerOptions options)
    {
        var additionalResolver = JsonSerializer.IsReflectionEnabledByDefault
            ? ReflectionBackedResolver.Value
            : CliOnlyResolver.Value;

        options.TypeInfoResolver = options.TypeInfoResolver is null
            ? additionalResolver
            : JsonTypeInfoResolver.Combine(options.TypeInfoResolver, additionalResolver);
    }

    private static readonly Lazy<IJsonTypeInfoResolver> CliOnlyResolver = new(static () => OmniRelayCliJsonContext.Default);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Reflection-based JSON fallback is only used when dynamic code is available.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Reflection-based JSON fallback is only used when dynamic code is available.")]
    private static readonly Lazy<IJsonTypeInfoResolver> ReflectionBackedResolver = new(static () =>
        JsonTypeInfoResolver.Combine(OmniRelayCliJsonContext.Default, new DefaultJsonTypeInfoResolver()));
}
