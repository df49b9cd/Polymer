using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using YARPCore.Core;

namespace YARPCore.Dispatcher;

/// <summary>
/// Thread-safe registry that resolves codecs for inbound and outbound procedures.
/// </summary>
public sealed class CodecRegistry
{
    private readonly Dictionary<string, ProcedureCodecDescriptor> _codecs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _localService;

    internal CodecRegistry(string localService, IEnumerable<ProcedureCodecRegistration> registrations)
    {
        if (string.IsNullOrWhiteSpace(localService))
        {
            throw new ArgumentException("Local service name cannot be null or whitespace.", nameof(localService));
        }

        _localService = localService;

        foreach (var registration in registrations)
        {
            RegisterInternal(
                registration.Scope,
                ResolveService(registration.Scope, registration.Service),
                registration.Procedure,
                registration.Kind,
                CreateDescriptor(registration),
                registration.Aliases);
        }
    }

    internal CodecRegistry(string localService)
        : this(localService, [])
    {
    }

    /// <summary>
    /// Registers a codec for an inbound procedure on the local service.
    /// </summary>
    public void RegisterInbound<TRequest, TResponse>(
        string procedure,
        ProcedureKind kind,
        ICodec<TRequest, TResponse> codec,
        IEnumerable<string>? aliases = null)
    {
        Register(
            ProcedureCodecScope.Inbound,
            _localService,
            procedure,
            kind,
            codec,
            aliases);
    }

    /// <summary>
    /// Registers a codec for an outbound procedure on the specified remote service.
    /// </summary>
    public void RegisterOutbound<TRequest, TResponse>(
        string service,
        string procedure,
        ProcedureKind kind,
        ICodec<TRequest, TResponse> codec,
        IEnumerable<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        Register(
            ProcedureCodecScope.Outbound,
            service,
            procedure,
            kind,
            codec,
            aliases);
    }

    /// <summary>
    /// Attempts to resolve a codec descriptor for the specified procedure.
    /// </summary>
    public bool TryResolve(
        ProcedureCodecScope scope,
        string service,
        string procedure,
        ProcedureKind kind,
        [MaybeNullWhen(false)] out ProcedureCodecDescriptor descriptor)
    {
        descriptor = default!;

        if (string.IsNullOrWhiteSpace(service))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(procedure))
        {
            return false;
        }

        var key = BuildKey(scope, service, procedure, kind);

        lock (_gate)
        {
            return _codecs.TryGetValue(key, out descriptor);
        }
    }

    /// <summary>
    /// Attempts to resolve a strongly typed codec for the specified procedure.
    /// </summary>
    public bool TryResolve<TRequest, TResponse>(
        ProcedureCodecScope scope,
        string service,
        string procedure,
        ProcedureKind kind,
        out ICodec<TRequest, TResponse> codec)
    {
        codec = default!;

        if (!TryResolve(scope, service, procedure, kind, out var descriptor))
        {
            return false;
        }

        EnsureTypeMatch(descriptor, typeof(TRequest), typeof(TResponse), service, procedure, kind, scope);

        codec = (ICodec<TRequest, TResponse>)descriptor.Codec;
        return true;
    }

    /// <summary>
    /// Returns an immutable snapshot of all registered codecs.
    /// </summary>
    public ImmutableArray<(ProcedureCodecScope Scope, string Service, string Procedure, ProcedureKind Kind, ProcedureCodecDescriptor Descriptor)> Snapshot()
    {
        lock (_gate)
        {
            var builder = ImmutableArray.CreateBuilder<(ProcedureCodecScope, string, string, ProcedureKind, ProcedureCodecDescriptor)>(_codecs.Count);
            foreach (var entry in _codecs)
            {
                var parsed = ParseKey(entry.Key);
                builder.Add((parsed.Scope, parsed.Service, parsed.Procedure, parsed.Kind, entry.Value));
            }

            return builder.ToImmutable();
        }
    }

    private void Register<TRequest, TResponse>(
        ProcedureCodecScope scope,
        string service,
        string procedure,
        ProcedureKind kind,
        ICodec<TRequest, TResponse> codec,
        IEnumerable<string>? aliases)
    {
        ArgumentNullException.ThrowIfNull(codec);

        RegisterInternal(
            scope,
            service,
            procedure,
            kind,
            new ProcedureCodecDescriptor(typeof(TRequest), typeof(TResponse), codec, codec.Encoding),
            aliases);
    }

    private void RegisterInternal(
        ProcedureCodecScope scope,
        string service,
        string procedure,
        ProcedureKind kind,
        ProcedureCodecDescriptor descriptor,
        ImmutableArray<string> aliases)
    {
        RegisterInternal(scope, service, procedure, kind, descriptor, (IEnumerable<string>)aliases);
    }

    private void RegisterInternal(
        ProcedureCodecScope scope,
        string service,
        string procedure,
        ProcedureKind kind,
        ProcedureCodecDescriptor descriptor,
        IEnumerable<string>? aliases = null)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            throw new ArgumentException("Service identifier cannot be null or whitespace.", nameof(service));
        }

        if (string.IsNullOrWhiteSpace(procedure))
        {
            throw new ArgumentException("Procedure name cannot be null or whitespace.", nameof(procedure));
        }

        lock (_gate)
        {
            foreach (var name in EnumerateNames(procedure, aliases))
            {
                var key = BuildKey(scope, service, name, kind);
                if (_codecs.ContainsKey(key))
                {
                    throw new InvalidOperationException($"Codec for {scope} procedure '{service}::{name}' ({kind}) is already registered.");
                }

                _codecs[key] = descriptor;
            }
        }
    }

    private static ProcedureCodecDescriptor CreateDescriptor(ProcedureCodecRegistration registration) =>
        new(registration.RequestType, registration.ResponseType, registration.Codec, registration.Encoding);

    private string ResolveService(ProcedureCodecScope scope, string? service) =>
        scope switch
        {
            ProcedureCodecScope.Inbound => _localService,
            _ => service ?? throw new InvalidOperationException("Outbound codec registrations must specify a service name.")
        };

    private static IEnumerable<string> EnumerateNames(string procedure, IEnumerable<string>? aliases)
    {
        yield return procedure;

        if (aliases is null)
        {
            yield break;
        }

        foreach (var alias in aliases)
        {
            if (string.IsNullOrWhiteSpace(alias))
            {
                continue;
            }

            yield return alias;
        }
    }

    private static void EnsureTypeMatch(
        ProcedureCodecDescriptor descriptor,
        Type requestType,
        Type responseType,
        string service,
        string procedure,
        ProcedureKind kind,
        ProcedureCodecScope scope)
    {
        if (descriptor.RequestType != requestType || descriptor.ResponseType != responseType)
        {
            throw new InvalidOperationException(
                $"Codec registered for {scope} procedure '{service}::{procedure}' ({kind}) expects request '{descriptor.RequestType.FullName}' and response '{descriptor.ResponseType.FullName}', but caller requested '{requestType.FullName}' → '{responseType.FullName}'.");
        }
    }

    private static string BuildKey(ProcedureCodecScope scope, string service, string procedure, ProcedureKind kind)
    {
        var normalizedService = service.Trim();
        var normalizedProcedure = procedure.Trim();
        return $"{scope}|{kind}|{normalizedService.ToLowerInvariant()}|{normalizedProcedure.ToLowerInvariant()}";
    }

    private static (ProcedureCodecScope Scope, string Service, string Procedure, ProcedureKind Kind) ParseKey(string key)
    {
        var parts = key.Split('|');
        if (parts.Length != 4)
        {
            throw new InvalidOperationException($"Codec registry key '{key}' is invalid.");
        }

        return (
            Enum.Parse<ProcedureCodecScope>(parts[0], ignoreCase: true),
            parts[2],
            parts[3],
            Enum.Parse<ProcedureKind>(parts[1], ignoreCase: true));
    }
}
