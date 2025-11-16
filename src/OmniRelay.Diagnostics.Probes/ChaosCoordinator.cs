using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;

namespace OmniRelay.Diagnostics;

/// <summary>Coordinates chaos experiments that can be toggled via diagnostics endpoints.</summary>
public sealed class ChaosCoordinator
{
    private readonly ConcurrentDictionary<string, IChaosExperiment> _experiments;

    public ChaosCoordinator(IEnumerable<IChaosExperiment> experiments)
    {
        _experiments = new ConcurrentDictionary<string, IChaosExperiment>(StringComparer.OrdinalIgnoreCase);
        foreach (var experiment in experiments)
        {
            _experiments[experiment.Name] = experiment;
        }
    }

    public async Task<ChaosCommandResult> StartAsync(string name, CancellationToken cancellationToken)
    {
        if (!_experiments.TryGetValue(name, out var experiment))
        {
            return ChaosCommandResult.NotFound(name);
        }

        await experiment.StartAsync(cancellationToken).ConfigureAwait(false);
        return ChaosCommandResult.Started(name);
    }

    public async Task<ChaosCommandResult> StopAsync(string name, CancellationToken cancellationToken)
    {
        if (!_experiments.TryGetValue(name, out var experiment))
        {
            return ChaosCommandResult.NotFound(name);
        }

        await experiment.StopAsync(cancellationToken).ConfigureAwait(false);
        return ChaosCommandResult.Stopped(name);
    }
}

public sealed record ChaosCommandResult(string Name, string Status)
{
    public static ChaosCommandResult NotFound(string name) => new(name, "not_found");

    public static ChaosCommandResult Started(string name) => new(name, "started");

    public static ChaosCommandResult Stopped(string name) => new(name, "stopped");

    public IResult ToHttpResult() => Status switch
    {
        "not_found" => Results.NotFound(new { name = Name, status = Status }),
        _ => Results.Ok(new { name = Name, status = Status })
    };
}

public interface IChaosExperiment
{
    string Name { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

internal sealed class NullChaosExperiment : IChaosExperiment
{
    public string Name => "noop-chaos";

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
