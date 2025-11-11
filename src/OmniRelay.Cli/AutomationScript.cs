using System.Collections.Generic;

namespace OmniRelay.Cli;

internal sealed record AutomationScript
{
    public AutomationStep[] Steps { get; init; } = [];
}

internal sealed record AutomationStep
{
    public string Type { get; init; } = string.Empty;

    public static string? Description { get; set; }

    public static string? Transport { get; set; }

    public static string? Service { get; set; }

    public static string? Procedure { get; set; }

    public static string? Caller { get; set; }

    public static string? Encoding { get; set; }

    public static Dictionary<string, string>? Headers { get; set; }

    public static string[]? Profiles { get; set; }

    public static string? ShardKey { get; set; }

    public static string? RoutingKey { get; set; }

    public static string? RoutingDelegate { get; set; }

    public static string[]? ProtoFiles { get; set; }

    public static string? ProtoMessage { get; set; }

    public static string? Ttl { get; set; }

    public static string? Deadline { get; set; }

    public static string? Timeout { get; set; }

    public static string? Body { get; set; }

    public static string? BodyFile { get; set; }

    public static string? BodyBase64 { get; set; }

    public static string? Url { get; set; }

    public static string? Address { get; set; }

    public static string[]? Addresses { get; set; }

    public static string? Format { get; set; }

    public static string? Duration { get; set; }

    public static string? Delay { get; set; }
}
