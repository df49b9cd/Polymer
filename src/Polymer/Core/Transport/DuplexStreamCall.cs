using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Hugo;
using Polymer.Errors;

namespace Polymer.Core.Transport;

public sealed class DuplexStreamCall : IDuplexStreamCall
{
    private readonly Channel<ReadOnlyMemory<byte>> _requests;
    private readonly Channel<ReadOnlyMemory<byte>> _responses;
    private bool _requestsCompleted;
    private bool _responsesCompleted;

    private DuplexStreamCall(RequestMeta requestMeta, ResponseMeta responseMeta)
    {
        RequestMeta = requestMeta ?? throw new ArgumentNullException(nameof(requestMeta));
        ResponseMeta = responseMeta ?? new ResponseMeta();

        _requests = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        _responses = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
    }

    public static DuplexStreamCall Create(RequestMeta requestMeta, ResponseMeta? responseMeta = null) =>
        new(requestMeta, responseMeta ?? new ResponseMeta());

    public RequestMeta RequestMeta { get; }

    public ResponseMeta ResponseMeta { get; private set; }

    public ChannelWriter<ReadOnlyMemory<byte>> RequestWriter => _requests.Writer;

    public ChannelReader<ReadOnlyMemory<byte>> RequestReader => _requests.Reader;

    public ChannelWriter<ReadOnlyMemory<byte>> ResponseWriter => _responses.Writer;

    public ChannelReader<ReadOnlyMemory<byte>> ResponseReader => _responses.Reader;

    public void SetResponseMeta(ResponseMeta meta)
    {
        ResponseMeta = meta ?? new ResponseMeta();
    }

    public ValueTask CompleteRequestsAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_requestsCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _requestsCompleted = true;
        TryCompleteChannel(_requests.Writer, error, RequestMeta.Transport);
        return ValueTask.CompletedTask;
    }

    public ValueTask CompleteResponsesAsync(Error? error = null, CancellationToken cancellationToken = default)
    {
        if (_responsesCompleted)
        {
            return ValueTask.CompletedTask;
        }

        _responsesCompleted = true;
        TryCompleteChannel(_responses.Writer, error, RequestMeta.Transport);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _requests.Writer.TryComplete();
        _responses.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static void TryCompleteChannel(ChannelWriter<ReadOnlyMemory<byte>> writer, Error? error, string? transport)
    {
        if (error is null)
        {
            writer.TryComplete();
            return;
        }

        var exception = PolymerErrors.FromError(error, transport);
        writer.TryComplete(exception);
    }
}
