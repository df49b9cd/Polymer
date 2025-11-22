using System.Buffers;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using OmniRelay.Dispatcher;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Dispatcher introspection commands.
/// </summary>
internal static partial class ProgramIntrospectModule
{
    internal static Command CreateIntrospectCommand()
    {
        var command = new Command("introspect", "Fetch dispatcher introspection over HTTP.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Introspection endpoint to query.",
            DefaultValueFactory = _ => Program.DefaultIntrospectionUrl
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
            var url = parseResult.GetValue(urlOption) ?? Program.DefaultIntrospectionUrl;
            var format = parseResult.GetValue(formatOption) ?? "text";
            var timeout = parseResult.GetValue(timeoutOption);
            return ProgramIntrospectModule.RunIntrospectAsync(url, format, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "ConfigureAwait is applied on awaited operations; CLI does not capture context.")]
    internal static async Task<int> RunIntrospectAsync(string url, string format, string? timeoutOption)
    {
        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "text" : format.ToLowerInvariant();
        var timeout = TimeSpan.FromSeconds(10);

        if (!string.IsNullOrWhiteSpace(timeoutOption) && !Program.TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'. Use standard TimeSpan formats or suffixes like 5s/1m.").ConfigureAwait(false);
            return 1;
        }

        using var httpClient = CliRuntime.HttpClientFactory.CreateClient();
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

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var snapshot = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.DispatcherIntrospection, cts.Token).ConfigureAwait(false);
            if (snapshot is null)
            {
                await Console.Error.WriteLineAsync("Introspection response was empty.").ConfigureAwait(false);
                return 1;
            }
            snapshot = snapshot.Normalize();

            if (normalizedFormat is "json" or "raw")
            {
                var buffer = new ArrayBufferWriter<byte>();
                using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                {
                    JsonSerializer.Serialize(writer, snapshot, OmniRelayCliJsonContext.Default.DispatcherIntrospection);
                }
                Console.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
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

    internal static void PrintIntrospectionSummary(DispatcherIntrospection snapshot)
    {
        Console.WriteLine($"Service: {snapshot.Service}");
        Console.WriteLine($"Status: {snapshot.Status}");
        Console.WriteLine();

        Console.WriteLine("Procedures:");
        Program.PrintProcedureGroup("Unary", snapshot.Procedures.Unary.Select(static p => p.Name));
        Program.PrintProcedureGroup("Oneway", snapshot.Procedures.Oneway.Select(static p => p.Name));
        Program.PrintProcedureGroup("Stream", snapshot.Procedures.Stream.Select(static p => p.Name));
        Program.PrintProcedureGroup("ClientStream", snapshot.Procedures.ClientStream.Select(static p => p.Name));
        Program.PrintProcedureGroup("Duplex", snapshot.Procedures.Duplex.Select(static p => p.Name));
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
                Program.PrintOutboundGroup("    Unary", outbound.Unary);
                Program.PrintOutboundGroup("    Oneway", outbound.Oneway);
                Program.PrintOutboundGroup("    Stream", outbound.Stream);
                Program.PrintOutboundGroup("    ClientStream", outbound.ClientStream);
                Program.PrintOutboundGroup("    Duplex", outbound.Duplex);
            }
            Console.WriteLine();
        }

        Console.WriteLine("Middleware (Inbound → Outbound):");
        Program.PrintMiddlewareLine("Unary", snapshot.Middleware.InboundUnary, snapshot.Middleware.OutboundUnary);
        Program.PrintMiddlewareLine("Oneway", snapshot.Middleware.InboundOneway, snapshot.Middleware.OutboundOneway);
        Program.PrintMiddlewareLine("Stream", snapshot.Middleware.InboundStream, snapshot.Middleware.OutboundStream);
        Program.PrintMiddlewareLine("ClientStream", snapshot.Middleware.InboundClientStream, snapshot.Middleware.OutboundClientStream);
        Program.PrintMiddlewareLine("Duplex", snapshot.Middleware.InboundDuplex, snapshot.Middleware.OutboundDuplex);
    }
}
