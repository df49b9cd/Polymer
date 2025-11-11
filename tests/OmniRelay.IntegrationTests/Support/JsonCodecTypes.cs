namespace OmniRelay.IntegrationTests.Codecs;

public sealed record JsonCodecRequest(string Message)
{
    public string Message { get; init; } = Message;
}

public sealed record JsonCodecResponse(string Message)
{
    public string Message { get; init; } = Message;
}
