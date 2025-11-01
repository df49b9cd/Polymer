using System;
using System.Collections.Generic;
using Polymer.Core;
using Polymer.Core.Clients;
using Polymer.Core.Transport;

namespace Polymer.Dispatcher;

public static class DispatcherClientExtensions
{
    public static UnaryClient<TRequest, TResponse> CreateUnaryClient<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string service,
        ICodec<TRequest, TResponse> codec,
        string? outboundKey = null)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        if (codec is null)
        {
            throw new ArgumentNullException(nameof(codec));
        }

        var configuration = dispatcher.ClientConfig(service);
        if (!configuration.TryGetUnary(outboundKey, out var outbound) || outbound is null)
        {
            throw new KeyNotFoundException($"No unary outbound registered for service '{service}' with key '{outboundKey ?? OutboundCollection.DefaultKey}'.");
        }

        return new UnaryClient<TRequest, TResponse>(outbound, codec, configuration.UnaryMiddleware);
    }

    public static OnewayClient<TRequest> CreateOnewayClient<TRequest>(
        this Dispatcher dispatcher,
        string service,
        ICodec<TRequest, object> codec,
        string? outboundKey = null)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        if (codec is null)
        {
            throw new ArgumentNullException(nameof(codec));
        }

        var configuration = dispatcher.ClientConfig(service);
        if (!configuration.TryGetOneway(outboundKey, out var outbound) || outbound is null)
        {
            throw new KeyNotFoundException($"No oneway outbound registered for service '{service}' with key '{outboundKey ?? OutboundCollection.DefaultKey}'.");
        }

        return new OnewayClient<TRequest>(outbound, codec, configuration.OnewayMiddleware);
    }

    public static StreamClient<TRequest, TResponse> CreateStreamClient<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string service,
        ICodec<TRequest, TResponse> codec,
        string? outboundKey = null)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        if (codec is null)
        {
            throw new ArgumentNullException(nameof(codec));
        }

        var configuration = dispatcher.ClientConfig(service);
        if (!configuration.TryGetStream(outboundKey, out var outbound) || outbound is null)
        {
            throw new KeyNotFoundException($"No stream outbound registered for service '{service}' with key '{outboundKey ?? OutboundCollection.DefaultKey}'.");
        }

        return new StreamClient<TRequest, TResponse>(outbound, codec, configuration.StreamMiddleware);
    }

    public static ClientStreamClient<TRequest, TResponse> CreateClientStreamClient<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string service,
        ICodec<TRequest, TResponse> codec,
        string? outboundKey = null)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        if (codec is null)
        {
            throw new ArgumentNullException(nameof(codec));
        }

        var configuration = dispatcher.ClientConfig(service);
        if (!configuration.TryGetClientStream(outboundKey, out var outbound) || outbound is null)
        {
            throw new KeyNotFoundException($"No client stream outbound registered for service '{service}' with key '{outboundKey ?? OutboundCollection.DefaultKey}'.");
        }

        return new ClientStreamClient<TRequest, TResponse>(outbound, codec, configuration.ClientStreamMiddleware);
    }

    public static DuplexStreamClient<TRequest, TResponse> CreateDuplexStreamClient<TRequest, TResponse>(
        this Dispatcher dispatcher,
        string service,
        ICodec<TRequest, TResponse> codec,
        string? outboundKey = null)
    {
        if (dispatcher is null)
        {
            throw new ArgumentNullException(nameof(dispatcher));
        }

        if (codec is null)
        {
            throw new ArgumentNullException(nameof(codec));
        }

        var configuration = dispatcher.ClientConfig(service);
        if (!configuration.TryGetDuplex(outboundKey, out var outbound) || outbound is null)
        {
            throw new KeyNotFoundException($"No duplex stream outbound registered for service '{service}' with key '{outboundKey ?? OutboundCollection.DefaultKey}'.");
        }

        return new DuplexStreamClient<TRequest, TResponse>(outbound, codec, configuration.DuplexMiddleware);
    }
}
