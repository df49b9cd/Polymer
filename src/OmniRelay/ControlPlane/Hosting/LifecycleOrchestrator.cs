using System.Collections.Immutable;
using Hugo;
using Hugo.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Default implementation that coordinates <see cref="ILifecycle"/> components.</summary>
public sealed class LifecycleOrchestrator : ILifecycleOrchestrator
{
    private readonly ImmutableDictionary<string, LifecycleComponentRegistration> _components;
    private readonly ImmutableArray<LifecycleComponentRegistration> _startOrder;
    private readonly ImmutableArray<LifecycleComponentRegistration> _stopOrder;
    private readonly Dictionary<string, ComponentRuntimeState> _runtimeState;
    private readonly ResultExecutionPolicy _defaultStartPolicy;
    private readonly ResultExecutionPolicy _defaultStopPolicy;
    private readonly ILogger<LifecycleOrchestrator> _logger;
    private readonly object _stateLock = new();
    private LifecycleOrchestratorStatus _status = LifecycleOrchestratorStatus.Created;

    public LifecycleOrchestrator(
        IEnumerable<LifecycleComponentRegistration> components,
        ResultExecutionPolicy? defaultStartPolicy = null,
        ResultExecutionPolicy? defaultStopPolicy = null,
        ILogger<LifecycleOrchestrator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(components);

        _components = components
            .Select(static registration => registration ?? throw new ArgumentNullException(nameof(components), "Lifecycle component registration cannot be null."))
            .ToImmutableDictionary(static component => component.Name, StringComparer.OrdinalIgnoreCase);

        _defaultStartPolicy = defaultStartPolicy ?? ResultExecutionPolicy.None;
        _defaultStopPolicy = defaultStopPolicy ?? ResultExecutionPolicy.None;
        _logger = logger ?? NullLogger<LifecycleOrchestrator>.Instance;

        _startOrder = BuildStartOrder(_components);
        _stopOrder = [.. _startOrder.Reverse()];
        _runtimeState = _components.Values.ToDictionary(
            static component => component.Name,
            static component => new ComponentRuntimeState(component),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public async ValueTask<Result<Unit>> StartAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMoveToState(LifecycleOrchestratorStatus.Starting, LifecycleOrchestratorStatus.Created, LifecycleOrchestratorStatus.Stopped))
        {
            return Err<Unit>(Error.FromException(new InvalidOperationException("Lifecycle orchestrator is already running or stopping.")));
        }

        var started = new Stack<LifecycleComponentRegistration>();

        foreach (var component in _startOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var runtime = _runtimeState[component.Name];
            runtime.Status = LifecycleComponentStatus.Starting;
            _logger.LogInformation("Starting lifecycle component {Component}", component.Name);

            var startResult = await ExecuteLifecycleAsync(
                component.Name,
                component.Lifecycle.StartAsync,
                component.StartPolicy == ResultExecutionPolicy.None ? _defaultStartPolicy : component.StartPolicy,
                cancellationToken).ConfigureAwait(false);

            if (startResult.IsFailure)
            {
                runtime.Status = LifecycleComponentStatus.Faulted;
                runtime.LastError = startResult.Error;
                await RollbackAsync(started, cancellationToken).ConfigureAwait(false);
                MoveToState(LifecycleOrchestratorStatus.Stopped);
                return startResult;
            }

            runtime.Status = LifecycleComponentStatus.Running;
            runtime.LastError = null;
            started.Push(component);
            _logger.LogInformation("Lifecycle component {Component} started.", component.Name);
        }

        MoveToState(LifecycleOrchestratorStatus.Running);
        return Ok(Unit.Value);
    }

    /// <inheritdoc />
    public async ValueTask<Result<Unit>> StopAsync(CancellationToken cancellationToken = default)
    {
        if (!TryMoveToState(LifecycleOrchestratorStatus.Stopping, LifecycleOrchestratorStatus.Running))
        {
            // If we were never started we still need to ensure Stop returns success.
            if (_status is LifecycleOrchestratorStatus.Created or LifecycleOrchestratorStatus.Stopped)
            {
                MoveToState(LifecycleOrchestratorStatus.Stopped);
                return Ok(Unit.Value);
            }

            return Err<Unit>(Error.FromException(new InvalidOperationException("Lifecycle orchestrator is not running.")));
        }

        foreach (var component in _stopOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var runtime = _runtimeState[component.Name];

            if (runtime.Status is LifecycleComponentStatus.Stopped or LifecycleComponentStatus.Created)
            {
                continue;
            }

            runtime.Status = LifecycleComponentStatus.Stopping;
            _logger.LogInformation("Stopping lifecycle component {Component}", component.Name);

            var stopResult = await ExecuteLifecycleAsync(
                component.Name,
                component.Lifecycle.StopAsync,
                component.StopPolicy == ResultExecutionPolicy.None ? _defaultStopPolicy : component.StopPolicy,
                cancellationToken).ConfigureAwait(false);

            if (stopResult.IsFailure)
            {
                runtime.Status = LifecycleComponentStatus.Faulted;
                runtime.LastError = stopResult.Error;
                MoveToState(LifecycleOrchestratorStatus.Stopped);
                return stopResult;
            }

            runtime.Status = LifecycleComponentStatus.Stopped;
            runtime.LastError = null;
            _logger.LogInformation("Lifecycle component {Component} stopped.", component.Name);
        }

        MoveToState(LifecycleOrchestratorStatus.Stopped);
        return Ok(Unit.Value);
    }

    /// <inheritdoc />
    public LifecycleOrchestratorSnapshot Snapshot()
    {
        var components = _runtimeState
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new LifecycleComponentSnapshot(pair.Key, pair.Value.Status, pair.Value.LastError))
            .ToImmutableArray();

        return new LifecycleOrchestratorSnapshot(DateTimeOffset.UtcNow, components);
    }

    private async ValueTask RollbackAsync(Stack<LifecycleComponentRegistration> started, CancellationToken cancellationToken)
    {
        while (started.Count > 0)
        {
            var component = started.Pop();
            var runtime = _runtimeState[component.Name];
            runtime.Status = LifecycleComponentStatus.Stopping;

            try
            {
                await component.Lifecycle.StopAsync(cancellationToken).ConfigureAwait(false);
                runtime.Status = LifecycleComponentStatus.Stopped;
            }
            catch (Exception ex)
            {
                runtime.Status = LifecycleComponentStatus.Faulted;
                runtime.LastError = Error.FromException(ex).WithMetadata("component", component.Name);
                _logger.LogWarning(ex, "Failed to roll back lifecycle component {Component}", component.Name);
            }
        }
    }

    private static async ValueTask<Result<Unit>> ExecuteLifecycleAsync(
        string componentName,
        Func<CancellationToken, ValueTask> lifecycleAction,
        ResultExecutionPolicy policy,
        CancellationToken cancellationToken)
    {
        return await Result.RetryWithPolicyAsync<Unit>(
            async (_, token) =>
            {
                try
                {
                    await lifecycleAction(token).ConfigureAwait(false);
                    return Ok(Unit.Value);
                }
                catch (Exception ex)
                {
                    var error = Error.FromException(ex)
                        .WithMetadata("component", componentName);
                    return Err<Unit>(error);
                }
            },
            policy,
            TimeProvider.System,
            cancellationToken).ConfigureAwait(false);
    }

    private static ImmutableArray<LifecycleComponentRegistration> BuildStartOrder(
        ImmutableDictionary<string, LifecycleComponentRegistration> components)
    {
        var incomingEdges = components.Values.ToDictionary(
            static component => component.Name,
            component => component.Dependencies.Length,
            StringComparer.OrdinalIgnoreCase);

        var adjacency = components.Values.ToDictionary(
            static component => component.Name,
            static component => new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        foreach (var component in components.Values)
        {
            foreach (var dependency in component.Dependencies)
            {
                if (!components.ContainsKey(dependency))
                {
                    throw new InvalidOperationException($"Lifecycle component '{component.Name}' depends on missing component '{dependency}'.");
                }

                adjacency[dependency].Add(component.Name);
            }
        }

        var queue = new Queue<string>(incomingEdges.Where(static pair => pair.Value == 0).Select(static pair => pair.Key));
        var ordered = new List<LifecycleComponentRegistration>(components.Count);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            ordered.Add(components[name]);

            foreach (var dependent in adjacency[name])
            {
                if (--incomingEdges[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (ordered.Count != components.Count)
        {
            throw new InvalidOperationException("Lifecycle component dependency graph contains a cycle.");
        }

        return [.. ordered];
    }

    private bool TryMoveToState(LifecycleOrchestratorStatus target, params LifecycleOrchestratorStatus[] allowedSources)
    {
        lock (_stateLock)
        {
            if (!allowedSources.Contains(_status))
            {
                return false;
            }

            _status = target;
            return true;
        }
    }

    private void MoveToState(LifecycleOrchestratorStatus status)
    {
        lock (_stateLock)
        {
            _status = status;
        }
    }

    private sealed class ComponentRuntimeState
    {
        public ComponentRuntimeState(LifecycleComponentRegistration component)
        {
            Component = component;
        }

        public LifecycleComponentRegistration Component { get; }
        public LifecycleComponentStatus Status { get; set; } = LifecycleComponentStatus.Created;
        public Error? LastError { get; set; }
    }
}

internal enum LifecycleOrchestratorStatus
{
    Created = 0,
    Starting = 1,
    Running = 2,
    Stopping = 3,
    Stopped = 4
}
