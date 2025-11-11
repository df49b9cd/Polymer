using System.Globalization;
using System.Text;
using System.Text.Json;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed record MeshEnqueueRequest(
    string Catalog = "fabric-lakehouse",
    string Database = "sales",
    string Table = "ad_hoc",
    LakehouseCatalogOperationType Operation = LakehouseCatalogOperationType.CommitSnapshot,
    int Version = 1,
    string Principal = "cli.enqueue",
    string[]? Columns = null,
    string[]? Changes = null,
    Dictionary<string, string>? Attributes = null,
    string? Body = null,
    string? RequestId = null)
{
    public ResourceLeaseItemPayload ToPayload()
    {
        var requestId = RequestId ?? Guid.NewGuid().ToString("N");
        byte[] effectiveBody;
        string resourceId;
        var payloadAttributes = Attributes ?? new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = Catalog,
            ["database"] = Database,
            ["table"] = Table
        };

        if (string.IsNullOrWhiteSpace(Body))
        {
            var columnSet = Columns is { Length: > 0 } cols ? cols : ["id STRING", "payload STRING"];
            var changes = Changes is { Length: > 0 } delta ? delta : columnSet;
            var operation = new LakehouseCatalogOperation(
                Catalog,
                Database,
                Table,
                Operation,
                Version,
                Principal,
                columnSet,
                changes,
                SnapshotId: Guid.NewGuid().ToString("N"),
                Timestamp: DateTimeOffset.UtcNow,
                RequestId: requestId);
            effectiveBody = JsonSerializer.SerializeToUtf8Bytes(operation, MeshJson.Context.LakehouseCatalogOperation);
            resourceId = operation.ResourceId;
            payloadAttributes["operation"] = Operation.ToString();
            payloadAttributes["version"] = Version.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            effectiveBody = Encoding.UTF8.GetBytes(Body);
            resourceId = $"{Catalog}.{Database}.{Table}.manual";
        }

        return new ResourceLeaseItemPayload(
            ResourceType: "lakehouse.catalog",
            ResourceId: resourceId,
            PartitionKey: Catalog,
            PayloadEncoding: "application/json",
            Body: effectiveBody,
            Attributes: payloadAttributes,
            RequestId: requestId);
    }
}
