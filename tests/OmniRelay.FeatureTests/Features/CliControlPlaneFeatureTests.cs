using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OmniRelay.ControlPlane.Bootstrap;
using OmniRelay.ControlPlane.Upgrade;
using OmniRelay.Core.Gossip;
using OmniRelay.FeatureTests.Fixtures;
using OmniRelay.Tests;
using Xunit;

namespace OmniRelay.FeatureTests.Features;

public sealed class CliControlPlaneFeatureTests : IAsyncLifetime
{
    private StubControlPlaneHost? _host;

    public async ValueTask InitializeAsync()
    {
        _host = await StubControlPlaneHost.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshPeersList_RunsAgainstStubControlPlane()
    {
        var command = $"mesh peers list --url {_host!.BaseAddress} --format table";
        var result = await CliCommandRunner.RunAsync(command, TestContext.Current.CancellationToken);

        result.ExitCode.ShouldBe(0, result.Stdout);
        result.Stdout.ShouldContain("node-b");
        result.Stdout.ShouldContain("Suspect");
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshUpgradeLifecycle_DrainStatusResume()
    {
        var baseUrl = _host!.BaseAddress;
        var ct = TestContext.Current.CancellationToken;

        var drain = await CliCommandRunner.RunAsync(
            $"mesh upgrade drain --url {baseUrl} --reason rolling", ct);
        drain.ExitCode.ShouldBe(0, drain.Stdout);
        drain.Stdout.ShouldContain("rolling");

        var status = await CliCommandRunner.RunAsync($"mesh upgrade status --url {baseUrl}", ct);
        status.ExitCode.ShouldBe(0, status.Stdout);
        status.Stdout.ShouldContain("Draining");

        var resume = await CliCommandRunner.RunAsync($"mesh upgrade resume --url {baseUrl}", ct);
        resume.ExitCode.ShouldBe(0, resume.Stdout);

        var statusAfter = await CliCommandRunner.RunAsync($"mesh upgrade status --url {baseUrl}", ct);
        statusAfter.ExitCode.ShouldBe(0, statusAfter.Stdout);
        statusAfter.Stdout.ShouldContain("Active");
    }

    [Fact(Timeout = 120_000)]
    public async ValueTask MeshBootstrapJoin_WritesBundle()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"feature-bootstrap-{Guid.NewGuid():N}.json");
        try
        {
            var command = $"mesh bootstrap join --url {_host!.BaseAddress} --token cli-token --output {outputPath}";
            var result = await CliCommandRunner.RunAsync(command, TestContext.Current.CancellationToken);

            result.ExitCode.ShouldBe(0, result.Stdout);
            File.Exists(outputPath).ShouldBeTrue();
            var content = await File.ReadAllTextAsync(outputPath, TestContext.Current.CancellationToken);
            content.ShouldContain("alpha-cluster");
            content.ShouldContain("peer-b");
        }
        finally
        {
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }
    }
}

internal sealed class StubControlPlaneHost : IAsyncDisposable
{
    private readonly WebApplication _app;
    private NodeDrainState _state = NodeDrainState.Active;
    private string? _reason;

    private StubControlPlaneHost(WebApplication app, Uri baseAddress)
    {
        _app = app;
        BaseAddress = baseAddress;
    }

    public Uri BaseAddress { get; }

    public static async Task<StubControlPlaneHost> StartAsync(CancellationToken cancellationToken)
    {
        var port = TestPortAllocator.GetRandomPort();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));

        var app = builder.Build();

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        jsonOptions.Converters.Add(new JsonStringEnumConverter());

        app.MapGet("/control/peers", () =>
        {
            var payload = new
            {
                schemaVersion = "v1",
                generatedAt = DateTimeOffset.UtcNow,
                localNodeId = "node-a",
                peers = new[]
                {
                    new
                    {
                        nodeId = "node-a",
                        status = MeshGossipMemberStatus.Alive,
                        lastSeen = DateTimeOffset.UtcNow,
                        rttMs = 0.25,
                        metadata = new
                        {
                            nodeId = "node-a",
                            role = "control",
                            clusterId = "alpha-cluster",
                            region = "us-east-1",
                            meshVersion = "1.2.3",
                            http3Support = true
                        }
                    },
                    new
                    {
                        nodeId = "node-b",
                        status = MeshGossipMemberStatus.Suspect,
                        lastSeen = DateTimeOffset.UtcNow.AddSeconds(-5),
                        rttMs = 1.4,
                        metadata = new
                        {
                            nodeId = "node-b",
                            role = "worker",
                            clusterId = "alpha-cluster",
                            region = "us-east-1",
                            meshVersion = "1.2.3",
                            http3Support = false
                        }
                    }
                }
            };

            return Results.Json(payload, jsonOptions);
        });

        var host = new StubControlPlaneHost(app, new Uri($"http://127.0.0.1:{port}"));

        app.MapGet("/control/upgrade", (HttpContext context) =>
        {
            var snapshot = host.BuildSnapshot();
            return Results.Json(snapshot, jsonOptions);
        });

        app.MapPost("/control/upgrade/drain", async (HttpContext context) =>
        {
            host._state = NodeDrainState.Draining;
            var command = await context.Request.ReadFromJsonAsync<DrainCommand>(jsonOptions, cancellationToken);
            host._reason = command?.Reason;
            var snapshot = host.BuildSnapshot();
            return Results.Json(snapshot, jsonOptions);
        });

        app.MapPost("/control/upgrade/resume", () =>
        {
            host._state = NodeDrainState.Active;
            host._reason = null;
            var snapshot = host.BuildSnapshot();
            return Results.Json(snapshot, jsonOptions);
        });

        app.MapPost("/omnirelay/bootstrap/join", async (HttpContext context) =>
        {
            var request = await context.Request.ReadFromJsonAsync(BootstrapJsonContext.Default.BootstrapJoinRequest, cancellationToken);
            if (request is null || string.IsNullOrWhiteSpace(request.Token))
            {
                return Results.BadRequest(new { error = "token missing" });
            }

            var response = new BootstrapJoinResponse
            {
                ClusterId = "alpha-cluster",
                Role = "worker",
                Identity = "node-a",
                IdentityProvider = "stub",
                CertificateData = "CERTDATA",
                TrustBundleData = "TRUST",
                SeedPeers = new[] { "https://peer-a", "https://peer-b" },
                IssuedAt = DateTimeOffset.UtcNow,
                RenewAfter = DateTimeOffset.UtcNow.AddMinutes(5),
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };

            var json = JsonSerializer.Serialize(response, BootstrapJsonContext.Default.BootstrapJoinResponse);
            return Results.Content(json, "application/json");
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return host;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record DrainCommand(string? Reason);

    private NodeDrainSnapshot BuildSnapshot() =>
        new(
            _state,
            _reason,
            DateTimeOffset.UtcNow,
            new[]
            {
                new NodeDrainParticipantSnapshot(
                    "dispatcher",
                    _state == NodeDrainState.Active ? NodeDrainParticipantState.Active : NodeDrainParticipantState.Draining,
                    null,
                    DateTimeOffset.UtcNow)
            }.ToImmutableArray());
}
