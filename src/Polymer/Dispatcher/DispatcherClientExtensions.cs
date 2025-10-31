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
}
