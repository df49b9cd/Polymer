using OmniRelay.Core.Transport;

namespace OmniRelay.Dispatcher;

public sealed record StreamChannelMetadata(
    StreamDirection Direction,
    string BufferingStrategy,
    int? Capacity,
    bool TracksMessageCount)
{
    public static StreamChannelMetadata DefaultResponse { get; } =
        new(StreamDirection.Server, "unbounded-channel", null, true);

    public static StreamChannelMetadata DefaultRequest { get; } =
        new(StreamDirection.Client, "unbounded-channel", null, false);

    public StreamDirection Direction { get; init; } = Direction;

    public string BufferingStrategy { get; init; } = BufferingStrategy;

    public int? Capacity { get; init; } = Capacity;

    public bool TracksMessageCount { get; init; } = TracksMessageCount;
}

public sealed record StreamIntrospectionMetadata(StreamChannelMetadata ResponseChannel)
{
    public static StreamIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultResponse);

    public StreamChannelMetadata ResponseChannel { get; init; } = ResponseChannel;
}

public sealed record ClientStreamIntrospectionMetadata(
    StreamChannelMetadata RequestChannel,
    bool AggregatesUnaryResponse)
{
    public static ClientStreamIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultRequest, true);

    public StreamChannelMetadata RequestChannel { get; init; } = RequestChannel;

    public bool AggregatesUnaryResponse { get; init; } = AggregatesUnaryResponse;
}

public sealed record DuplexIntrospectionMetadata(
    StreamChannelMetadata RequestChannel,
    StreamChannelMetadata ResponseChannel)
{
    public static DuplexIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultRequest, StreamChannelMetadata.DefaultResponse);

    public StreamChannelMetadata RequestChannel { get; init; } = RequestChannel;

    public StreamChannelMetadata ResponseChannel { get; init; } = ResponseChannel;
}
