using System.CommandLine;
using System.Text.Json;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Script automation commands.
/// </summary>
internal static partial class ProgramScriptModule
{
    // Command builder
    internal static Command CreateScriptCommand()
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
            return ProgramScriptModule.RunAutomationAsync(file, dryRun, continueOnError).GetAwaiter().GetResult();
        });

        command.Add(runCommand);
        return command;
    }

    internal static async Task<int> RunAutomationAsync(string scriptPath, bool dryRun, bool continueOnError)
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
            var bytes = await File.ReadAllBytesAsync(scriptPath).ConfigureAwait(false);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            script = JsonSerializer.Deserialize(ref reader, OmniRelayCliJsonContext.Default.AutomationScript);
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
            var description = string.IsNullOrWhiteSpace(step.Description) ? string.Empty : $" - {step.Description}";
            Console.WriteLine($"[{index + 1}/{script.Steps.Length}] {typeLabel}{description}");

            var normalizedType = step.Type?.Trim().ToLowerInvariant() ?? string.Empty;
            switch (normalizedType)
            {
                case "request":
                    if (string.IsNullOrWhiteSpace(step.Service) || string.IsNullOrWhiteSpace(step.Procedure))
                    {
                        await Console.Error.WriteLineAsync("  Request step is missing 'service' or 'procedure'.").ConfigureAwait(false);
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    var headerPairs = step.Headers?.Select(static kvp => $"{kvp.Key}={kvp.Value}").ToArray() ?? [];
                    var profiles = step.Profiles ?? [];
                    var addresses = step.Addresses?.Where(static address => !string.IsNullOrWhiteSpace(address)).ToArray() ?? [];
                    if (addresses.Length == 0 && !string.IsNullOrWhiteSpace(step.Address))
                    {
                        addresses = [step.Address];
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

                    var requestResult = await Program.RunRequestAsync(
                        step.Transport ?? "http",
                        step.Service,
                        step.Procedure,
                        step.Caller,
                        step.Encoding,
                        headerPairs,
                        profiles,
                        step.ShardKey,
                        step.RoutingKey,
                        step.RoutingDelegate,
                        step.ProtoFiles ?? Array.Empty<string>(),
                        step.ProtoMessage,
                        step.Ttl,
                        step.Deadline,
                        step.Timeout,
                        step.Body,
                        step.BodyFile,
                        step.BodyBase64,
                        step.Url,
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
                    var targetUrl = string.IsNullOrWhiteSpace(step.Url) ? Program.DefaultIntrospectionUrl : step.Url!;
                    Console.WriteLine($"  -> GET {targetUrl} (format={step.Format ?? "text"})");

                    if (dryRun)
                    {
                        Console.WriteLine("  dry-run: skipping introspection call.");
                        continue;
                    }

                    var introspectResult = await ProgramIntrospectModule.RunIntrospectAsync(targetUrl, step.Format ?? "text", step.Timeout).ConfigureAwait(false);
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
                        await Console.Error.WriteLineAsync("  Delay step requires a 'duration' or 'delay' value.").ConfigureAwait(false);
                        exitCode = exitCode == 0 ? 1 : exitCode;
                        if (!continueOnError)
                        {
                            return exitCode;
                        }
                        continue;
                    }

                    if (!Program.TryParseDuration(delayValue!, out var delay))
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
}
