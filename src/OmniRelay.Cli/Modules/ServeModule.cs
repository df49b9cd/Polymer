using System.CommandLine;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Hosts the 'serve' command and dispatcher bootstrap logic.
/// </summary>
internal static partial class ProgramServeModule
{
    // Command builder
    internal static Command CreateServeCommand()
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
            DefaultValueFactory = _ => Program.DefaultConfigSection
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
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? [];
            var readyFile = parseResult.GetValue(readyFileOption);
            var shutdownAfter = parseResult.GetValue(shutdownAfterOption);
            return ProgramServeModule.RunServeAsync(configs, section, overrides, readyFile, shutdownAfter).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static async Task<int> RunServeAsync(string[] configPaths, string section, string[] setOverrides, string? readyFile, string? shutdownAfterOption)
    {
        if (!Program.TryBuildConfiguration(configPaths, setOverrides, out var configuration, out var errorMessage))
        {
            await Console.Error.WriteLineAsync(errorMessage ?? "Failed to load configuration.").ConfigureAwait(false);
            return 1;
        }

        TimeSpan? shutdownAfter = null;
        if (!string.IsNullOrWhiteSpace(shutdownAfterOption))
        {
            if (!Program.TryParseDuration(shutdownAfterOption!, out var parsed))
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

        var resolvedSection = string.IsNullOrWhiteSpace(section) ? Program.DefaultConfigSection : section;
        IServeHost host;
        try
        {
            host = CliRuntime.ServeHostFactory.CreateHost(configuration, resolvedSection);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Failed to configure dispatcher: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        await using (host.ConfigureAwait(false))
        {
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
                var dispatcher = host.Dispatcher;
                var serviceName = dispatcher?.ServiceName ?? resolvedSection;
                Console.WriteLine($"OmniRelay dispatcher '{serviceName}' started.");

                if (!string.IsNullOrWhiteSpace(readyFile))
                {
                    Program.TryWriteReadyFile(readyFile!);
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
    }
}
