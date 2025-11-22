namespace OmniRelay.Core.Gossip;

/// <summary>Payload for passive-view shuffle exchanges.</summary>
internal sealed record MeshGossipShuffleEnvelope(string Sender, string[] Endpoints);
