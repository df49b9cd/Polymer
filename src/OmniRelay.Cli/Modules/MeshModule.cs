#pragma warning disable IDE0005
using System.Buffers;
using System.CommandLine;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Cli.Core;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Clients;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core;
using OmniRelay.Dispatcher.Config;
using OmniRelay.Mesh.Control.V1;
using OmniRelay.Transport.Grpc;
using static OmniRelay.Cli.Program;
using CoreLeadershipEventKind = OmniRelay.Core.Leadership.LeadershipEventKind;
using DomainShardStatus = OmniRelay.Core.Shards.ShardStatus;
using ProtoLeadershipEvent = OmniRelay.Mesh.Control.V1.LeadershipEvent;
using ProtoLeadershipEventKind = OmniRelay.Mesh.Control.V1.LeadershipEventKind;
using ShardControl = OmniRelay.Core.Shards.ControlPlane;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Mesh control-plane tooling commands and handlers.
/// </summary>
internal static partial class ProgramMeshModule
{
    internal const string DefaultControlPlaneUrl = "http://127.0.0.1:8080";
    internal const string MeshScopeHeader = "x-mesh-scope";
    internal const string MeshReadScope = "mesh.read";
    internal const string MeshOperateScope = "mesh.operate";

    private static readonly JsonWriterOptions PrettyWriterOptions = new() { Indented = true };

    internal enum MeshPeersOutputFormat
    {
        Table,
        Json
    }

    internal static Command CreateMeshCommand()
    {
        var command = new Command("mesh", "Mesh control-plane tooling.")
        {
            CreateMeshLeadersCommand(),
            CreateMeshPeersCommand(),
            CreateMeshUpgradeCommand(),
            CreateMeshBootstrapCommand(),
            CreateMeshShardsCommand(),
            CreateMeshConfigCommand()
        };
        return command;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "CLI validation runs in non-trimmed host; reflection usage is acceptable.")]
    internal static Command CreateMeshConfigCommand()
    {
        var command = new Command("config", "Mesh configuration and transport policy tooling.")
        {
            CreateMeshConfigValidateCommand()
        };

        return command;
    }

    [RequiresUnreferencedCode("Config validation uses ConfigurationBinder.Bind which is not trimming-safe.")]
    internal static Command CreateMeshConfigValidateCommand()
    {
        var command = new Command("validate", "Validate transports/encodings against the mesh transport policy.");

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
            Description = "Root configuration section (defaults to omnirelay).",
            DefaultValueFactory = _ => Program.DefaultConfigSection
        };

        var setOption = new Option<string[]>("--set")
        {
            Description = "Override configuration values (KEY=VALUE).",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => Array.Empty<string>()
        };

        var formatOption = new Option<string>("--format")
        {
            Description = "Output format (text|json).",
            DefaultValueFactory = _ => "text"
        };

        command.Add(configOption);
        command.Add(sectionOption);
        command.Add(setOption);
        command.Add(formatOption);

        command.SetAction(parseResult =>
        {
            var configs = parseResult.GetValue(configOption) ?? Array.Empty<string>();
            var section = parseResult.GetValue(sectionOption) ?? Program.DefaultConfigSection;
            var overrides = parseResult.GetValue(setOption) ?? Array.Empty<string>();
            var format = parseResult.GetValue(formatOption) ?? "text";
            return RunMeshConfigValidateAsync(configs, section, overrides, format).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshLeadersCommand()
    {
        var command = new Command("leaders", "Mesh leadership operations.")
        {
            CreateMeshLeadersStatusCommand()
        };
        return command;
    }

    internal static Command CreateMeshPeersCommand()
    {
        var command = new Command("peers", "Mesh membership diagnostics.")
        {
            CreateMeshPeersListCommand()
        };

        return command;
    }

    internal static Command CreateMeshUpgradeCommand()
    {
        var command = new Command("upgrade", "Node upgrade and drain orchestration.")
        {
            CreateMeshUpgradeStatusCommand(),
            CreateMeshUpgradeDrainCommand(),
            CreateMeshUpgradeResumeCommand()
        };

        return command;
    }

    internal static Command CreateMeshBootstrapCommand()
    {
        var command = new Command("bootstrap", "Bootstrap token and join tooling.")
        {
            CreateMeshBootstrapIssueCommand(),
            CreateMeshBootstrapJoinCommand()
        };
        return command;
    }

    internal static Command CreateMeshShardsCommand()
    {
        var command = new Command("shards", "Shard ownership APIs.")
        {
            CreateMeshShardsListCommand(),
            CreateMeshShardsDiffCommand(),
            CreateMeshShardsSimulateCommand()
        };

        return command;
    }

    internal static Command CreateMeshShardsListCommand()
    {
        var command = new Command("list", "List shard ownership records.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };

        var namespaceOption = new Option<string?>("--namespace") { Description = "Filter by namespace id." };
        var ownerOption = new Option<string?>("--owner") { Description = "Filter by owner node id." };
        var statusOption = new Option<string[]>("--status") { Description = "Filter by shard status (repeat for multiple)." };
        var searchOption = new Option<string?>("--search") { Description = "Filter shard id substring." };
        var cursorOption = new Option<string?>("--cursor") { Description = "Resume cursor token." };
        var pageSizeOption = new Option<int?>("--page-size") { Description = "Page size (default 100)." };
        var jsonOption = new Option<bool>("--json") { Description = "Emit JSON instead of tables." };

        command.Add(urlOption);
        command.Add(namespaceOption);
        command.Add(ownerOption);
        command.Add(statusOption);
        command.Add(searchOption);
        command.Add(cursorOption);
        command.Add(pageSizeOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var ns = parseResult.GetValue(namespaceOption);
            var owner = parseResult.GetValue(ownerOption);
            var statuses = parseResult.GetValue(statusOption) ?? Array.Empty<string>();
            var search = parseResult.GetValue(searchOption);
            var cursor = parseResult.GetValue(cursorOption);
            var pageSize = parseResult.GetValue(pageSizeOption);
            var asJson = parseResult.GetValue(jsonOption);
            return RunMeshShardsListAsync(url, ns, owner, statuses, search, cursor, pageSize, asJson).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshShardsDiffCommand()
    {
        var command = new Command("diff", "Fetch shard diff stream between resume tokens.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL.",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };

        var namespaceOption = new Option<string?>("--namespace") { Description = "Filter by namespace id." };
        var ownerOption = new Option<string?>("--owner") { Description = "Filter by owner node id." };
        var statusOption = new Option<string[]>("--status") { Description = "Filter by shard status." };
        var searchOption = new Option<string?>("--search") { Description = "Filter shard id substring." };
        var fromOption = new Option<long?>("--from-version") { Description = "Starting resume token." };
        var toOption = new Option<long?>("--to-version") { Description = "Ending resume token." };
        var jsonOption = new Option<bool>("--json") { Description = "Emit JSON instead of tables." };

        command.Add(urlOption);
        command.Add(namespaceOption);
        command.Add(ownerOption);
        command.Add(statusOption);
        command.Add(searchOption);
        command.Add(fromOption);
        command.Add(toOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var ns = parseResult.GetValue(namespaceOption);
            var owner = parseResult.GetValue(ownerOption);
            var statuses = parseResult.GetValue(statusOption) ?? Array.Empty<string>();
            var search = parseResult.GetValue(searchOption);
            var from = parseResult.GetValue(fromOption);
            var to = parseResult.GetValue(toOption);
            var asJson = parseResult.GetValue(jsonOption);
            return RunMeshShardsDiffAsync(url, ns, owner, statuses, search, from, to, asJson).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshShardsSimulateCommand()
    {
        var command = new Command("simulate", "Run shard simulation with custom node set.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL.",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };

        var namespaceOption = new Option<string>("--namespace")
        {
            Description = "Namespace to simulate."
        };

        var strategyOption = new Option<string?>("--strategy") { Description = "Override shard strategy id." };
        var nodeOption = new Option<string[]>("--node")
        {
            Description = "Simulation node descriptor (nodeId[:weight]). Repeat for multiple."
        };
        var jsonOption = new Option<bool>("--json") { Description = "Emit JSON instead of tables." };

        command.Add(urlOption);
        command.Add(namespaceOption);
        command.Add(strategyOption);
        command.Add(nodeOption);
        command.Add(jsonOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var ns = parseResult.GetValue(namespaceOption) ?? string.Empty;
            var strategy = parseResult.GetValue(strategyOption);
            var nodes = parseResult.GetValue(nodeOption) ?? Array.Empty<string>();
            var asJson = parseResult.GetValue(jsonOption);
            return RunMeshShardsSimulateAsync(url, ns, strategy, nodes, asJson).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshUpgradeStatusCommand()
    {
        var command = new Command("status", "Show node drain state.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };
        urlOption.Aliases.Add("-u");

        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit JSON instead of plain text."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 10s, 1m)."
        };

        command.Add(urlOption);
        command.Add(jsonOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var asJson = parseResult.GetValue(jsonOption);
            var timeout = parseResult.GetValue(timeoutOption);
            return RunMeshUpgradeStatusAsync(url, asJson, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshUpgradeDrainCommand()
    {
        var command = new Command("drain", "Begin draining the node for an upgrade.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };
        urlOption.Aliases.Add("-u");

        var reasonOption = new Option<string?>("--reason")
        {
            Description = "Optional reason to record with the drain request."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 10s, 1m)."
        };

        command.Add(urlOption);
        command.Add(reasonOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var reason = parseResult.GetValue(reasonOption);
            var timeout = parseResult.GetValue(timeoutOption);
            return RunMeshUpgradeDrainAsync(url, reason, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshUpgradeResumeCommand()
    {
        var command = new Command("resume", "Resume serving traffic after a drain.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };
        urlOption.Aliases.Add("-u");

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 10s, 1m)."
        };

        command.Add(urlOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var timeout = parseResult.GetValue(timeoutOption);
            return RunMeshUpgradeResumeAsync(url, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshBootstrapIssueCommand()
    {
        var command = new Command("issue-token", "Generate a bootstrap join token.");

        var signingKeyOption = new Option<string>("--signing-key")
        {
            Description = "Signing key used for HMAC tokens.",
            Arity = ArgumentArity.ExactlyOne
        };
        var clusterOption = new Option<string>("--cluster") { Description = "Cluster identifier for the token." };
        var roleOption = new Option<string>("--role") { Description = "Role assigned to the joining node." };
        var lifetimeOption = new Option<string?>("--lifetime") { Description = "Token lifetime (e.g. 1h, 30m). Defaults to 1h." };
        var maxUsesOption = new Option<int?>("--max-uses") { Description = "Maximum number of times the token can be consumed." };
        var issuerOption = new Option<string?>("--issuer") { Description = "Token issuer (defaults to omnirelay-cli)." };

        command.Add(signingKeyOption);
        command.Add(clusterOption);
        command.Add(roleOption);
        command.Add(lifetimeOption);
        command.Add(maxUsesOption);
        command.Add(issuerOption);

        command.SetAction(parseResult =>
        {
            var signingKey = parseResult.GetValue(signingKeyOption) ?? string.Empty;
            var cluster = parseResult.GetValue(clusterOption) ?? "default";
            var role = parseResult.GetValue(roleOption) ?? "worker";
            var lifetime = parseResult.GetValue(lifetimeOption);
            var maxUses = parseResult.GetValue(maxUsesOption);
            var issuer = parseResult.GetValue(issuerOption) ?? "omnirelay-cli";
            return RunMeshBootstrapIssueToken(signingKey, cluster, role, lifetime, maxUses, issuer);
        });

        return command;
    }

    internal static Command CreateMeshBootstrapJoinCommand()
    {
        var command = new Command("join", "Request a bootstrap bundle using a join token.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Bootstrap server base URL (e.g. https://127.0.0.1:9443).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };
        urlOption.Aliases.Add("-u");

        var tokenOption = new Option<string>("--token")
        {
            Description = "Join token issued by the bootstrap service.",
            Arity = ArgumentArity.ExactlyOne
        };
        var outputOption = new Option<string?>("--output") { Description = "Optional path to write the bootstrap bundle (JSON)." };
        var timeoutOption = new Option<string?>("--timeout") { Description = "Request timeout (e.g. 30s, 1m)." };

        command.Add(urlOption);
        command.Add(tokenOption);
        command.Add(outputOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var token = parseResult.GetValue(tokenOption) ?? string.Empty;
            var output = parseResult.GetValue(outputOption);
            var timeout = parseResult.GetValue(timeoutOption);
            return RunMeshBootstrapJoinAsync(url, token, output, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshPeersListCommand()
    {
        var command = new Command("list", "List gossip peers from the control plane.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080)."
        };
        urlOption.Aliases.Add("-u");

        var formatOption = new Option<MeshPeersOutputFormat>("--format")
        {
            Description = "Output format (table or json)."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 10s, 1m)."
        };

        command.Add(urlOption);
        command.Add(formatOption);
        command.Add(timeoutOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var format = parseResult.GetValue(formatOption);
            var timeout = parseResult.GetValue(timeoutOption);
            return RunMeshPeersListAsync(url, format, timeout).GetAwaiter().GetResult();
        });

        return command;
    }

    internal static Command CreateMeshLeadersStatusCommand()
    {
        var command = new Command("status", "Show current leadership tokens or stream leadership events.");

        var urlOption = new Option<string>("--url")
        {
            Description = "Base control-plane URL (e.g. http://127.0.0.1:8080).",
            DefaultValueFactory = _ => DefaultControlPlaneUrl
        };
        urlOption.Aliases.Add("-u");

        var scopeOption = new Option<string?>("--scope")
        {
            Description = "Scope filter (global-control, shard/namespace/id, etc.)."
        };

        var watchOption = new Option<bool>("--watch")
        {
            Description = "Stream leadership changes via SSE."
        };

        var timeoutOption = new Option<string?>("--timeout")
        {
            Description = "Request timeout (e.g. 10s, 1m)."
        };

        var grpcUrlOption = new Option<string?>("--grpc-url")
        {
            Description = "Optional gRPC control-plane URL for leadership streaming (e.g. http://127.0.0.1:9090)."
        };

        command.Add(urlOption);
        command.Add(scopeOption);
        command.Add(watchOption);
        command.Add(timeoutOption);
        command.Add(grpcUrlOption);

        command.SetAction(parseResult =>
        {
            var url = parseResult.GetValue(urlOption) ?? DefaultControlPlaneUrl;
            var scope = parseResult.GetValue(scopeOption);
            var watch = parseResult.GetValue(watchOption);
            var timeout = parseResult.GetValue(timeoutOption);
            var grpcUrl = parseResult.GetValue(grpcUrlOption);
            return RunMeshLeadersStatusAsync(url, scope, watch, timeout, grpcUrl).GetAwaiter().GetResult();
        });

        return command;
    }

    [RequiresUnreferencedCode("Calls Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(Object)")]
    internal static async Task<int> RunMeshConfigValidateAsync(
        string[] configPaths,
        string section,
        string[] setOverrides,
        string format)
    {
        if (!Program.TryBuildConfiguration(configPaths, setOverrides, out var configuration, out var errorMessage))
        {
            await Console.Error.WriteLineAsync(errorMessage ?? "Failed to load configuration.").ConfigureAwait(false);
            return 1;
        }

        var resolvedSection = string.IsNullOrWhiteSpace(section) ? Program.DefaultConfigSection : section;
        var options = new OmniRelayConfigurationOptions();
        configuration.GetSection(resolvedSection).Bind(options);

        TransportPolicyEvaluationResult evaluation;
        try
        {
            evaluation = TransportPolicyEvaluator.Evaluate(options);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"Transport policy evaluation failed: {ex.Message}").ConfigureAwait(false);
            return 1;
        }

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "text" : format.Trim().ToLowerInvariant();
        if (normalizedFormat is not ("text" or "json"))
        {
            await Console.Error.WriteLineAsync($"Unsupported --format '{format}'. Use 'text' or 'json'.").ConfigureAwait(false);
            return 1;
        }

        if (normalizedFormat == "json")
        {
            var report = MeshConfigValidationReport.From(resolvedSection, evaluation);
            var json = JsonSerializer.Serialize(report, OmniRelayCliJsonContext.Default.MeshConfigValidationReport);
            Console.WriteLine(json);
        }
        else
        {
            EmitPolicyReport(resolvedSection, evaluation);
        }

        if (evaluation.HasViolations)
        {
            await Console.Error.WriteLineAsync("Transport policy violations detected. Update configuration or register an approved exception.").ConfigureAwait(false);
            return 1;
        }

        return 0;
    }

    private static void EmitPolicyReport(string section, TransportPolicyEvaluationResult evaluation)
    {
        Console.WriteLine($"Transport policy evaluation for section '{section}':");
        if (evaluation.Findings.Count == 0)
        {
            Console.WriteLine("  No control-plane or diagnostics endpoints configured.");
            Console.WriteLine("Summary: total=0, compliant=0, exceptions=0, violations=0");
            return;
        }

        foreach (var finding in evaluation.Findings)
        {
            var statusLabel = finding.Status switch
            {
                TransportPolicyFindingStatus.Compliant => "OK",
                TransportPolicyFindingStatus.Excepted => "EXCEPT",
                _ => "VIOLATION"
            };

            Console.WriteLine($"  [{statusLabel}] {finding.Endpoint} ({finding.Category}) -> {finding.Transport}/{finding.Encoding}");
            Console.WriteLine($"      {finding.Message}");
            var negotiatedProtocol = finding.Http3Enabled ? "http3" : "http2-fallback";
            Console.WriteLine($"      Negotiated protocol: {negotiatedProtocol}");
            if (!string.IsNullOrWhiteSpace(finding.Hint))
            {
                Console.WriteLine($"      Hint: {finding.Hint}");
            }

            if (finding.ExceptionName is not null)
            {
                var reason = string.IsNullOrWhiteSpace(finding.ExceptionReason) ? "no reason provided" : finding.ExceptionReason;
                var expires = finding.ExceptionExpiresAfter is { } expiry
                    ? $"expires {expiry:O}"
                    : "no expiration";
                Console.WriteLine($"      Exception '{finding.ExceptionName}' ({reason}, {expires})");
            }
        }

        var summary = evaluation.Summary;
        Console.WriteLine($"Summary: total={summary.Total}, compliant={summary.Compliant}, exceptions={summary.Excepted}, violations={summary.Violations}");
        if (summary.Total > 0)
        {
            var violationRatio = summary.Violations / (double)summary.Total;
            Console.WriteLine($"Downgrade ratio: {violationRatio:P1}");
        }

        Console.WriteLine(evaluation.HasViolations
            ? "Policy violations detected. See details above."
            : "Transport policy satisfied.");
    }

    internal static async Task<int> RunMeshLeadersStatusAsync(string baseUrl, string? scope, bool watch, string? timeoutOption, string? grpcUrlOption)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'.").ConfigureAwait(false);
            return 1;
        }

        if (watch)
        {
            if (!string.IsNullOrWhiteSpace(grpcUrlOption))
            {
                var grpcResult = await TryRunMeshLeadersWatchGrpcAsync(grpcUrlOption!, scope, timeout).ConfigureAwait(false);
                if (grpcResult.HasValue)
                {
                    return grpcResult.Value;
                }
            }

            return await RunMeshLeadersWatchAsync(baseUrl, scope, timeout).ConfigureAwait(false);
        }

        return await RunMeshLeadersSnapshotAsync(baseUrl, scope, timeout).ConfigureAwait(false);
    }

    internal static async Task<int> RunMeshUpgradeStatusAsync(string baseUrl, bool asJson, string? timeoutOption)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/upgrade", scope: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Upgrade status request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            NodeDrainSnapshot? snapshot;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                snapshot = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.NodeDrainSnapshot, cts.Token).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                await Console.Error.WriteLineAsync("Upgrade status response was empty.").ConfigureAwait(false);
                return 1;
            }

            if (asJson)
            {
                var json = JsonSerializer.Serialize(snapshot, OmniRelayCliJsonContext.Default.NodeDrainSnapshot);
                CliRuntime.Console.WriteLine(json);
            }
            else
            {
                PrintNodeDrainSnapshot(snapshot);
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Upgrade status request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunMeshUpgradeDrainAsync(string baseUrl, string? reason, string? timeoutOption)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/upgrade/drain", scope: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target)
            {
                Content = JsonContent.Create(new NodeDrainCommandDto(reason), mediaType: null, jsonTypeInfo: OmniRelayCliJsonContext.Default.NodeDrainCommandDto)
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Drain request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            NodeDrainSnapshot? snapshot;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                snapshot = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.NodeDrainSnapshot, cts.Token).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                await Console.Error.WriteLineAsync("Drain response was empty.").ConfigureAwait(false);
                return 1;
            }

            PrintNodeDrainSnapshot(snapshot);
            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Drain request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunMeshUpgradeResumeAsync(string baseUrl, string? timeoutOption)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/upgrade/resume", scope: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Resume request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            NodeDrainSnapshot? snapshot;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                snapshot = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.NodeDrainSnapshot, cts.Token).ConfigureAwait(false);
            }

            if (snapshot is null)
            {
                await Console.Error.WriteLineAsync("Resume response was empty.").ConfigureAwait(false);
                return 1;
            }

            PrintNodeDrainSnapshot(snapshot);
            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Resume request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunMeshShardsListAsync(
        string baseUrl,
        string? namespaceId,
        string? ownerNodeId,
        string[] statusFilters,
        string? search,
        string? cursor,
        int? pageSize,
        bool asJson)
    {
        if (!TryNormalizeShardStatuses(statusFilters, out var normalizedStatuses, out var statusError))
        {
            await Console.Error.WriteLineAsync(statusError!).ConfigureAwait(false);
            return 1;
        }

        if (pageSize.HasValue && pageSize <= 0)
        {
            await Console.Error.WriteLineAsync("Page size must be greater than zero.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildShardUri(baseUrl, "/control/shards", namespaceId, ownerNodeId, normalizedStatuses, search, cursor, pageSize, fromVersion: null, toVersion: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        ApplyMeshScope(request, MeshReadScope);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Shard list request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            ShardControl.ShardListResponse? payload;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                payload = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.ShardListResponse, cts.Token).ConfigureAwait(false);
            }

            if (payload is null)
            {
                await Console.Error.WriteLineAsync("Shard list response was empty.").ConfigureAwait(false);
                return 1;
            }

            if (asJson)
            {
                var json = JsonSerializer.Serialize(payload, OmniRelayCliJsonContext.Default.ShardListResponse);
                CliRuntime.Console.WriteLine(FormatJson(json));
            }
            else
            {
                PrintShardList(payload);
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Shard list request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunMeshShardsDiffAsync(
        string baseUrl,
        string? namespaceId,
        string? ownerNodeId,
        string[] statusFilters,
        string? search,
        long? fromVersion,
        long? toVersion,
        bool asJson)
    {
        if (!TryNormalizeShardStatuses(statusFilters, out var normalizedStatuses, out var statusError))
        {
            await Console.Error.WriteLineAsync(statusError!).ConfigureAwait(false);
            return 1;
        }

        if (fromVersion.HasValue && fromVersion < 0)
        {
            await Console.Error.WriteLineAsync("from-version must be non-negative.").ConfigureAwait(false);
            return 1;
        }

        if (toVersion.HasValue && toVersion < 0)
        {
            await Console.Error.WriteLineAsync("to-version must be non-negative.").ConfigureAwait(false);
            return 1;
        }

        if (fromVersion.HasValue && toVersion.HasValue && fromVersion > toVersion)
        {
            await Console.Error.WriteLineAsync("from-version must be less than or equal to to-version.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildShardUri(baseUrl, "/control/shards/diff", namespaceId, ownerNodeId, normalizedStatuses, search, cursor: null, pageSize: null, fromVersion, toVersion);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        ApplyMeshScope(request, MeshOperateScope);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Shard diff request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            ShardControl.ShardDiffResponse? payload;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                payload = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.ShardDiffResponse, cts.Token).ConfigureAwait(false);
            }

            if (payload is null)
            {
                await Console.Error.WriteLineAsync("Shard diff response was empty.").ConfigureAwait(false);
                return 1;
            }

            if (asJson)
            {
                var json = JsonSerializer.Serialize(payload, OmniRelayCliJsonContext.Default.ShardDiffResponse);
                CliRuntime.Console.WriteLine(FormatJson(json));
            }
            else
            {
                PrintShardDiff(payload);
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Shard diff request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static async Task<int> RunMeshShardsSimulateAsync(
        string baseUrl,
        string namespaceId,
        string? strategyId,
        string[] nodeTokens,
        bool asJson)
    {
        if (string.IsNullOrWhiteSpace(namespaceId))
        {
            await Console.Error.WriteLineAsync("Namespace must be provided.").ConfigureAwait(false);
            return 1;
        }

        if (!TryParseSimulationNodes(nodeTokens, out var nodes, out var nodeError))
        {
            await Console.Error.WriteLineAsync(nodeError!).ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/shards/simulate", scope: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var requestPayload = new ShardControl.ShardSimulationRequest
        {
            Namespace = namespaceId,
            StrategyId = strategyId,
            Nodes = nodes
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, target)
        {
            Content = JsonContent.Create(requestPayload, mediaType: null, jsonTypeInfo: OmniRelayCliJsonContext.Default.ShardSimulationRequest)
        };
        ApplyMeshScope(request, MeshOperateScope);

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Simulation request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            ShardControl.ShardSimulationResponse? payload;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                payload = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.ShardSimulationResponse, cts.Token).ConfigureAwait(false);
            }

            if (payload is null)
            {
                await Console.Error.WriteLineAsync("Simulation response was empty.").ConfigureAwait(false);
                return 1;
            }

            if (asJson)
            {
                var json = JsonSerializer.Serialize(payload, OmniRelayCliJsonContext.Default.ShardSimulationResponse);
                CliRuntime.Console.WriteLine(FormatJson(json));
            }
            else
            {
                PrintShardSimulation(payload);
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Simulation request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    internal static int RunMeshBootstrapIssueToken(string signingKey, string cluster, string role, string? lifetimeOption, int? maxUses, string issuer)
    {
        if (string.IsNullOrWhiteSpace(signingKey))
        {
            throw new InvalidOperationException("Signing key must be provided.");
        }

        var lifetime = TimeSpan.FromHours(1);
        if (!string.IsNullOrWhiteSpace(lifetimeOption) && !TryParseDuration(lifetimeOption!, out lifetime))
        {
            throw new InvalidOperationException($"Lifetime '{lifetimeOption}' is not a valid duration.");
        }

        var descriptor = new BootstrapTokenDescriptor
        {
            ClusterId = cluster,
            Role = role,
            Lifetime = lifetime,
            MaxUses = maxUses
        };

        var signingOptions = new BootstrapTokenSigningOptions
        {
            SigningKey = Encoding.UTF8.GetBytes(signingKey),
            Issuer = issuer,
            DefaultLifetime = lifetime,
            DefaultMaxUses = maxUses
        };

        var service = new BootstrapTokenService(signingOptions, new InMemoryBootstrapReplayProtector(), NullLogger<BootstrapTokenService>.Instance);
        var token = service.CreateToken(descriptor);
        Console.WriteLine(token);
        return 0;
    }

    internal static async Task<int> RunMeshBootstrapJoinAsync(string baseUrl, string token, string? outputPath, string? timeoutOption)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("A join token must be provided.");
        }

        var timeout = TimeSpan.FromSeconds(30);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            throw new InvalidOperationException($"Timeout '{timeoutOption}' is not a valid duration.");
        }

        using var cts = new CancellationTokenSource(timeout);
        var httpClient = CliRuntime.HttpClientFactory.CreateClient();
        httpClient.Timeout = Timeout.InfiniteTimeSpan;
        var client = new BootstrapClient(httpClient);
        var result = await client.JoinAsync(
            new Uri(baseUrl),
            new BootstrapJoinRequest { Token = token },
            timeout,
            cts.Token).ConfigureAwait(false);
        if (result.IsFailure)
        {
            var error = result.Error!;
            await Console.Error.WriteLineAsync($"Bootstrap join failed: {error.Message} ({error.Code ?? "error"})").ConfigureAwait(false);
            return string.Equals(error.Code, "timeout", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
        }

        var response = result.ValueOrThrow();
        var compactJson = JsonSerializer.Serialize(response, BootstrapJsonContext.Default.BootstrapJoinResponse);
        var json = FormatJson(compactJson);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            File.WriteAllText(outputPath!, json);
            Console.WriteLine($"Bootstrap bundle written to {outputPath}");
        }
        else
        {
            Console.WriteLine(json);
        }

        return 0;
    }

    private static string FormatJson(string json)
    {
        // Avoid large reformatting that would allocate on LOH; fall back to compact JSON.
        if (Encoding.UTF8.GetByteCount(json) > Program.PrettyPrintLimitBytes)
        {
            return json;
        }

        using var document = JsonDocument.Parse(json);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, PrettyWriterOptions))
        {
            document.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal static async Task<int> RunMeshPeersListAsync(string baseUrl, MeshPeersOutputFormat format, string? timeoutOption)
    {
        var timeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(timeoutOption) && !TryParseDuration(timeoutOption!, out timeout))
        {
            await Console.Error.WriteLineAsync($"Could not parse timeout '{timeoutOption}'.").ConfigureAwait(false);
            return 1;
        }

        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/peers", scope: null);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Peer diagnostics request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            MeshPeersResponse? result;
            await using ((await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false)).AsAsyncDisposable(out var stream))
            {
                result = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.MeshPeersResponse, cts.Token).ConfigureAwait(false);
            }
            if (result is null)
            {
                await Console.Error.WriteLineAsync("Peer diagnostics response was empty.").ConfigureAwait(false);
                return 1;
            }

            switch (format)
            {
                case MeshPeersOutputFormat.Json:
                    var json = JsonSerializer.Serialize(result, OmniRelayCliJsonContext.Default.MeshPeersResponse);
                    CliRuntime.Console.WriteLine(json);
                    break;
                default:
                    PrintMeshPeersTable(result);
                    break;
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Peer diagnostics request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Method awaits asynchronous disposables and already configures awaited operations for CLI usage.")]
    internal static async Task<int> RunMeshLeadersSnapshotAsync(string baseUrl, string? scope, TimeSpan timeout)
    {
        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/leaders", scope);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var cts = new CancellationTokenSource(timeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Leadership request failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var snapshot = await JsonSerializer.DeserializeAsync(stream, OmniRelayCliJsonContext.Default.LeadershipSnapshotResponse, cts.Token).ConfigureAwait(false);
            PrintLeadershipSnapshot(snapshot, scope);
            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Leadership request timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Method awaits asynchronous disposables and already configures awaited operations for CLI usage.")]
    internal static async Task<int> RunMeshLeadersWatchAsync(string baseUrl, string? scope, TimeSpan timeout)
    {
        Uri target;
        try
        {
            target = BuildControlPlaneUri(baseUrl, "/control/events/leadership", scope);
        }
        catch (ArgumentException ex)
        {
            await Console.Error.WriteLineAsync(ex.Message).ConfigureAwait(false);
            return 1;
        }

        using var client = CliRuntime.HttpClientFactory.CreateClient();
        client.Timeout = Timeout.InfiniteTimeSpan;
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var connectCts = new CancellationTokenSource(timeout);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                await Console.Error.WriteLineAsync($"Leadership stream failed: {(int)response.StatusCode} {response.ReasonPhrase}.").ConfigureAwait(false);
                return 1;
            }

            Console.WriteLine($"Streaming leadership events from {target} (scope={scope ?? "*"}). Press Ctrl+C to exit.");

            await using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            var buffer = new StringBuilder();

            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    if (buffer.Length == 0)
                    {
                        continue;
                    }

                    var payload = buffer.ToString();
                    buffer.Clear();
                    try
                    {
                        var leadershipEvent = JsonSerializer.Deserialize(payload, OmniRelayCliJsonContext.Default.LeadershipEventDto);
                        if (leadershipEvent is not null)
                        {
                            PrintLeadershipEvent(leadershipEvent);
                        }
                    }
                    catch (JsonException ex)
                    {
                        await Console.Error.WriteLineAsync($"Failed to parse leadership event: {ex.Message}").ConfigureAwait(false);
                    }

                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var segment = line.Length > 5 ? line[5..].TrimStart() : string.Empty;
                    buffer.Append(segment);
                }
            }

            return 0;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Leadership stream connection timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    private static readonly Method<LeadershipSubscribeRequest, ProtoLeadershipEvent> LeadershipSubscribeMethod =
        new Method<LeadershipSubscribeRequest, ProtoLeadershipEvent>(
            MethodType.ServerStreaming,
            "omnirelay.mesh.control.v1.LeadershipControlService",
            "Subscribe",
            Marshallers.Create(
                request => request.ToByteArray(),
                data => LeadershipSubscribeRequest.Parser.ParseFrom(data)),
            Marshallers.Create(
                response => response.ToByteArray(),
                data => ProtoLeadershipEvent.Parser.ParseFrom(data)));

    private static async Task<int?> TryRunMeshLeadersWatchGrpcAsync(string grpcUrl, string? scope, TimeSpan timeout)
    {
        if (!Uri.TryCreate(grpcUrl, UriKind.Absolute, out var address))
        {
            await Console.Error.WriteLineAsync($"Invalid gRPC URL '{grpcUrl}'.").ConfigureAwait(false);
            return 1;
        }

        var profile = new GrpcControlPlaneClientProfile
        {
            Address = address,
            PreferHttp3 = !string.Equals(address.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase),
            UseSharedTls = false,
            Runtime = new GrpcClientRuntimeOptions
            {
                EnableHttp3 = true
            }
        };

        try
        {
            var channelResult = CliRuntime.GrpcControlPlaneClientFactory.CreateChannel(profile);
            if (channelResult.IsFailure)
            {
                await Console.Error.WriteLineAsync($"Failed to create gRPC control-plane channel: {channelResult.Error?.Message ?? "unknown"}").ConfigureAwait(false);
                return 1;
            }

            using var channel = channelResult.ValueOrThrow();
            using var cts = new CancellationTokenSource(timeout);
            var invoker = channel.CreateCallInvoker();
            using var call = invoker.AsyncServerStreamingCall(
                LeadershipSubscribeMethod,
                host: null,
                options: new CallOptions(cancellationToken: cts.Token),
                request: new LeadershipSubscribeRequest { Scope = scope ?? string.Empty });

            Console.WriteLine($"Streaming leadership events via gRPC from {address} (scope={scope ?? "*"}). Press Ctrl+C to exit.");

            while (await call.ResponseStream.MoveNext(cts.Token).ConfigureAwait(false))
            {
                var dto = LeadershipEventDto.FromProto(call.ResponseStream.Current);
                PrintLeadershipEvent(dto);
            }

            return 0;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Unimplemented)
        {
            await Console.Error.WriteLineAsync($"gRPC leadership stream unavailable ({ex.StatusCode}). Falling back to HTTP SSE...").ConfigureAwait(false);
            return null;
        }
        catch (RpcException ex)
        {
            await Console.Error.WriteLineAsync($"gRPC leadership stream failed: {ex.StatusCode} {ex.Message}").ConfigureAwait(false);
            return 1;
        }
        catch (TaskCanceledException)
        {
            await Console.Error.WriteLineAsync("Leadership stream connection timed out.").ConfigureAwait(false);
            return 2;
        }
    }

    private static Uri BuildControlPlaneUri(string baseUrl, string relativePath, string? scope)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException($"Invalid control-plane URL '{baseUrl}'.", nameof(baseUrl));
        }

        var target = new Uri(baseUri, relativePath);
        if (string.IsNullOrWhiteSpace(scope))
        {
            return target;
        }

        var builder = new UriBuilder(target)
        {
            Query = $"scope={Uri.EscapeDataString(scope)}"
        };
        return builder.Uri;
    }

    private static void PrintLeadershipSnapshot(LeadershipSnapshotResponse? snapshot, string? scopeFilter)
    {
        if (snapshot is null)
        {
            Console.WriteLine("No leadership data available.");
            return;
        }

        var tokens = snapshot.Tokens ?? [];
        if (!string.IsNullOrWhiteSpace(scopeFilter))
        {
            tokens = [.. tokens.Where(token => string.Equals(token.Scope, scopeFilter, StringComparison.OrdinalIgnoreCase))];
        }

        Console.WriteLine($"Generated at: {snapshot.GeneratedAt:O}");

        if (tokens.Length == 0)
        {
            Console.WriteLine("No active leaders.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"{"Scope",-32} {"Leader",-20} {"Term",6} {"Fence",6} {"Expires (UTC)",-24} {"Kind",-12}");

        foreach (var token in tokens.OrderBy(static t => t.Scope, StringComparer.OrdinalIgnoreCase))
        {
            var expires = token.ExpiresAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss\\Z", CultureInfo.InvariantCulture);
            Console.WriteLine($"{token.Scope,-32} {token.LeaderId,-20} {token.Term,6} {token.FenceToken,6} {expires,-24} {token.ScopeKind,-12}");
        }
    }

    private static void PrintMeshPeersTable(MeshPeersResponse response)
    {
        Console.WriteLine($"Generated at: {response.GeneratedAt:O}");
        Console.WriteLine($"Local node: {response.LocalNodeId}");
        Console.WriteLine();
        Console.WriteLine($"{"Node",-24} {"Status",-8} {"Role",-12} {"Cluster",-18} {"Region",-18} {"Version",-12} {"HTTP/3",-7} {"rtt (ms)",8}");

        var ordered = response.Peers
            .OrderByDescending(peer => string.Equals(peer.NodeId, response.LocalNodeId, StringComparison.Ordinal))
            .ThenBy(peer => peer.NodeId, StringComparer.Ordinal);

        foreach (var peer in ordered)
        {
            var metadata = peer.Metadata;
            var rtt = peer.RoundTripTimeMs.HasValue ? peer.RoundTripTimeMs.Value.ToString("0.##", CultureInfo.InvariantCulture) : "-";
            Console.WriteLine(
                $"{peer.NodeId,-24} {peer.Status,-8} {metadata.Role,-12} {metadata.ClusterId,-18} {metadata.Region,-18} {metadata.MeshVersion,-12} {(metadata.Http3Support ? "yes" : "no"),-7} {rtt,8}");
        }
    }

    private static void PrintNodeDrainSnapshot(NodeDrainSnapshot snapshot)
    {
        Console.WriteLine($"State   : {snapshot.State}");
        if (!string.IsNullOrWhiteSpace(snapshot.Reason))
        {
            Console.WriteLine($"Reason  : {snapshot.Reason}");
        }

        Console.WriteLine($"Updated : {snapshot.UpdatedAt:O}");

        if (snapshot.Participants.Length == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Participants:");
        foreach (var participant in snapshot.Participants.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var errorSuffix = string.IsNullOrWhiteSpace(participant.LastError) ? string.Empty : $" (error: {participant.LastError})";
            Console.WriteLine($"  - {participant.Name}: {participant.State} @ {participant.UpdatedAt:O}{errorSuffix}");
        }
    }

    private static void PrintShardList(ShardControl.ShardListResponse response)
    {
        Console.WriteLine($"Shard version : {response.Version}");
        Console.WriteLine($"Next cursor   : {response.NextCursor ?? "(none)"}");
        Console.WriteLine();

        if (response.Items.Count == 0)
        {
            Console.WriteLine("No shards matched the filters.");
            return;
        }

        Console.WriteLine($"{"Namespace",-24} {"Shard",-18} {"Owner",-18} {"Status",-10} {"Version",8} {"Updated",-24}");
        foreach (var shard in response.Items
                     .OrderBy(s => s.Namespace, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(s => s.ShardId, StringComparer.OrdinalIgnoreCase))
        {
            var updated = shard.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss\\Z", CultureInfo.InvariantCulture);
            Console.WriteLine($"{shard.Namespace,-24} {shard.ShardId,-18} {shard.OwnerNodeId,-18} {shard.Status,-10} {shard.Version,8} {updated,-24}");
        }
    }

    private static void PrintShardDiff(ShardControl.ShardDiffResponse response)
    {
        if (response.Items.Count == 0)
        {
            Console.WriteLine("No shard diffs were found for the requested range.");
        }
        else
        {
            foreach (var entry in response.Items.OrderBy(diff => diff.Position))
            {
                Console.WriteLine($"[{entry.Position}] {entry.Current.Namespace}/{entry.Current.ShardId}");
                var previousOwner = entry.Previous?.OwnerNodeId ?? "(new)";
                Console.WriteLine($"  owner : {previousOwner} -> {entry.Current.OwnerNodeId} ({entry.Current.Status})");
                Console.WriteLine($"  version: {entry.Current.Version}");
                if (!string.IsNullOrWhiteSpace(entry.Current.ChangeTicket))
                {
                    Console.WriteLine($"  ticket : {entry.Current.ChangeTicket}");
                }

                if (entry.History is { } history)
                {
                    Console.WriteLine($"  actor  : {history.Actor} reason={history.Reason}");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Last position: {response.LastPosition?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
    }

    private static void PrintShardSimulation(ShardControl.ShardSimulationResponse response)
    {
        Console.WriteLine($"Namespace : {response.Namespace}");
        Console.WriteLine($"Strategy  : {response.StrategyId}");
        Console.WriteLine($"Generated : {response.GeneratedAt:O}");
        Console.WriteLine($"Assignments: {response.Assignments.Count}");
        Console.WriteLine($"Changes    : {response.Changes.Count}");
        Console.WriteLine();

        if (response.Changes.Count == 0)
        {
            Console.WriteLine("Existing owners already match the proposed plan.");
            return;
        }

        Console.WriteLine($"{"Shard",-18} {"Current",-18} {"Proposed",-18}");
        foreach (var change in response.Changes
                     .OrderBy(c => c.Namespace, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(c => c.ShardId, StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine($"{change.ShardId,-18} {change.CurrentOwner,-18} {change.ProposedOwner,-18}");
        }
    }

    private static Uri BuildShardUri(
        string baseUrl,
        string relativePath,
        string? namespaceId,
        string? ownerNodeId,
        List<string> statuses,
        string? search,
        string? cursor,
        int? pageSize,
        long? fromVersion,
        long? toVersion)
    {
        var target = BuildControlPlaneUri(baseUrl, relativePath, scope: null);
        var parameters = new List<KeyValuePair<string, string>>();

        if (!string.IsNullOrWhiteSpace(namespaceId))
        {
            parameters.Add(new KeyValuePair<string, string>("namespace", namespaceId));
        }

        if (!string.IsNullOrWhiteSpace(ownerNodeId))
        {
            parameters.Add(new KeyValuePair<string, string>("owner", ownerNodeId));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add(new KeyValuePair<string, string>("search", search));
        }

        if (!string.IsNullOrWhiteSpace(cursor))
        {
            parameters.Add(new KeyValuePair<string, string>("cursor", cursor));
        }

        if (pageSize.HasValue)
        {
            parameters.Add(new KeyValuePair<string, string>("pageSize", pageSize.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (fromVersion.HasValue)
        {
            parameters.Add(new KeyValuePair<string, string>("fromVersion", fromVersion.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (toVersion.HasValue)
        {
            parameters.Add(new KeyValuePair<string, string>("toVersion", toVersion.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (statuses.Count > 0)
        {
            parameters.Add(new KeyValuePair<string, string>("status", string.Join(',', statuses)));
        }

        if (parameters.Count == 0)
        {
            return target;
        }

        var builder = new UriBuilder(target)
        {
            Query = BuildQueryString(parameters)
        };
        return builder.Uri;
    }

    private static string BuildQueryString(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        var builder = new StringBuilder();
        foreach (var (key, value) in parameters)
        {
            if (string.IsNullOrEmpty(key) || value is null)
            {
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append('&');
            }

            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value));
        }

        return builder.ToString();
    }

    private static bool TryNormalizeShardStatuses(string[] values, out List<string> normalized, out string? error)
    {
        if (values is null || values.Length == 0)
        {
            normalized = [];
            error = null;
            return true;
        }

        normalized = new List<string>(values.Length);
        error = null;

        foreach (var raw in values)
        {
            var remaining = raw.AsSpan();
            while (TryReadNextToken(ref remaining, out var token))
            {
                if (!Enum.TryParse(token, ignoreCase: true, out DomainShardStatus parsed))
                {
                    normalized.Clear();
                    error = $"Invalid shard status '{token.ToString()}'. Valid values: {string.Join(", ", Enum.GetNames<DomainShardStatus>())}.";
                    return false;
                }

                normalized.Add(parsed.ToString());
            }
        }

        return true;

        static bool TryReadNextToken(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> token)
        {
            token = default;
            if (remaining.IsEmpty)
            {
                return false;
            }

            var commaIndex = remaining.IndexOf(',');
            if (commaIndex < 0)
            {
                token = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
                return !token.IsEmpty;
            }

            token = remaining[..commaIndex].Trim();
            remaining = remaining[(commaIndex + 1)..];
            return !token.IsEmpty;
        }
    }

    private static bool TryParseSimulationNodes(string[] nodeTokens, out List<ShardControl.ShardSimulationNode> nodes, out string? error)
    {
        nodes = new List<ShardControl.ShardSimulationNode>(nodeTokens?.Length ?? 0);
        error = null;

        if (nodeTokens is null || nodeTokens.Length == 0)
        {
            error = "At least one --node value must be provided (e.g. --node node-a:1.0).";
            return false;
        }

        foreach (var raw in nodeTokens)
        {
            var remaining = raw.AsSpan();
            while (TryReadNextEntry(ref remaining, out var entry))
            {
                if (!TryParseNodeEntry(entry, out var node, out error))
                {
                    nodes.Clear();
                    return false;
                }

                nodes.Add(node);
            }
        }

        if (nodes.Count == 0)
        {
            error = "Unable to parse any --node values.";
            return false;
        }

        return true;

        static bool TryReadNextEntry(ref ReadOnlySpan<char> remaining, out ReadOnlySpan<char> entry)
        {
            entry = default;
            if (remaining.IsEmpty)
            {
                return false;
            }

            var commaIndex = remaining.IndexOf(',');
            if (commaIndex < 0)
            {
                entry = remaining.Trim();
                remaining = ReadOnlySpan<char>.Empty;
                return !entry.IsEmpty;
            }

            entry = remaining[..commaIndex].Trim();
            remaining = remaining[(commaIndex + 1)..];
            return !entry.IsEmpty;
        }

        static bool TryParseNodeEntry(ReadOnlySpan<char> token, out ShardControl.ShardSimulationNode node, out string? error)
        {
            node = null!;
            error = null;

            var firstColon = token.IndexOf(':');
            var nodeIdSpan = firstColon < 0 ? token : token[..firstColon];
            var remainder = firstColon < 0 ? ReadOnlySpan<char>.Empty : token[(firstColon + 1)..];

            if (nodeIdSpan.Trim().IsEmpty)
            {
                error = $"Could not parse node '{token.ToString()}'. Expected nodeId[:weight[:region[:zone]]].";
                return false;
            }

            var weightSpan = ReadOnlySpan<char>.Empty;
            var regionSpan = ReadOnlySpan<char>.Empty;
            var zoneSpan = ReadOnlySpan<char>.Empty;

            if (!remainder.IsEmpty)
            {
                var secondColon = remainder.IndexOf(':');
                if (secondColon < 0)
                {
                    weightSpan = remainder;
                }
                else
                {
                    weightSpan = remainder[..secondColon];
                    var tail = remainder[(secondColon + 1)..];

                    var thirdColon = tail.IndexOf(':');
                    if (thirdColon < 0)
                    {
                        regionSpan = tail;
                    }
                    else
                    {
                        regionSpan = tail[..thirdColon];
                        zoneSpan = tail[(thirdColon + 1)..];
                    }
                }
            }

            double? weight = null;
            if (!weightSpan.IsEmpty)
            {
                if (!double.TryParse(weightSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedWeight))
                {
                    error = $"Invalid weight '{weightSpan.ToString()}' for node '{nodeIdSpan.ToString()}'.";
                    return false;
                }

                weight = parsedWeight;
            }

            node = new ShardControl.ShardSimulationNode(
                nodeIdSpan.ToString(),
                weight,
                regionSpan.IsEmpty ? null : regionSpan.ToString(),
                zoneSpan.IsEmpty ? null : zoneSpan.ToString());

            return true;
        }
    }
    private static void ApplyMeshScope(HttpRequestMessage request, string scope)
    {
        request.Headers.TryAddWithoutValidation(MeshScopeHeader, scope);
    }

    private static void PrintLeadershipEvent(LeadershipEventDto leadershipEvent)
    {
        var leader = leadershipEvent.Token?.LeaderId ?? leadershipEvent.LeaderId ?? "n/a";
        var term = leadershipEvent.Token?.Term ?? 0;
        var fence = leadershipEvent.Token?.FenceToken ?? 0;
        var expires = leadershipEvent.Token is null
            ? "n/a"
            : leadershipEvent.Token.ExpiresAt.ToUniversalTime().ToString("HH:mm:ss\\Z", CultureInfo.InvariantCulture);
        var reason = string.IsNullOrWhiteSpace(leadershipEvent.Reason) ? "(none)" : leadershipEvent.Reason!;
        var scope = leadershipEvent.Scope ?? leadershipEvent.Token?.Scope ?? "*";
        Console.WriteLine($"[{leadershipEvent.OccurredAt:HH:mm:ss}] {leadershipEvent.EventKind.ToString().ToUpperInvariant(),-10} scope={scope} leader={leader} term={term} fence={fence} expires={expires} reason={reason}");
    }

    internal sealed class LeadershipSnapshotResponse
    {
        public DateTimeOffset GeneratedAt { get; set; }

        public LeadershipTokenResponse[] Tokens { get; set; } = [];
    }

    internal sealed class LeadershipTokenResponse
    {
        public string Scope { get; set; } = string.Empty;

        public string ScopeKind { get; set; } = string.Empty;

        public string LeaderId { get; set; } = string.Empty;

        public long Term { get; set; }

        public long FenceToken { get; set; }

        public DateTimeOffset IssuedAt { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }

        public Dictionary<string, string> Labels { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class LeadershipEventDto
    {
        public CoreLeadershipEventKind EventKind { get; set; } = CoreLeadershipEventKind.Snapshot;

        public string? Scope { get; set; }

        public string? LeaderId { get; set; }

        public LeadershipTokenResponse? Token { get; set; }

        public string? Reason { get; set; }

        public Guid CorrelationId { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        public static LeadershipEventDto FromProto(ProtoLeadershipEvent source)
        {
            ArgumentNullException.ThrowIfNull(source);

            var dto = new LeadershipEventDto
            {
                EventKind = source.Kind switch
                {
                    ProtoLeadershipEventKind.Observed => CoreLeadershipEventKind.Observed,
                    ProtoLeadershipEventKind.Elected => CoreLeadershipEventKind.Elected,
                    ProtoLeadershipEventKind.Renewed => CoreLeadershipEventKind.Renewed,
                    ProtoLeadershipEventKind.Lost => CoreLeadershipEventKind.Lost,
                    ProtoLeadershipEventKind.Expired => CoreLeadershipEventKind.Expired,
                    ProtoLeadershipEventKind.SteppedDown => CoreLeadershipEventKind.SteppedDown,
                    _ => CoreLeadershipEventKind.Snapshot
                },
                Scope = string.IsNullOrWhiteSpace(source.Scope) ? null : source.Scope,
                LeaderId = string.IsNullOrWhiteSpace(source.LeaderId) ? null : source.LeaderId,
                Reason = string.IsNullOrWhiteSpace(source.Reason) ? null : source.Reason,
                CorrelationId = Guid.TryParse(source.CorrelationId, out var correlation) ? correlation : Guid.Empty,
                OccurredAt = source.OccurredAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow
            };

            if (source.Token is not null)
            {
                dto.Token = new LeadershipTokenResponse
                {
                    Scope = source.Token.Scope,
                    ScopeKind = source.Token.ScopeKind,
                    LeaderId = source.Token.LeaderId,
                    Term = source.Token.Term,
                    FenceToken = source.Token.FenceToken,
                    IssuedAt = source.Token.IssuedAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    ExpiresAt = source.Token.ExpiresAt?.ToDateTimeOffset() ?? DateTimeOffset.MinValue,
                    Labels = source.Token.Labels.ToDictionary(
                        pair => pair.Key,
                        pair => pair.Value,
                        StringComparer.OrdinalIgnoreCase)
                };
            }

            return dto;
        }
    }
}
