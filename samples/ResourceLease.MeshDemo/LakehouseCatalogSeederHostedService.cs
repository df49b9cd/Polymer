using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class LakehouseCatalogSeederHostedService : BackgroundService
{
    private readonly ResourceLeaseHttpClient _client;
    private readonly MeshDemoOptions _options;
    private readonly ILogger<LakehouseCatalogSeederHostedService> _logger;
    private readonly Random _random = new();
    private readonly List<CatalogTable> _tables = [];
    private readonly object _lock = new();
    private int _tableCounter;

    public LakehouseCatalogSeederHostedService(ResourceLeaseHttpClient client, IOptions<MeshDemoOptions> options, ILogger<LakehouseCatalogSeederHostedService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = _options.GetSeederInterval();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var operation = CreateOperation();
                var payload = new ResourceLeaseItemPayload(
                    ResourceType: "lakehouse.catalog",
                    ResourceId: operation.ResourceId,
                    PartitionKey: operation.Catalog,
                    PayloadEncoding: "application/json",
                    Body: JsonSerializer.SerializeToUtf8Bytes(operation, MeshJson.Context.LakehouseCatalogOperation),
                    Attributes: new Dictionary<string, string>
                    {
                        ["catalog"] = operation.Catalog,
                        ["database"] = operation.Database,
                        ["table"] = operation.Table,
                        ["operation"] = operation.OperationType.ToString(),
                        ["version"] = operation.Version.ToString(CultureInfo.InvariantCulture)
                    },
                    RequestId: operation.RequestId);

                var response = await _client.EnqueueAsync(payload, stoppingToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Enqueued {Catalog}.{Database}.{Table} v{Version} ({Operation}) pending={Pending} active={Active}",
                    operation.Catalog,
                    operation.Database,
                    operation.Table,
                    operation.Version,
                    operation.OperationType,
                    response.Stats.PendingCount,
                    response.Stats.ActiveLeaseCount);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enqueue catalog operation.");
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private LakehouseCatalogOperation CreateOperation()
    {
        lock (_lock)
        {
            var catalogs = _options.Catalogs is { Length: > 0 } c ? c : MeshDemoOptions.DefaultCatalogs;
            var principals = _options.Principals is { Length: > 0 } p ? p : MeshDemoOptions.DefaultPrincipals;
            var databases = _options.DatabasePrefixes is { Length: > 0 } d ? d : MeshDemoOptions.DefaultDatabasePrefixes;

            CatalogTable table;
            LakehouseCatalogOperationType operationType;

            if (_tables.Count == 0 || _tables.Count < 5 || _random.NextDouble() < 0.25)
            {
                table = CreateTable(catalogs, databases);
                _tables.Add(table);
                operationType = LakehouseCatalogOperationType.CreateTable;
            }
            else
            {
                table = _tables[_random.Next(_tables.Count)];
                operationType = ChooseOperationType();
            }

            table.Version++;

            IReadOnlyList<string> changes;
            switch (operationType)
            {
                case LakehouseCatalogOperationType.CreateTable:
                    table.Columns = new List<string>(GenerateInitialSchema());
                    changes = table.Columns;
                    break;

                case LakehouseCatalogOperationType.AlterSchema:
                    var change = ApplySchemaChange(table);
                    changes = [change];
                    break;

                case LakehouseCatalogOperationType.Vacuum:
                    changes = [$"vacuum retention={_random.Next(7, 31)}d"];
                    break;

                default:
                    changes = [$"commit snapshot {Guid.NewGuid():N}"];
                    break;
            }

            var operation = new LakehouseCatalogOperation(
                table.Catalog,
                table.Database,
                table.Table,
                operationType,
                table.Version,
                principals[_random.Next(principals.Length)],
                table.Columns,
                changes,
                SnapshotId: Guid.NewGuid().ToString("N"),
                Timestamp: DateTimeOffset.UtcNow,
                RequestId: Guid.NewGuid().ToString("N"));

            return operation;
        }
    }

    private CatalogTable CreateTable(string[] catalogs, string[] databases)
    {
        var catalog = catalogs[_random.Next(catalogs.Length)];
        var dbPrefix = databases[_random.Next(databases.Length)];
        var database = $"{dbPrefix}_db_{_random.Next(1, 5)}";
        var tableName = $"{dbPrefix}_table_{Interlocked.Increment(ref _tableCounter):D3}";
        return new CatalogTable(catalog, database, tableName)
        {
            Columns = []
        };
    }

    private LakehouseCatalogOperationType ChooseOperationType()
    {
        var roll = _random.NextDouble();
        if (roll < 0.25)
        {
            return LakehouseCatalogOperationType.AlterSchema;
        }

        if (roll < 0.7)
        {
            return LakehouseCatalogOperationType.CommitSnapshot;
        }

        return LakehouseCatalogOperationType.Vacuum;
    }

    private IReadOnlyList<string> GenerateInitialSchema()
    {
        var columns = new List<string>
        {
            "table_id STRING",
            "created_at TIMESTAMP",
            "updated_at TIMESTAMP",
            "region STRING"
        };

        if (_random.NextDouble() > 0.5)
        {
            columns.Add("tenant_id STRING");
        }

        if (_random.NextDouble() > 0.5)
        {
            columns.Add("is_deleted BOOLEAN");
        }

        return columns;
    }

    private string ApplySchemaChange(CatalogTable table)
    {
        if (_random.NextDouble() < 0.5 || table.Columns.Count < 6)
        {
            var newColumn = $"metric_{_random.Next(1, 20)} DOUBLE";
            table.Columns.Add(newColumn);
            return $"ADD COLUMN {newColumn}";
        }

        var index = _random.Next(table.Columns.Count);
        var column = table.Columns[index];
        var mutated = column.Replace("DOUBLE", "DECIMAL(18,4)", StringComparison.OrdinalIgnoreCase);
        if (mutated == column)
        {
            mutated = column.Replace("STRING", "VARCHAR(256)", StringComparison.OrdinalIgnoreCase);
        }

        table.Columns[index] = mutated;
        return $"ALTER COLUMN {mutated}";
    }

    private sealed class CatalogTable
    {
        public CatalogTable(string catalog, string database, string table)
        {
            Catalog = catalog;
            Database = database;
            Table = table;
        }

        public string Catalog { get; }

        public string Database { get; }

        public string Table { get; }

        public int Version { get; set; }

        public List<string> Columns { get; set; } = [];
    }
}
