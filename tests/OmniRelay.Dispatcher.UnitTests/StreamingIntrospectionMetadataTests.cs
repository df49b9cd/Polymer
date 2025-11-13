using OmniRelay.Core.Transport;
using Xunit;

namespace OmniRelay.Dispatcher.UnitTests;

public class StreamingIntrospectionMetadataTests
{
    [Fact]
    public void StreamChannelMetadata_DefaultsMatchExpected()
    {
        var response = StreamChannelMetadata.DefaultResponse;
        Assert.Equal(StreamDirection.Server, response.Direction);
        Assert.Null(response.Capacity);
        Assert.True(response.TracksMessageCount);

        var request = StreamChannelMetadata.DefaultRequest;
        Assert.Equal(StreamDirection.Client, request.Direction);
        Assert.False(request.TracksMessageCount);
    }

    [Fact]
    public void AggregateDefaults_ComposeChannelMetadata()
    {
        Assert.Equal(StreamChannelMetadata.DefaultResponse, StreamIntrospectionMetadata.Default.ResponseChannel);
        Assert.Equal(StreamChannelMetadata.DefaultRequest, ClientStreamIntrospectionMetadata.Default.RequestChannel);
        Assert.True(ClientStreamIntrospectionMetadata.Default.AggregatesUnaryResponse);

        var duplex = DuplexIntrospectionMetadata.Default;
        Assert.Equal(StreamChannelMetadata.DefaultRequest, duplex.RequestChannel);
        Assert.Equal(StreamChannelMetadata.DefaultResponse, duplex.ResponseChannel);
    }
}
