namespace OmniRelay.Core.Leadership;

/// <summary>Runtime configuration for the leadership coordinator.</summary>
public sealed class LeadershipOptions
{
    /// <summary>Turns the leadership service on/off. Defaults to <c>true</c> when the section exists.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Primary lease duration. Renewals occur before the deadline using <see cref="RenewalLeadTime"/>.</summary>
    [TimeSpanRange("00:00:01", "00:10:00")]
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>How long before expiry the coordinator should renew its lease.</summary>
    [TimeSpanRange("00:00:01", "00:05:00")]
    public TimeSpan RenewalLeadTime { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Interval between election evaluations per scope.</summary>
    [TimeSpanRange("00:00:00.100", "00:00:10")]
    public TimeSpan EvaluationInterval { get; set; } = TimeSpan.FromMilliseconds(750);

    /// <summary>Maximum amount of time leadership acquisition should take under healthy conditions.</summary>
    [TimeSpanRange("00:00:01", "00:00:30")]
    public TimeSpan MaxElectionWindow { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Backoff applied after failed elections to avoid thundering herds.</summary>
    [TimeSpanRange("00:00:00.100", "00:00:05")]
    public TimeSpan ElectionBackoff { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Optional explicit node identifier; defaults to the gossip node id.</summary>
    public string? NodeId { get; set; }

    /// <summary>Explicit scope descriptors. When empty, the coordinator falls back to the global-control scope.</summary>
    public IList<LeadershipScopeDescriptor> Scopes { get; } = [];

    /// <summary>Convenience configuration for bulk shard scopes.</summary>
    public IList<LeadershipShardScopeOptions> Shards { get; } = [];
}
