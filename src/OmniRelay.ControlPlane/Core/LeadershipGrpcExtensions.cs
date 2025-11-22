using Google.Protobuf.WellKnownTypes;
using ProtoLeadershipEvent = OmniRelay.Mesh.Control.V1.LeadershipEvent;
using ProtoLeadershipEventKind = OmniRelay.Mesh.Control.V1.LeadershipEventKind;
using ProtoLeadershipToken = OmniRelay.Mesh.Control.V1.LeadershipToken;

namespace OmniRelay.Core.Leadership;

internal static class LeadershipGrpcExtensions
{
    public static ProtoLeadershipEvent ToProto(this LeadershipEvent leadershipEvent)
    {
        var proto = new ProtoLeadershipEvent
        {
            Kind = leadershipEvent.EventKind switch
            {
                LeadershipEventKind.Snapshot => ProtoLeadershipEventKind.Snapshot,
                LeadershipEventKind.Observed => ProtoLeadershipEventKind.Observed,
                LeadershipEventKind.Elected => ProtoLeadershipEventKind.Elected,
                LeadershipEventKind.Renewed => ProtoLeadershipEventKind.Renewed,
                LeadershipEventKind.Lost => ProtoLeadershipEventKind.Lost,
                LeadershipEventKind.Expired => ProtoLeadershipEventKind.Expired,
                LeadershipEventKind.SteppedDown => ProtoLeadershipEventKind.SteppedDown,
                _ => ProtoLeadershipEventKind.Unspecified
            },
            Scope = leadershipEvent.Scope ?? string.Empty,
            LeaderId = leadershipEvent.LeaderId ?? string.Empty,
            Reason = leadershipEvent.Reason ?? string.Empty,
            CorrelationId = leadershipEvent.CorrelationId == Guid.Empty ? string.Empty : leadershipEvent.CorrelationId.ToString(),
            OccurredAt = Timestamp.FromDateTimeOffset(leadershipEvent.OccurredAt)
        };

        if (leadershipEvent.Token is not null)
        {
            proto.Token = leadershipEvent.Token.ToProto();
        }

        return proto;
    }

    public static ProtoLeadershipToken ToProto(this LeadershipToken token)
    {
        var proto = new ProtoLeadershipToken
        {
            Scope = token.Scope,
            ScopeKind = token.ScopeKind,
            LeaderId = token.LeaderId,
            Term = token.Term,
            FenceToken = token.FenceToken,
            IssuedAt = Timestamp.FromDateTimeOffset(token.IssuedAt),
            ExpiresAt = Timestamp.FromDateTimeOffset(token.ExpiresAt)
        };

        foreach (var label in token.Labels)
        {
            proto.Labels[label.Key] = label.Value;
        }

        return proto;
    }
}
