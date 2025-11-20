using System.CommandLine;
using System.Diagnostics.CodeAnalysis;

namespace OmniRelay.Cli.Modules;

/// <summary>Serve command wiring kept explicit for NativeAOT friendliness.</summary>
internal static partial class ProgramServeModule
{
    [RequiresUnreferencedCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    [RequiresDynamicCode("OmniRelay dispatcher bootstrapping uses reflection and dynamic configuration; it is not trimming/AOT safe.")]
    internal static Command CreateServeCommand()
    {
        var command = new Command("serve", "Run an OmniRelay dispatcher using configuration files.");

        var configOption = new Option<string[]>("--config")
        {
            Description = "Configuration file(s) to load (JSON).",
            AllowMultipleArgumentsPerToken = true,
            Required = true
        };
        configOption.Aliases.Add("-c");

        var sectionOption = new Option<string>("--section")
        {
            Description = "Configuration section containing dispatcher settings.",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override configuration values (KEY=VALUE).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        var readyFileOption = new Option<string?>("--ready-file")
        {
            Description = "Write a marker file once the dispatcher has started."
        };

        var shutdownAfterOption = new Option<string?>("--shutdown-after")
        {
            Description = "Automatically stop after the specified duration (e.g. 00:05:00, 30s)."
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);
        command.Add(readyFileOption);
        command.Add(shutdownAfterOption);

        command.SetAction(async parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? Array.Empty<string>();
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? Array.Empty<string>();
            var readyFile = parseResult.GetValue(readyFileOption);
            var shutdownAfterValue = parseResult.GetValue(shutdownAfterOption);

            TimeSpan? shutdownAfter = null;
            if (!string.IsNullOrWhiteSpace(shutdownAfterValue))
            {
                if (!Program.TryParseDuration(shutdownAfterValue!, out var parsed))
                {
                    CliRuntime.Console.WriteError($"Could not parse --shutdown-after value '{shutdownAfterValue}'.");
                    return 1;
                }

                shutdownAfter = parsed;
            }

            if (!Program.TryBuildConfiguration(configs, overrides, out var configuration, out var error))
            {
                CliRuntime.Console.WriteError(error ?? "Failed to build configuration.");
                return 1;
            }

            await using var host = CliRuntime.ServeHostFactory.CreateHost(configuration, section);
            try
            {
                await host.StartAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CliRuntime.Console.WriteError($"Failed to start dispatcher: {ex.Message}");
                return 1;
            }

            if (!string.IsNullOrWhiteSpace(readyFile))
            {
                Program.TryWriteReadyFile(readyFile!);
            }

            using var shutdownCts = new CancellationTokenSource();
            ConsoleCancelEventHandler? handler = null;
            handler = (_, args) =>
            {
                shutdownCts.Cancel();
                args.Cancel = true;
            };
            Console.CancelKeyPress += handler;

            try
            {
                try
                {
                    await WaitForShutdownAsync(shutdownAfter, shutdownCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown triggered by Ctrl+C or cancellation token.
                }
            }
            finally
            {
                Console.CancelKeyPress -= handler;
            }

            try
            {
                await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                CliRuntime.Console.WriteError($"Failed to stop dispatcher: {ex.Message}");
                return 1;
            }

            return 0;
        });

        return command;
    }

    private static Task WaitForShutdownAsync(TimeSpan? shutdownAfter, CancellationToken cancellationToken)
    {
        if (shutdownAfter.HasValue && shutdownAfter.Value > TimeSpan.Zero)
        {
            return Task.Delay(shutdownAfter.Value, cancellationToken);
        }

        return Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
