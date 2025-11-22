using System.Collections.Immutable;
using Hugo.Policies;
using OmniRelay.Core.Transport;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Describes a lifecycle component registered with the orchestrator.</summary>
public sealed class LifecycleComponentRegistration
{
    public LifecycleComponentRegistration(
        string name,
        ILifecycle lifecycle,
        IEnumerable<string>? dependencies = null,
        ResultExecutionPolicy? startPolicy = null,
        ResultExecutionPolicy? stopPolicy = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Lifecycle component name cannot be null or whitespace.", nameof(name));
        }

        Name = name;
        Lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        Dependencies = dependencies is null
            ? ImmutableArray<string>.Empty
            : [.. dependencies.Where(static dependency => !string.IsNullOrWhiteSpace(dependency))
                .Select(static dependency => dependency!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        StartPolicy = startPolicy ?? ResultExecutionPolicy.None;
        StopPolicy = stopPolicy ?? ResultExecutionPolicy.None;
    }

    /// <summary>Component name used for diagnostics and dependency wiring.</summary>
    public string Name { get; }

    /// <summary>Concrete lifecycle implementation.</summary>
    public ILifecycle Lifecycle { get; }

    /// <summary>Names of components that must complete start before this component runs.</summary>
    public ImmutableArray<string> Dependencies { get; }

    /// <summary>Retry policy applied to StartAsync.</summary>
    public ResultExecutionPolicy StartPolicy { get; }

    /// <summary>Retry policy applied to StopAsync.</summary>
    public ResultExecutionPolicy StopPolicy { get; }
}
