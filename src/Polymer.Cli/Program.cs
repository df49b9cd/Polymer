using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polymer.Configuration;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Dispatcher;
using Polymer.Errors;
using Polymer.Transport.Grpc;
using Polymer.Transport.Http;

namespace Polymer.Cli;

public static class Program
{
    private const string DefaultConfigSection = "polymer";
    private const string DefaultIntrospectionUrl = "http://127.0.0.1:8080/polymer/introspect";

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
            Console.Error.WriteLine("Operation cancelled.");
            return 2;
        }
    }

    private static RootCommand BuildRootCommand()
    {
        var root = new RootCommand("Polymer CLI providing configuration validation, dispatcher introspection, and ad-hoc request tooling.");
        root.Add(CreateConfigCommand());
        root.Add(CreateIntrospectCommand());
        root.Add(CreateRequestCommand());
        root.Add(CreateScriptCommand());
        return root;
    }

    private static Command CreateConfigCommand()
    {
        var command = new Command("config", "Configuration utilities.");
        command.Add(CreateConfigValidateCommand());
        return command;
    }

    private static Command CreateScriptCommand()
    {
        var command = new Command("script", "Run scripted Polymer CLI automation.");

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
        var command = new Command("validate", "Validate Polymer dispatcher configuration.");

        var configOption = new Option<string[]>("--config")
        {
            Description = "Configuration file(s) to load.",
            AllowMultipleArgumentsPerToken = true,
            Required = true
        };
        configOption.Arity = ArgumentArity.OneOrMore;
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
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);

        command.SetAction(parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? Array.Empty<string>();
            var section = parseResult.GetValue(sectionOption) ?? DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? Array.Empty<string>();
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
            DefaultValueFactory = _ => Array.Empty<string>()
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

        var addressOption = new Option<string[]>("--address")
        {
            Description = "gRPC address(es) to dial.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        command.Add(transportOption);
        command.Add(serviceOption);
        command.Add(procedureOption);
        command.Add(callerOption);
        command.Add(encodingOption);
        command.Add(headerOption);
        command.Add(shardKeyOption);
        command.Add(routingKeyOption);
        command.Add(routingDelegateOption);
        command.Add(ttlOption);
        command.Add(deadlineOption);
        command.Add(timeoutOption);
        command.Add(bodyOption);
        command.Add(bodyFileOption);
        command.Add(bodyBase64Option);
        command.Add(httpUrlOption);
        command.Add(addressOption);

        command.SetAction(parseResult =>
        {
            var transport = parseResult.GetValue(transportOption) ?? "http";
            var service = parseResult.GetValue(serviceOption) ?? string.Empty;
            var procedure = parseResult.GetValue(procedureOption) ?? string.Empty;
            var caller = parseResult.GetValue(callerOption);
            var encoding = parseResult.GetValue(encodingOption);
            var headers = parseResult.GetValue(headerOption) ?? Array.Empty<string>();
            var shardKey = parseResult.GetValue(shardKeyOption);
            var routingKey = parseResult.GetValue(routingKeyOption);
            var routingDelegate = parseResult.GetValue(routingDelegateOption);
            var ttl = parseResult.GetValue(ttlOption);
            var deadline = parseResult.GetValue(deadlineOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var body = parseResult.GetValue(bodyOption);
            var bodyFile = parseResult.GetValue(bodyFileOption);
            var bodyBase64 = parseResult.GetValue(bodyBase64Option);
            var httpUrl = parseResult.GetValue(httpUrlOption);
            var addresses = parseResult.GetValue(addressOption) ?? Array.Empty<string>();

            return RunRequestAsync(
                    transport,
                    service,
                    procedure,
                    caller,
                    encoding,
                    headers,
                    shardKey,
                    routingKey,
                    routingDelegate,
                    ttl,
                    deadline,
                    timeout,
                    body,
                    bodyFile,
                    bodyBase64,
                    httpUrl,
                    addresses)
                .GetAwaiter()
                .GetResult();
        });

        return command;
    }

    private static async Task<int> RunConfigValidateAsync(string[] configPaths, string section, string[] setOverrides)
    {
        if (configPaths.Length == 0)
        {
            Console.Error.WriteLine("No configuration files supplied. Use --config <path> (repeat for layering).");
            return 1;
        }

        var builder = new ConfigurationBuilder();

        foreach (var path in configPaths)
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Configuration file '{path}' does not exist.");
                return 1;
            }

            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".jsn", StringComparison.OrdinalIgnoreCase))
            {
                builder.AddJsonFile(path, optional: false, reloadOnChange: false);
            }
            else
            {
                Console.Error.WriteLine($"Unsupported configuration format '{extension}'. Only JSON is currently supported.");
                return 1;
            }
        }

        if (setOverrides.Length > 0)
        {
            var overlay = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in setOverrides)
            {
                if (!TrySplitKeyValue(entry, out var key, out var value))
                {
                    Console.Error.WriteLine($"Could not parse --set value '{entry}'. Expected KEY=VALUE.");
                    return 1;
                }

                overlay[key] = value;
            }

            builder.AddInMemoryCollection(overlay!);
        }

        IConfigurationRoot configuration;
        try
        {
            configuration = builder.Build();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to build configuration: {ex.Message}");
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
            services.AddPolymerDispatcher(configuration.GetSection(section ?? DefaultConfigSection));
        }
        catch (PolymerConfigurationException ex)
        {
            Console.Error.WriteLine($"Configuration invalid: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to configure Polymer dispatcher: {ex.Message}");
            return 1;
        }

        try
        {
            using var provider = services.BuildServiceProvider();
            var dispatcher = provider.GetRequiredService<Polymer.Dispatcher.Dispatcher>();
            var summary = dispatcher.Introspect();

            Console.WriteLine($"Configuration valid for service '{summary.Service}'.");
            Console.WriteLine($"Status: {summary.Status}");
            Console.WriteLine("Procedures:");
            Console.WriteLine($"  Unary:        {summary.Procedures.Unary.Length}");
            Console.WriteLine($"  Oneway:       {summary.Procedures.Oneway.Length}");
            Console.WriteLine($"  Stream:       {summary.Procedures.Stream.Length}");
            Console.WriteLine($"  ClientStream: {summary.Procedures.ClientStream.Length}");
            Console.WriteLine($"  Duplex:       {summary.Procedures.Duplex.Length}");

            if (summary.Components.Length > 0)
            {
                Console.WriteLine("Lifecycle components:");
                foreach (var component in summary.Components)
                {
                    Console.WriteLine($"  - {component.Name} ({component.ComponentType})");
                }
            }

            return 0;
        }
        catch (PolymerConfigurationException ex)
        {
            Console.Error.WriteLine($"Configuration validation failed: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Dispatcher validation threw: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunIntrospectAsync(string url, string format, string? timeoutOption)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "text" : format.ToLowerInvariant();
        var timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            Console.Error.WriteLine($"Could not parse timeout '{timeoutOption}'. Use standard TimeSpan formats or suffixes like 5s/1m.");
            return 1;
        }

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var response = await httpClient.GetAsync(url, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Introspection request failed: {(int)response.StatusCode} {response.ReasonPhrase}.");
                return 1;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

            var snapshot = await JsonSerializer.DeserializeAsync<DispatcherIntrospection>(stream, options, cts.Token).ConfigureAwait(false);
            if (snapshot is null)
            {
                Console.Error.WriteLine("Introspection response was empty.");
                return 1;
            }

            if (normalizedFormat is "json" or "raw")
            {
                var outputOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                };
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
                Console.Error.WriteLine($"Unknown format '{format}'. Expected 'text' or 'json'.");
                return 1;
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            Console.Error.WriteLine("Introspection request timed out.");
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Introspection failed: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunRequestAsync(
        string transport,
        string service,
        string procedure,
        string? caller,
        string? encoding,
        string[] headerValues,
        string? shardKey,
        string? routingKey,
        string? routingDelegate,
        string? ttlOption,
        string? deadlineOption,
        string? timeoutOption,
        string? body,
        string? bodyFile,
        string? bodyBase64,
        string? httpUrl,
        string[] addresses)
    {
        transport = string.IsNullOrWhiteSpace(transport) ? "http" : transport.ToLowerInvariant();
        var headers = headerValues ?? Array.Empty<string>();

        if (transport is not ("http" or "grpc"))
        {
            Console.Error.WriteLine($"Unsupported transport '{transport}'. Use 'http' or 'grpc'.");
            return 1;
        }

        TimeSpan? ttl = null;
        if (!string.IsNullOrWhiteSpace(ttlOption))
        {
            if (!TryParseDuration(ttlOption!, out var parsedTtl))
            {
                Console.Error.WriteLine($"Could not parse TTL '{ttlOption}'. Use formats like '00:00:05' or suffixes (e.g. 5s, 1m).");
                return 1;
            }

            ttl = parsedTtl;
        }

        DateTimeOffset? deadline = null;
        if (!string.IsNullOrWhiteSpace(deadlineOption))
        {
            if (!DateTimeOffset.TryParse(deadlineOption, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
            {
                Console.Error.WriteLine($"Could not parse deadline '{deadlineOption}'. Provide an ISO-8601 timestamp.");
                return 1;
            }

            deadline = parsedDeadline;
        }

        TimeSpan? timeout = null;
        if (!string.IsNullOrWhiteSpace(timeoutOption))
        {
            if (!TryParseDuration(timeoutOption!, out var parsedTimeout))
            {
                Console.Error.WriteLine($"Could not parse timeout '{timeoutOption}'.");
                return 1;
            }

            timeout = parsedTimeout;
        }

        if (!TryParseHeaders(headers, out var headerPairs))
        {
            return 1;
        }

        if (!TryResolvePayload(body, bodyFile, bodyBase64, out var payload, out var payloadError))
        {
            Console.Error.WriteLine(payloadError ?? "Failed to resolve payload.");
            return 1;
        }

        var meta = new RequestMeta(
            service: service,
            procedure: procedure,
            caller: caller,
            encoding: encoding,
            transport: transport,
            shardKey: shardKey,
            routingKey: routingKey,
            routingDelegate: routingDelegate,
            timeToLive: ttl,
            deadline: deadline,
            headers: headerPairs);

        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);
        using var cts = timeout.HasValue && timeout.Value > TimeSpan.Zero
            ? new CancellationTokenSource(timeout.Value)
            : new CancellationTokenSource(TimeSpan.FromSeconds(30));

        return transport switch
        {
            "http" => await ExecuteHttpRequestAsync(httpUrl, request, cts.Token).ConfigureAwait(false),
            "grpc" => await ExecuteGrpcRequestAsync(addresses ?? Array.Empty<string>(), service, request, cts.Token).ConfigureAwait(false),
            _ => 1
        };
    }

    private static async Task<int> ExecuteHttpRequestAsync(string? url, Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Console.Error.WriteLine("HTTP transport requires --url specifying the request endpoint.");
            return 1;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
        {
            Console.Error.WriteLine($"Invalid URL '{url}'.");
            return 1;
        }

        using var httpClient = new HttpClient();
        var outbound = new HttpOutbound(httpClient, requestUri);

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
            Console.Error.WriteLine($"HTTP call failed: {ex.Message}");
            return 1;
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteGrpcRequestAsync(string[] addresses, string remoteService, Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
    {
        if (addresses.Length == 0)
        {
            Console.Error.WriteLine("gRPC transport requires at least one --address option (e.g. --address http://127.0.0.1:9090).");
            return 1;
        }

        var uris = new List<Uri>(addresses.Length);
        foreach (var address in addresses)
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                Console.Error.WriteLine($"Invalid gRPC address '{address}'.");
                return 1;
            }

            uris.Add(uri);
        }

        var outbound = new GrpcOutbound(uris, remoteService);

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
            Console.Error.WriteLine($"gRPC call failed: {ex.Message}");
            return 1;
        }
        finally
        {
            await outbound.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> RunAutomationAsync(string scriptPath, bool dryRun, bool continueOnError)
    {
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            Console.Error.WriteLine("Script path was empty.");
            return 1;
        }

        if (!File.Exists(scriptPath))
        {
            Console.Error.WriteLine($"Script file '{scriptPath}' does not exist.");
            return 1;
        }

        AutomationScript? script;
        try
        {
            await using var stream = File.OpenRead(scriptPath);
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true
            };
            script = await JsonSerializer.DeserializeAsync<AutomationScript>(stream, options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to parse script '{scriptPath}': {ex.Message}");
            return 1;
        }

        if (script?.Steps is null || script.Steps.Length == 0)
        {
            Console.Error.WriteLine($"Script '{scriptPath}' does not contain any steps.");
            return 1;
        }

        Console.WriteLine($"Loaded script '{scriptPath}' with {script.Steps.Length} step(s).");
        var exitCode = 0;

        for (var index = 0; index < script.Steps.Length; index++)
        {
            var step = script.Steps[index];
            var typeLabel = string.IsNullOrWhiteSpace(step.Type) ? "(unspecified)" : step.Type;
            var description = string.IsNullOrWhiteSpace(step.Description) ? string.Empty : $" - {step.Description}";
            Console.WriteLine($"[{index + 1}/{script.Steps.Length}] {typeLabel}{description}");

            var normalizedType = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            switch (normalizedType)
            {
                case "request":
                    if (string.IsNullOrWhiteSpace(step.Service) || string.IsNullOrWhiteSpace(step.Procedure))
                    {
                        Console.Error.WriteLine("  Request step is missing 'service' or 'procedure'.");
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    var headerPairs = step.Headers?.Select(static kvp => $"{kvp.Key}={kvp.Value}").ToArray() ?? Array.Empty<string>();
                    var addresses = step.Addresses ?? Array.Empty<string>();
                    if (addresses.Length == 0 && !string.IsNullOrWhiteSpace(step.Address))
                    {
                        addresses = new[] { step.Address! };
                    }

                    var targetSummary = !string.IsNullOrWhiteSpace(step.Url)
                        ? step.Url
                        : (addresses.Length > 0 ? string.Join(", ", addresses) : "(default transport settings)");
                    Console.WriteLine($"  -> {step.Transport ?? "http"} {step.Service}/{step.Procedure} @ {targetSummary}");

                    if (dryRun)
                    {
                        Console.WriteLine("  dry-run: skipping request execution.");
                        continue;
                    }

                    var requestResult = await RunRequestAsync(
                        step.Transport ?? "http",
                        step.Service,
                        step.Procedure,
                        step.Caller,
                        step.Encoding,
                        headerPairs,
                        step.ShardKey,
                        step.RoutingKey,
                        step.RoutingDelegate,
                        step.Ttl,
                        step.Deadline,
                        step.Timeout,
                        step.Body,
                        step.BodyFile,
                        step.BodyBase64,
                        step.Url,
                        addresses).ConfigureAwait(false);

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
                    var targetUrl = string.IsNullOrWhiteSpace(step.Url) ? DefaultIntrospectionUrl : step.Url!;
                    Console.WriteLine($"  -> GET {targetUrl} (format={step.Format ?? "text"})");

                    if (dryRun)
                    {
                        Console.WriteLine("  dry-run: skipping introspection call.");
                        continue;
                    }

                    var introspectResult = await RunIntrospectAsync(targetUrl, step.Format ?? "text", step.Timeout).ConfigureAwait(false);
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
                    var delayValue = step.Duration ?? step.Delay;
                    if (string.IsNullOrWhiteSpace(delayValue))
                    {
                        Console.Error.WriteLine("  Delay step requires a 'duration' or 'delay' value.");
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
                        Console.Error.WriteLine($"  Could not parse delay '{delayValue}'.");
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
                            Console.Error.WriteLine($"  Delay interrupted: {ex.Message}");
                            exitCode = exitCode == 0 ? 1 : exitCode;
                            if (!continueOnError)
                            {
                                return exitCode;
                            }
                        }
                    }
                    break;

                default:
                    Console.Error.WriteLine($"  Unknown script step type '{step.Type}'.");
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

    private sealed record AutomationScript
    {
        public AutomationStep[] Steps { get; init; } = Array.Empty<AutomationStep>();
    }

    private sealed record AutomationStep
    {
        public string Type { get; init; } = string.Empty;
        public string? Description { get; init; }
        public string? Transport { get; init; }
        public string? Service { get; init; }
        public string? Procedure { get; init; }
        public string? Caller { get; init; }
        public string? Encoding { get; init; }
        public Dictionary<string, string>? Headers { get; init; }
        public string? ShardKey { get; init; }
        public string? RoutingKey { get; init; }
        public string? RoutingDelegate { get; init; }
        public string? Ttl { get; init; }
        public string? Deadline { get; init; }
        public string? Timeout { get; init; }
        public string? Body { get; init; }
        public string? BodyFile { get; init; }
        public string? BodyBase64 { get; init; }
        public string? Url { get; init; }
        public string? Address { get; init; }
        public string[]? Addresses { get; init; }
        public string? Format { get; init; }
        public string? Duration { get; init; }
        public string? Delay { get; init; }
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
        var polymerException = PolymerErrors.FromError(error, transport);
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

    private static void PrintMiddlewareLine(string label, IReadOnlyList<string> inbound, IReadOnlyList<string> outbound)
    {
        Console.WriteLine($"  {label}: inbound[{inbound.Count}] outbound[{outbound.Count}]");
    }

    private static bool TryParseHeaders(IEnumerable<string> values, out List<KeyValuePair<string, string>> headers)
    {
        headers = new List<KeyValuePair<string, string>>();
        foreach (var value in values)
        {
            if (!TrySplitKeyValue(value, out var key, out var parsedValue))
            {
                Console.Error.WriteLine($"Could not parse header '{value}'. Expected KEY=VALUE.");
                headers = new List<KeyValuePair<string, string>>();
                return false;
            }

            headers.Add(new KeyValuePair<string, string>(key, parsedValue));
        }

        return true;
    }

    private static bool TryResolvePayload(string? body, string? bodyFile, string? bodyBase64, out ReadOnlyMemory<byte> payload, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
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
            return true;
        }

        if (!string.IsNullOrEmpty(body))
        {
            payload = Encoding.UTF8.GetBytes(body);
            return true;
        }

        payload = ReadOnlyMemory<byte>.Empty;
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
}
