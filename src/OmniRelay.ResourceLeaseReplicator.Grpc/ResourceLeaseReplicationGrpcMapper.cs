using System.Collections.Immutable;
using System.Globalization;
using Google.Protobuf;
using OmniRelay.Dispatcher.Grpc;

namespace OmniRelay.Dispatcher;

internal static class ResourceLeaseReplicationGrpcMapper
{
    public static ResourceLeaseReplicationEvent ToDomain(ResourceLeaseReplicationEventMessage message)
    {
        var ownership = message.Ownership is null
            ? null
            : new ResourceLeaseOwnershipHandle(
                message.Ownership.SequenceId,
                message.Ownership.Attempt,
                new Guid(message.Ownership.LeaseId.ToByteArray()));

        var payload = message.Payload is null
            ? null
            : new ResourceLeaseItemPayload(
                message.Payload.ResourceType,
                message.Payload.ResourceId,
                message.Payload.PartitionKey,
                message.Payload.PayloadEncoding,
                message.Payload.Body.ToByteArray(),
                message.Payload.Attributes,
                string.IsNullOrWhiteSpace(message.Payload.RequestId) ? null : message.Payload.RequestId);

        var error = message.Error is null
            ? null
            : new ResourceLeaseErrorInfo(message.Error.Message, message.Error.Code);

        var metadata = message.Metadata.Count == 0
            ? ImmutableDictionary<string, string>.Empty
            : message.Metadata.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

        return new ResourceLeaseReplicationEvent(
            message.SequenceNumber,
            (ResourceLeaseReplicationEventType)message.EventType,
            DateTimeOffset.Parse(message.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ownership,
            string.IsNullOrWhiteSpace(message.PeerId) ? null : message.PeerId,
            payload,
            error,
            metadata);
    }

    public static ResourceLeaseReplicationEventMessage ToMessage(ResourceLeaseReplicationEvent replicationEvent)
    {
        var message = new ResourceLeaseReplicationEventMessage
        {
            SequenceNumber = replicationEvent.SequenceNumber,
            EventType = (int)replicationEvent.EventType,
            Timestamp = replicationEvent.Timestamp.ToString("O"),
            PeerId = replicationEvent.PeerId ?? string.Empty
        };

        if (replicationEvent.Ownership is { } ownership)
        {
            message.Ownership = new ResourceLeaseOwnershipHandleMessage
            {
                SequenceId = ownership.SequenceId,
                Attempt = ownership.Attempt,
                LeaseId = ByteString.CopyFrom(ownership.LeaseId.ToByteArray())
            };
        }

        if (replicationEvent.Payload is { } payload)
        {
            var payloadMessage = new ResourceLeaseItemPayloadMessage
            {
                ResourceType = payload.ResourceType ?? string.Empty,
                ResourceId = payload.ResourceId ?? string.Empty,
                PartitionKey = payload.PartitionKey ?? string.Empty,
                PayloadEncoding = payload.PayloadEncoding ?? string.Empty,
                Body = ByteString.CopyFrom(payload.Body ?? []),
                RequestId = payload.RequestId ?? string.Empty
            };

            if (payload.Attributes is { Count: > 0 })
            {
                foreach (var kvp in payload.Attributes)
                {
                    payloadMessage.Attributes[kvp.Key] = kvp.Value;
                }
            }

            message.Payload = payloadMessage;
        }

        if (replicationEvent.Error is { } error)
        {
            message.Error = new ResourceLeaseErrorInfoMessage
            {
                Message = error.Message ?? string.Empty,
                Code = error.Code ?? string.Empty
            };
        }

        if (replicationEvent.Metadata.Count > 0)
        {
            foreach (var kvp in replicationEvent.Metadata)
            {
                message.Metadata[kvp.Key] = kvp.Value;
            }
        }

        return message;
    }
}
