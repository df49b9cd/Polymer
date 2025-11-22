using System.Buffers;
using System.CommandLine;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Hugo;
using Microsoft.Extensions.Configuration;
using OmniRelay.Cli.Core;
using OmniRelay.Cli.Modules;
using OmniRelay.Core;
using OmniRelay.Core.Transport;
using OmniRelay.Dispatcher;
using OmniRelay.Errors;
using OmniRelay.Transport.Grpc;
using OmniRelay.Transport.Http;

namespace OmniRelay.Cli;

public static partial class Program
{
    static Program()
    {
        ProtobufPayloadRegistration.RegisterGenerated();
        ProtobufPayloadRegistration.RegisterManualEncoders();
    }

    internal const string DefaultConfigSection = "omnirelay";
    internal const string DefaultIntrospectionUrl = "http://127.0.0.1:8080/omnirelay/introspect";
    internal const int PrettyPrintLimitBytes = 512 * 1024; // avoid large buffering/LOH during pretty-print
    private const int MaxPayloadBytes = 1 * 1024 * 1024;   // cap payload materialization to avoid LOH
    private const int Base64ChunkBytes = 12 * 1024;        // multiple-of-3 chunk to stream base64 without LOH

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

    internal static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("OmniRelay CLI providing configuration validation, dispatcher introspection, and ad-hoc request tooling.");

        // Configure shared benchmarking hooks once for the process.
        BenchmarkRunner.HttpClientFactory = static () => CliRuntime.HttpClientFactory.CreateClient();
        BenchmarkRunner.GrpcInvokerFactory = static (addresses, service, runtime) => CliRuntime.GrpcInvokerFactory.Create(addresses, service, runtime);

        foreach (var module in CliModules.GetDefaultModules())
        {
            root.Add(module.Build());
        }

        return root;
    }

    internal static Command CreateRequestCommand()
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
            Description = "(Deprecated) Path(s) to FileDescriptorSet binaries. Ignored unless using binary payloads.",
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

    internal static Command CreateBenchmarkCommand()
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
            Description = "Path(s) to FileDescriptorSet binaries used for protobuf encoding. (Deprecated; binary-only when provided.)",
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

    internal static bool TryBuildConfiguration(string[] configPaths, string[] setOverrides, out IConfigurationRoot configuration, out string? errorMessage)
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

    internal static void TryWriteReadyFile(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            CliRuntime.FileSystem.EnsureDirectory(directory);
            CliRuntime.FileSystem.WriteAllText(path, DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            CliRuntime.Console.WriteError($"Failed to write ready file '{path}': {ex.Message}");
        }
    }

    internal static async Task<int> RunRequestAsync(
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

    internal static bool TryBuildRequestInvocation(
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

        var payloadSourcesSpecified = (string.IsNullOrEmpty(body) ? 0 : 1)
                                      + (string.IsNullOrEmpty(bodyFile) ? 0 : 1)
                                      + (string.IsNullOrEmpty(bodyBase64) ? 0 : 1);

        if (payloadSourcesSpecified > 1)
        {
            error = "Specify only one of --body, --body-file, or --body-base64.";
            return false;
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

    internal static async Task<int> RunBenchmarkAsync(
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

    internal static async Task<int> ExecuteHttpRequestAsync(string? url, Request<ReadOnlyMemory<byte>> request, HttpClientRuntimeOptions? runtimeOptions, CancellationToken cancellationToken)
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

        using var httpClient = CliRuntime.HttpClientFactory.CreateClient();
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

    internal static async Task<int> ExecuteGrpcRequestAsync(string[] addresses, string remoteService, Request<ReadOnlyMemory<byte>> request, GrpcClientRuntimeOptions? runtimeOptions, CancellationToken cancellationToken)
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

        var invoker = CliRuntime.GrpcInvokerFactory.Create(uris, remoteService, runtimeOptions);

        await using (invoker.ConfigureAwait(false))
        {
            try
            {
                await invoker.StartAsync(cancellationToken).ConfigureAwait(false);
                var result = await invoker.CallAsync(request, cancellationToken).ConfigureAwait(false);

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
                await invoker.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
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
            WriteBase64Body(response.Body.Span);
        }
    }

    private static void PrintError(Error error, string transport)
    {
        var omnirelayException = OmniRelayErrors.FromError(error, transport);
        Console.Error.WriteLine($"Request failed with status {omnirelayException.StatusCode}: {omnirelayException.Message}");

        if (omnirelayException.Error.Metadata is { Count: > 0 } metadata)
        {
            Console.Error.WriteLine("Metadata:");
            foreach (var kvp in metadata.OrderBy(static m => m.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  {kvp.Key}: {kvp.Value}");
            }
        }

        if (omnirelayException.InnerException is { } inner)
        {
            Console.Error.WriteLine($"Inner exception: {inner.GetType().Name}: {inner.Message}");
        }
    }

    internal static void PrintProcedureGroup(string label, IEnumerable<string> names)
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

    internal static void PrintOutboundGroup(string label, IReadOnlyList<OutboundBindingDescriptor> bindings)
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

    internal static void PrintMiddlewareLine(string label, IReadOnlyList<string> inbound, IReadOnlyList<string> outbound) => Console.WriteLine($"  {label}: inbound[{inbound.Count}] outbound[{outbound.Count}]");

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

    private static void EnsureHeader(List<KeyValuePair<string, string>> headers, string key, string value)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);

        if (headers.Any(h => string.Equals(h.Key, key, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        headers.Add(new KeyValuePair<string, string>(key, value));
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
                    if (state.ProtoMessageName is not null)
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

                    // In AOT mode we rely on compiled encoders; descriptor files are only valid when supplying raw binary payloads.
                    state.ProtoMessageName = messageName.Trim();
                    state.HasExternalDescriptors = protoFiles?.Length > 0;
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

        if (state.ProtoMessageName is not null)
        {
            var binaryPayloadProvided = payloadSource is PayloadSource.Base64 || (payloadSource is PayloadSource.File && !payload.IsEmpty);

            // If caller supplied raw binary (base64 or file), accept as-is.
            if (!binaryPayloadProvided)
            {
                var jsonText = inlineBody;
                if (string.IsNullOrEmpty(jsonText) && payloadSource == PayloadSource.File && !string.IsNullOrEmpty(bodyFile))
                {
                    try
                    {
                        jsonText = File.ReadAllText(bodyFile);
                    }
                    catch (Exception ex)
                    {
                        error = $"Failed to read payload file '{bodyFile}': {ex.Message}";
                        return false;
                    }
                }

                jsonText ??= "{}";

                if (!ProtobufPayloadRegistry.Instance.TryEncode(state.ProtoMessageName, jsonText, out payload, out error))
                {
                    if (state.HasExternalDescriptors)
                    {
                        error ??= "Runtime .proto descriptor loading isn't supported in NativeAOT. Provide a binary payload via --body-base64/--body-file or ensure a generated encoder is registered for the message.";
                    }

                    return false;
                }
            }
        }

        if (state.PrettyPrintJson)
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
                state.PrettyPrintJson = true;
                break;
            default:
                Console.Error.WriteLine($"Warning: unknown json profile '{qualifier}'. Falling back to default settings.");
                break;
        }

        encoding ??= "application/json";
        EnsureHeader(headers, "Content-Type", encoding);
        EnsureHeader(headers, "Accept", "application/json");
    }

    private static void FormatJsonPayload(
        string? inlineBody,
        string? bodyFile,
        PayloadSource payloadSource,
        ref ReadOnlyMemory<byte> payload)
    {
        // Guard against reformatting large payloads that would allocate on LOH.
        if (payload.Length > PrettyPrintLimitBytes)
        {
            Console.Error.WriteLine($"Warning: payload size {payload.Length:N0} bytes exceeds pretty-print limit ({PrettyPrintLimitBytes:N0}); skipping formatting.");
            return;
        }

        if (payloadSource == PayloadSource.Base64)
        {
            Console.Error.WriteLine("Warning: cannot pretty-print JSON when payload is provided via --body-base64.");
            return;
        }

        ReadOnlyMemory<byte> sourcePayload = payload;

        if (sourcePayload.IsEmpty)
        {
            if (!string.IsNullOrEmpty(inlineBody))
            {
                var inlineBytes = Encoding.UTF8.GetBytes(inlineBody);
                if (inlineBytes.Length > PrettyPrintLimitBytes)
                {
                    Console.Error.WriteLine($"Warning: payload size {inlineBytes.Length:N0} bytes exceeds pretty-print limit ({PrettyPrintLimitBytes:N0}); skipping formatting.");
                    return;
                }

                sourcePayload = inlineBytes;
            }
            else if (!string.IsNullOrEmpty(bodyFile) && File.Exists(bodyFile))
            {
                try
                {
                    var info = new FileInfo(bodyFile);
                    if (info.Length > PrettyPrintLimitBytes)
                    {
                        Console.Error.WriteLine($"Warning: payload size {info.Length:N0} bytes exceeds pretty-print limit ({PrettyPrintLimitBytes:N0}); skipping formatting.");
                        return;
                    }

                    sourcePayload = File.ReadAllBytes(bodyFile);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Warning: failed to read JSON payload file '{bodyFile}': {ex.Message}");
                    return;
                }
            }
        }

        if (sourcePayload.IsEmpty)
        {
            return;
        }

        try
        {
            var initialCapacity = Math.Max(Math.Min(PrettyPrintLimitBytes, sourcePayload.Length + 256), 1024);
            var buffer = new ArrayBufferWriter<byte>(initialCapacity);

            using (var document = JsonDocument.Parse(sourcePayload, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }))
            {
                using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
                document.RootElement.WriteTo(writer);
            }

            payload = buffer.WrittenMemory;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Warning: could not format JSON payload: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: could not format JSON payload: {ex.Message}");
        }
    }

    private static bool TryResolvePayload(
        string? inlineBody,
        string? bodyFile,
        string? bodyBase64,
        out ReadOnlyMemory<byte> payload,
        out PayloadSource source,
        out string? error)
    {
        error = null;
        payload = ReadOnlyMemory<byte>.Empty;
        source = PayloadSource.Inline;

        if (!string.IsNullOrEmpty(bodyBase64))
        {
            try
            {
                payload = Convert.FromBase64String(bodyBase64);
                if (payload.Length > MaxPayloadBytes)
                {
                    error = $"Decoded base64 payload is too large ({payload.Length:N0} bytes). Limit is {MaxPayloadBytes:N0} bytes.";
                    payload = ReadOnlyMemory<byte>.Empty;
                    return false;
                }
                source = PayloadSource.Base64;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to parse --body-base64: {ex.Message}";
                return false;
            }
        }

        if (!string.IsNullOrEmpty(bodyFile))
        {
            if (!File.Exists(bodyFile))
            {
                error = $"Body file '{bodyFile}' does not exist.";
                return false;
            }

            try
            {
                var info = new FileInfo(bodyFile);
                if (info.Length > MaxPayloadBytes)
                {
                    error = $"Body file is too large ({info.Length:N0} bytes). Limit is {MaxPayloadBytes:N0} bytes.";
                    return false;
                }

                payload = File.ReadAllBytes(bodyFile);
                source = PayloadSource.File;
                return true;
            }
            catch (Exception ex)
            {
                error = $"Failed to read body file '{bodyFile}': {ex.Message}";
                return false;
            }
        }

        if (inlineBody is not null)
        {
            if (Encoding.UTF8.GetByteCount(inlineBody) > MaxPayloadBytes)
            {
                error = $"Inline body is too large. Limit is {MaxPayloadBytes:N0} bytes.";
                return false;
            }

            payload = Encoding.UTF8.GetBytes(inlineBody);
            source = PayloadSource.Inline;
            return true;
        }

        // Empty payload is OK for some requests.
        payload = ReadOnlyMemory<byte>.Empty;
        source = PayloadSource.Inline;
        return true;
    }

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

    private sealed class ProfileProcessingState
    {
        public bool PrettyPrintJson { get; set; }
        public string? ProtoMessageName { get; set; }
        public bool HasExternalDescriptors { get; set; }
    }

    private enum PayloadSource
    {
        Inline,
        File,
        Base64
    }

    internal static bool TryParseDuration(string value, out TimeSpan duration)
    {
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out duration))
        {
            return true;
        }

        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 2)
        {
            duration = TimeSpan.Zero;
            return false;
        }

        if (TryParseWithSuffix(trimmed, "ms", TimeSpan.FromMilliseconds, out duration) ||
            TryParseWithSuffix(trimmed, "s", TimeSpan.FromSeconds, out duration) ||
            TryParseWithSuffix(trimmed, "m", TimeSpan.FromMinutes, out duration) ||
            TryParseWithSuffix(trimmed, "h", TimeSpan.FromHours, out duration))
        {
            return true;
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

    private static bool TryParseWithSuffix(ReadOnlySpan<char> value, ReadOnlySpan<char> suffix, Func<double, TimeSpan> factory, out TimeSpan duration)
    {
        if (!value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            duration = default;
            return false;
        }

        var numberPart = value[..^suffix.Length];
        if (double.TryParse(numberPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
        {
            duration = factory(scalar);
            return true;
        }

        duration = default;
        return false;
    }

    private static void WriteBase64Body(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            Console.WriteLine("<empty>");
            return;
        }

        var buffer = ArrayPool<char>.Shared.Rent(CalculateBase64CharLength(Base64ChunkBytes));

        try
        {
            var remaining = payload;
            while (!remaining.IsEmpty)
            {
                var chunkLength = Math.Min(Base64ChunkBytes, remaining.Length);
                var chunk = remaining[..chunkLength];

                if (!Convert.TryToBase64Chars(chunk, buffer, out var written))
                {
                    throw new InvalidOperationException("Failed to encode payload as base64.");
                }

                Console.Write(buffer.AsSpan(0, written));
                remaining = remaining[chunkLength..];
            }

            Console.WriteLine();
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static int CalculateBase64CharLength(int byteLength) => ((byteLength + 2) / 3) * 4;
}
