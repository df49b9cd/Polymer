using System.Collections.Immutable;
using Hugo;
using Hugo.Policies;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Transport;
using static Hugo.Go;

namespace OmniRelay.ControlPlane.Hosting;

/// <summary>Default implementation that coordinates <see cref="ILifecycle"/> components.</summary>
public sealed partial class LifecycleOrchestrator : ILifecycleOrchestrator
{
    private readonly ImmutableDictionary<string, LifecycleComponentRegistration> _components;
    private readonly ImmutableArray<LifecycleComponentRegistration> _startOrder;
    private readonly ImmutableArray<ImmutableArray<LifecycleComponentRegistration>> _startLayers;
    private readonly ImmutableArray<ImmutableArray<LifecycleComponentRegistration>> _stopLayers;
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
        _startLayers = BuildStartLayers(_startOrder, _components);
        _stopLayers = [.. _startLayers.Reverse()];
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

        foreach (var layer in _startLayers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var layerResult = await StartLayerAsync(layer, started, cancellationToken).ConfigureAwait(false);
            if (layerResult.IsFailure)
            {
                await RollbackAsync(started, cancellationToken).ConfigureAwait(false);
                await StopActiveComponentsAsync(cancellationToken).ConfigureAwait(false);
                MoveToState(LifecycleOrchestratorStatus.Stopped);
                return layerResult;
            }
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

        foreach (var layer in _stopLayers)
        {
            var layerResult = await StopLayerAsync(layer, cancellationToken).ConfigureAwait(false);
            if (layerResult.IsFailure)
            {
                MoveToState(LifecycleOrchestratorStatus.Stopped);
                return layerResult;
            }
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
                Log.RollbackFailed(_logger, component.Name, ex);
            }
        }
    }

    /// <summary>
    /// Stops any components that are still marked as running or starting. This acts as a safety net
    /// when failures or cancellations prevented a component from being recorded in the rollback stack
    /// but the runtime state reached Running/Starting.
    /// </summary>
    private async ValueTask StopActiveComponentsAsync(CancellationToken cancellationToken)
    {
        foreach (var layer in _stopLayers)
        {
            using var group = new ErrGroup(cancellationToken);
            var scheduled = false;

            foreach (var component in layer)
            {
                var runtime = _runtimeState[component.Name];
                if (runtime.Status is not (LifecycleComponentStatus.Running or LifecycleComponentStatus.Starting))
                {
                    continue;
                }

                runtime.Status = LifecycleComponentStatus.Stopping;
                Log.ComponentStopping(_logger, component.Name);
                scheduled = true;

                var policy = component.StopPolicy == ResultExecutionPolicy.None ? _defaultStopPolicy : component.StopPolicy;

                group.Go(async (_, _) =>
                {
                    var result = await ExecuteLifecycleAsync(component.Name, component.Lifecycle.StopAsync, policy, cancellationToken).ConfigureAwait(false);
                    if (result.IsSuccess)
                    {
                        runtime.Status = LifecycleComponentStatus.Stopped;
                        runtime.LastError = null;
                        Log.ComponentStopped(_logger, component.Name);
                    }
                    else
                    {
                        runtime.Status = LifecycleComponentStatus.Faulted;
                        runtime.LastError = result.Error;
                    }

                    return result;
                });
            }

            if (scheduled)
            {
                // Deliberately ignore cancellation for the wait to ensure we finish stop attempts.
                _ = await group.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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

    private async ValueTask<Result<Unit>> StartLayerAsync(
        ImmutableArray<LifecycleComponentRegistration> layer,
        Stack<LifecycleComponentRegistration> started,
        CancellationToken cancellationToken)
    {
        using var group = new ErrGroup();
        foreach (var component in layer)
        {
            var runtime = _runtimeState[component.Name];
            runtime.Status = LifecycleComponentStatus.Starting;
            Log.ComponentStarting(_logger, component.Name);

            var policy = component.StartPolicy == ResultExecutionPolicy.None ? _defaultStartPolicy : component.StartPolicy;

            group.Go(async (_, token) =>
            {
                var result = await ExecuteLifecycleAsync(component.Name, component.Lifecycle.StartAsync, policy, token).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    runtime.Status = LifecycleComponentStatus.Running;
                    runtime.LastError = null;
                    lock (started)
                    {
                        started.Push(component);
                    }
                    Log.ComponentStarted(_logger, component.Name);
                }
                else
                {
                    runtime.Status = LifecycleComponentStatus.Faulted;
                    runtime.LastError = result.Error;
                }

                return result;
            });
        }

        if (layer.IsDefaultOrEmpty)
        {
            return Ok(Unit.Value);
        }

        return await group.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<Result<Unit>> StopLayerAsync(
        ImmutableArray<LifecycleComponentRegistration> layer,
        CancellationToken cancellationToken)
    {
        using var group = new ErrGroup(cancellationToken);
        var scheduled = false;

        foreach (var component in layer)
        {
            var runtime = _runtimeState[component.Name];
            if (runtime.Status is LifecycleComponentStatus.Stopped or LifecycleComponentStatus.Created)
            {
                continue;
            }

            runtime.Status = LifecycleComponentStatus.Stopping;
            Log.ComponentStopping(_logger, component.Name);

            var policy = component.StopPolicy == ResultExecutionPolicy.None ? _defaultStopPolicy : component.StopPolicy;
            scheduled = true;

            group.Go(async (_, _) =>
            {
                var result = await ExecuteLifecycleAsync(component.Name, component.Lifecycle.StopAsync, policy, cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    runtime.Status = LifecycleComponentStatus.Stopped;
                    runtime.LastError = null;
                    Log.ComponentStopped(_logger, component.Name);
                }
                else
                {
                    runtime.Status = LifecycleComponentStatus.Faulted;
                    runtime.LastError = result.Error;
                }

                return result;
            });
        }

        if (!scheduled)
        {
            return Ok(Unit.Value);
        }

        return await group.WaitAsync(CancellationToken.None).ConfigureAwait(false);
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

    private static ImmutableArray<ImmutableArray<LifecycleComponentRegistration>> BuildStartLayers(
        ImmutableArray<LifecycleComponentRegistration> ordered,
        ImmutableDictionary<string, LifecycleComponentRegistration> components)
    {
        if (ordered.IsDefaultOrEmpty)
        {
            return ImmutableArray<ImmutableArray<LifecycleComponentRegistration>>.Empty;
        }

        var depths = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in ordered)
        {
            var depth = component.Dependencies.Length == 0
                ? 0
                : component.Dependencies.Max(dependency => depths[dependency] + 1);
            depths[component.Name] = depth;
        }

        var layers = depths
            .GroupBy(static pair => pair.Value)
            .OrderBy(static group => group.Key)
            .Select(group => group
                .Select(pair => components[pair.Key])
                .ToImmutableArray())
            .ToImmutableArray();

        return layers;
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Failed to roll back lifecycle component {Component}")]
        public static partial void RollbackFailed(ILogger logger, string component, Exception exception);

        [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Starting lifecycle component {Component}")]
        public static partial void ComponentStarting(ILogger logger, string component);

        [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Lifecycle component {Component} started.")]
        public static partial void ComponentStarted(ILogger logger, string component);

        [LoggerMessage(EventId = 4, Level = LogLevel.Information, Message = "Stopping lifecycle component {Component}")]
        public static partial void ComponentStopping(ILogger logger, string component);

        [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Lifecycle component {Component} stopped.")]
        public static partial void ComponentStopped(ILogger logger, string component);
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
