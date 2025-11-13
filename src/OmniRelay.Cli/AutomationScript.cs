namespace OmniRelay.Cli;

internal sealed record AutomationScript
{
    public AutomationStep[] Steps { get; init; } = [];
}

internal sealed record AutomationStep
{
    public string Type { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string? Transport { get; init; }

    public string? Service { get; init; }

    public string? Procedure { get; init; }

    public string? Caller { get; init; }

    public string? Encoding { get; init; }

    public Dictionary<string, string>? Headers { get; init; }

    public string[]? Profiles { get; init; }

    public string? ShardKey { get; init; }

    public string? RoutingKey { get; init; }

    public string? RoutingDelegate { get; init; }

    public string[]? ProtoFiles { get; init; }

    public string? ProtoMessage { get; init; }

    public string? Ttl { get; init; }

    public string? Deadline { get; init; }

    public string? Timeout { get; init; }

    public string? Body { get; init; }

    public string? BodyFile { get; init; }

    public string? BodyBase64 { get; init; }

    public string? Url { get; init; }

    public string? Address { get; init; }

    public string[]? Addresses { get; init; }

    public string? Format { get; init; }

    public string? Duration { get; init; }

    public string? Delay { get; init; }
}
