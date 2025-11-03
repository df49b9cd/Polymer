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
using Hugo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Polymer.Configuration;
using Polymer.Core;
using Polymer.Core.Transport;
using Polymer.Errors;
using Polymer.Dispatcher;
using Polymer.Transport.Grpc;
using Polymer.Transport.Http;

namespace Polymer.Cli;

public static class Program
{
    private const string DefaultConfigSection = "polymer";
    private const string DefaultIntrospectionUrl = "http://127.0.0.1:8080/polymer/introspect";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintRootHelp();
            return 0;
        }

        var command = args[0];
        if (IsHelpToken(command))
        {
            PrintRootHelp();
            return 0;
        }

        var remainder = args.Skip(1).ToArray();

        try
        {
            return command.ToLowerInvariant() switch
            {
                "config" => await RunConfigAsync(remainder).ConfigureAwait(false),
                "introspect" => await RunIntrospectAsync(remainder).ConfigureAwait(false),
                "request" => await RunRequestAsync(remainder).ConfigureAwait(false),
                "help" => PrintRootHelpAndReturn(),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 2;
        }
    }

    private static async Task<int> RunConfigAsync(string[] args)
    {
        if (args.Length == 0 || IsHelpToken(args[0]))
        {
            PrintConfigHelp();
            return 0;
        }

        var subcommand = args[0].ToLowerInvariant();
        var tail = args.Skip(1).ToArray();

        switch (subcommand)
        {
            case "validate":
                return await RunConfigValidateAsync(new CommandArguments(tail)).ConfigureAwait(false);
            case "help":
                PrintConfigHelp();
                return 0;
            default:
                Console.Error.WriteLine($"Unknown config subcommand '{args[0]}'.");
                PrintConfigHelp();
                return 1;
        }
    }

    private static async Task<int> RunConfigValidateAsync(CommandArguments arguments)
    {
        if (arguments.HelpRequested)
        {
            PrintConfigValidateHelp();
            return 0;
        }

        var configPaths = arguments.GetOptionValues("config");
        if (configPaths.Count == 0)
        {
            Console.Error.WriteLine("No configuration files supplied. Use --config <path> (repeat for layering).");
            return 1;
        }

        var section = arguments.GetOptionValue("section") ?? DefaultConfigSection;
        var setOverrides = arguments.GetOptionValues("set");

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

        if (setOverrides.Count > 0)
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
            services.AddPolymerDispatcher(configuration.GetSection(section));
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

    private static async Task<int> RunIntrospectAsync(string[] args)
    {
        var arguments = new CommandArguments(args);
        if (arguments.HelpRequested)
        {
            PrintIntrospectHelp();
            return 0;
        }

        if (arguments.Positionals.Count > 0)
        {
            Console.Error.WriteLine($"Unexpected argument '{arguments.Positionals[0]}'.");
            PrintIntrospectHelp();
            return 1;
        }

        var url = arguments.GetOptionValue("url") ?? DefaultIntrospectionUrl;
        var format = (arguments.GetOptionValue("format") ?? "text").ToLowerInvariant();
        var timeoutOption = arguments.GetOptionValue("timeout");
        var requestTimeout = TimeSpan.FromSeconds(10);

        if (timeoutOption is not null)
        {
            if (!TryParseDuration(timeoutOption, out requestTimeout))
            {
                Console.Error.WriteLine($"Could not parse timeout '{timeoutOption}'. Use standard TimeSpan formats or suffixes like 5s/1m.");
                return 1;
            }
        }

        using var httpClient = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        using var cts = new CancellationTokenSource(requestTimeout);

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

            if (format is "json" or "raw")
            {
                var outputOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true
                };
                outputOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
                var json = JsonSerializer.Serialize(snapshot, outputOptions);
                Console.WriteLine(json);
            }
            else if (format is "text" or "summary")
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

    private static async Task<int> RunRequestAsync(string[] args)
    {
        var arguments = new CommandArguments(args);
        if (arguments.HelpRequested)
        {
            PrintRequestHelp();
            return 0;
        }

        if (arguments.Positionals.Count > 0)
        {
            Console.Error.WriteLine($"Unexpected argument '{arguments.Positionals[0]}'.");
            PrintRequestHelp();
            return 1;
        }

        var transport = (arguments.GetOptionValue("transport") ?? "http").ToLowerInvariant();
        var service = arguments.GetOptionValue("service");
        if (string.IsNullOrWhiteSpace(service))
        {
            Console.Error.WriteLine("Missing required --service option.");
            return 1;
        }

        var procedure = arguments.GetOptionValue("procedure");
        if (string.IsNullOrWhiteSpace(procedure))
        {
            Console.Error.WriteLine("Missing required --procedure option.");
            return 1;
        }

        var caller = arguments.GetOptionValue("caller");
        var encoding = arguments.GetOptionValue("encoding");
        var shardKey = arguments.GetOptionValue("shard-key");
        var routingKey = arguments.GetOptionValue("routing-key");
        var routingDelegate = arguments.GetOptionValue("routing-delegate");
        var ttlOption = arguments.GetOptionValue("ttl");
        var deadlineOption = arguments.GetOptionValue("deadline");
        var timeoutOption = arguments.GetOptionValue("timeout");

        TimeSpan? ttl = null;
        if (ttlOption is not null)
        {
            if (!TryParseDuration(ttlOption, out var parsedTtl))
            {
                Console.Error.WriteLine($"Could not parse TTL '{ttlOption}'. Use formats like '00:00:05' or suffixes (e.g. 5s, 1m).");
                return 1;
            }

            ttl = parsedTtl;
        }

        DateTimeOffset? deadline = null;
        if (deadlineOption is not null)
        {
            if (!DateTimeOffset.TryParse(deadlineOption, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsedDeadline))
            {
                Console.Error.WriteLine($"Could not parse deadline '{deadlineOption}'. Provide an ISO-8601 timestamp.");
                return 1;
            }

            deadline = parsedDeadline;
        }

        TimeSpan? timeout = null;
        if (timeoutOption is not null)
        {
            if (!TryParseDuration(timeoutOption, out var parsedTimeout))
            {
                Console.Error.WriteLine($"Could not parse timeout '{timeoutOption}'.");
                return 1;
            }

            timeout = parsedTimeout;
        }

        if (!TryParseHeaders(arguments.GetOptionValues("header"), out var headers))
        {
            return 1;
        }

        if (!TryResolvePayload(arguments, out var payload, out var payloadError))
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
            headers: headers);

        var request = new Request<ReadOnlyMemory<byte>>(meta, payload);
        using var cts = timeout.HasValue && timeout.Value > TimeSpan.Zero
            ? new CancellationTokenSource(timeout.Value)
            : new CancellationTokenSource(TimeSpan.FromSeconds(30));

        switch (transport)
        {
            case "http":
                return await ExecuteHttpRequestAsync(arguments, request, cts.Token).ConfigureAwait(false);
            case "grpc":
                return await ExecuteGrpcRequestAsync(arguments, request, service, cts.Token).ConfigureAwait(false);
            default:
                Console.Error.WriteLine($"Unsupported transport '{transport}'. Use 'http' or 'grpc'.");
                return 1;
        }
    }

    private static async Task<int> ExecuteHttpRequestAsync(CommandArguments arguments, Request<ReadOnlyMemory<byte>> request, CancellationToken cancellationToken)
    {
        var url = arguments.GetOptionValue("url");
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

    private static async Task<int> ExecuteGrpcRequestAsync(CommandArguments arguments, Request<ReadOnlyMemory<byte>> request, string remoteService, CancellationToken cancellationToken)
    {
        var addressValues = arguments.GetOptionValues("address");
        if (addressValues.Count == 0)
        {
            Console.Error.WriteLine("gRPC transport requires at least one --address option (e.g. --address http://127.0.0.1:9090).");
            return 1;
        }

        var uris = new List<Uri>(addressValues.Count);
        foreach (var address in addressValues)
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

    private static bool TryParseHeaders(IReadOnlyList<string> values, out List<KeyValuePair<string, string>> headers)
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

    private static bool TryResolvePayload(CommandArguments arguments, out ReadOnlyMemory<byte> payload, out string? error)
    {
        payload = ReadOnlyMemory<byte>.Empty;
        error = null;

        var bodyValues = arguments.GetOptionValues("body");
        var bodyFileValues = arguments.GetOptionValues("body-file");
        var bodyBase64Values = arguments.GetOptionValues("body-base64");

        var sources = new[]
        {
            (Name: "--body", Count: bodyValues.Count),
            (Name: "--body-file", Count: bodyFileValues.Count),
            (Name: "--body-base64", Count: bodyBase64Values.Count)
        };

        var activeSources = sources.Where(static s => s.Count > 0).ToList();
        if (activeSources.Count > 1)
        {
            error = "Specify only one of --body, --body-file, or --body-base64.";
            return false;
        }

        if (bodyBase64Values.Count > 0)
        {
            try
            {
                payload = Convert.FromBase64String(bodyBase64Values[^1]);
                return true;
            }
            catch (FormatException ex)
            {
                error = $"Failed to decode base64 payload: {ex.Message}";
                return false;
            }
        }

        if (bodyFileValues.Count > 0)
        {
            var path = bodyFileValues[^1];
            if (!File.Exists(path))
            {
                error = $"Payload file '{path}' does not exist.";
                return false;
            }

            payload = File.ReadAllBytes(path);
            return true;
        }

        if (bodyValues.Count > 0)
        {
            payload = Encoding.UTF8.GetBytes(bodyValues[^1]);
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

        value = value.Trim();
        if (value.Length < 2)
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
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var numberPart = value[..^suffix.Length];
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

    private static bool IsHelpToken(string value) =>
        string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "-?", StringComparison.OrdinalIgnoreCase);

    private static int PrintRootHelpAndReturn()
    {
        PrintRootHelp();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintRootHelp();
        return 1;
    }

    private static void PrintRootHelp()
    {
        Console.WriteLine("Polymer CLI");
        Console.WriteLine();
        Console.WriteLine("Usage: polymer <command> [options]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  config validate   Validate a Polymer configuration file");
        Console.WriteLine("  introspect        Fetch dispatcher introspection over HTTP");
        Console.WriteLine("  request           Issue an RPC request over HTTP or gRPC");
        Console.WriteLine();
        Console.WriteLine("Run 'polymer <command> --help' for command-specific options.");
    }

    private static void PrintConfigHelp()
    {
        Console.WriteLine("Polymer CLI - config");
        Console.WriteLine("Usage: polymer config validate [options]");
        Console.WriteLine();
        PrintConfigValidateHelp();
    }

    private static void PrintConfigValidateHelp()
    {
        Console.WriteLine("Options:");
        Console.WriteLine("  --config <path>      Configuration file to load (repeat for layering)");
        Console.WriteLine("  --set key=value      Override keys in-memory (repeatable)");
        Console.WriteLine("  --section <name>     Root configuration section (default 'polymer')");
        Console.WriteLine();
        Console.WriteLine("Example:");
        Console.WriteLine("  polymer config validate --config appsettings.json --config appsettings.Development.json \\");
        Console.WriteLine("       --set polymer:outbounds:ledger:unary:http:0:url=http://127.0.0.1:8081");
    }

    private static void PrintIntrospectHelp()
    {
        Console.WriteLine("Polymer CLI - introspect");
        Console.WriteLine("Usage: polymer introspect [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --url <url>          Introspection endpoint (default http://127.0.0.1:8080/polymer/introspect)");
        Console.WriteLine("  --format <format>    Output format: text|json (default text)");
        Console.WriteLine("  --timeout <duration> Request timeout (e.g. 5s, 00:00:05)");
    }

    private static void PrintRequestHelp()
    {
        Console.WriteLine("Polymer CLI - request");
        Console.WriteLine("Usage: polymer request [options]");
        Console.WriteLine();
        Console.WriteLine("Required:");
        Console.WriteLine("  --service <name>     Remote service name");
        Console.WriteLine("  --procedure <name>   Procedure or method name");
        Console.WriteLine();
        Console.WriteLine("Transport options:");
        Console.WriteLine("  --transport <kind>   http|grpc (default http)");
        Console.WriteLine("  --url <url>          HTTP endpoint (required for http)");
        Console.WriteLine("  --address <url>      gRPC address (repeatable, required for grpc)");
        Console.WriteLine();
        Console.WriteLine("Payload options:");
        Console.WriteLine("  --body <text>        Inline UTF-8 body");
        Console.WriteLine("  --body-file <path>   Read payload bytes from file");
        Console.WriteLine("  --body-base64 <data> Base64 encoded payload");
        Console.WriteLine();
        Console.WriteLine("Metadata:");
        Console.WriteLine("  --encoding <name>    Payload encoding (e.g. application/json)");
        Console.WriteLine("  --caller <name>      Caller identifier");
        Console.WriteLine("  --header k=v         Attach header (repeatable)");
        Console.WriteLine("  --ttl <duration>     Request time-to-live (e.g. 5s)");
        Console.WriteLine("  --deadline <ts>      Absolute deadline (ISO-8601)");
        Console.WriteLine("  --timeout <duration> Overall call timeout (default 30s)");
    }

    private sealed class CommandArguments
    {
        private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _positionals = new();

        public CommandArguments(IEnumerable<string> args)
        {
            var array = args as string[] ?? args.ToArray();
            for (var index = 0; index < array.Length; index++)
            {
                var token = array[index];

                if (IsHelpToken(token))
                {
                    HelpRequested = true;
                    continue;
                }

                if (token.StartsWith("--", StringComparison.Ordinal))
                {
                    var equals = token.IndexOf('=');
                    if (equals > 2)
                    {
                        var name = token[2..equals];
                        var value = token[(equals + 1)..];
                        AddOption(name, value);
                    }
                    else
                    {
                        var name = token[2..];
                        if (index + 1 < array.Length && !array[index + 1].StartsWith("-", StringComparison.Ordinal))
                        {
                            AddOption(name, array[++index]);
                        }
                        else
                        {
                            AddOption(name, "true");
                        }
                    }

                    continue;
                }

                if (token.StartsWith("-", StringComparison.Ordinal) && token.Length > 1)
                {
                    if (string.Equals(token, "-h", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "-?", StringComparison.OrdinalIgnoreCase))
                    {
                        HelpRequested = true;
                        continue;
                    }

                    _positionals.Add(token);
                    continue;
                }

                _positionals.Add(token);
            }
        }

        public bool HelpRequested { get; }

        public IReadOnlyList<string> Positionals => _positionals;

        public IReadOnlyList<string> GetOptionValues(string name) =>
            _options.TryGetValue(name, out var values) ? values : Array.Empty<string>();

        public string? GetOptionValue(string name)
        {
            var values = GetOptionValues(name);
            return values.Count switch
            {
                0 => null,
                1 when values[0].Length == 0 => null,
                _ => values[^1]
            };
        }

        private void AddOption(string name, string value)
        {
            if (!_options.TryGetValue(name, out var list))
            {
                list = new List<string>();
                _options[name] = list;
            }

            list.Add(value);
        }
    }
}
