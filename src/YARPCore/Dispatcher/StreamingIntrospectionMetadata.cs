using YARPCore.Core.Transport;

namespace YARPCore.Dispatcher;

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
}

public sealed record StreamIntrospectionMetadata(StreamChannelMetadata ResponseChannel)
{
    public static StreamIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultResponse);
}

public sealed record ClientStreamIntrospectionMetadata(
    StreamChannelMetadata RequestChannel,
    bool AggregatesUnaryResponse)
{
    public static ClientStreamIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultRequest, true);
}

public sealed record DuplexIntrospectionMetadata(
    StreamChannelMetadata RequestChannel,
    StreamChannelMetadata ResponseChannel)
{
    public static DuplexIntrospectionMetadata Default { get; } =
        new(StreamChannelMetadata.DefaultRequest, StreamChannelMetadata.DefaultResponse);
}
