using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;

namespace OmniRelay.Core.Diagnostics;

internal static class ShardDiagnosticsEndpointExtensions
{
    private const string MeshReadScope = "mesh.read";
    private const string MeshOperateScope = "mesh.operate";
    private static readonly char[] ScopeSeparators = [' ', ',', ';'];

    public static void MapShardDiagnosticsEndpoints(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            var method = context.Request.Method;

            if (path is "/control/shards" && HttpMethods.IsGet(method))
            {
                await ListShardsAsync(context).ConfigureAwait(false);
                return;
            }

            if (path is "/control/shards/diff" && HttpMethods.IsGet(method))
            {
                await DiffShardsAsync(context).ConfigureAwait(false);
                return;
            }

            if (path is "/control/shards/watch" && HttpMethods.IsGet(method))
            {
                await WatchShardsAsync(context).ConfigureAwait(false);
                return;
            }

            if (path is "/control/shards/simulate" && HttpMethods.IsPost(method))
            {
                await SimulateShardsAsync(context).ConfigureAwait(false);
                return;
            }

            await next().ConfigureAwait(false);
        });
    }

    private static async Task ListShardsAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<ShardControlPlaneService>();
        if (!HasRequiredScope(context, MeshReadScope, MeshOperateScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!TryCreateShardFilter(context.Request, out var filter, out var errorResult))
        {
            await (errorResult ?? Results.BadRequest(new { error = "Invalid shard filter parameters." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        int? pageSize = null;
        if (context.Request.Query.TryGetValue("pageSize", out var sizeValues))
        {
            if (!int.TryParse(sizeValues, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPageSize))
            {
                await Results.BadRequest(new { error = "Invalid pageSize value." })
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }

            pageSize = parsedPageSize;
        }

        var cursor = context.Request.Query.TryGetValue("cursor", out var cursorValues)
            ? cursorValues.ToString()
            : null;

        try
        {
            var response = await service.ListAsync(filter, cursor, pageSize, context.RequestAborted).ConfigureAwait(false);
            context.Response.Headers.ETag = $@"W/""shards-{response.Version}""";
            context.Response.Headers["x-omnirelay-shards-version"] = response.Version.ToString(CultureInfo.InvariantCulture);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    response,
                    ShardDiagnosticsJsonContext.Default.ShardListResponse,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await Results.BadRequest(new { error = ex.Message })
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

    private static async Task DiffShardsAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<ShardControlPlaneService>();
        if (!HasRequiredScope(context, MeshOperateScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (!TryCreateShardFilter(context.Request, out var filter, out var errorResult))
        {
            await (errorResult ?? Results.BadRequest(new { error = "Invalid shard filter parameters." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseLongQuery(context.Request, "fromVersion", out var fromVersion, out var parseError))
        {
            await (parseError ?? Results.BadRequest(new { error = "Invalid fromVersion value." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseLongQuery(context.Request, "toVersion", out var toVersion, out parseError))
        {
            await (parseError ?? Results.BadRequest(new { error = "Invalid toVersion value." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        var response = await service.DiffAsync(fromVersion, toVersion, filter, context.RequestAborted).ConfigureAwait(false);
        context.Response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                ShardDiagnosticsJsonContext.Default.ShardDiffResponse,
                context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task WatchShardsAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<ShardControlPlaneService>();
        if (!HasRequiredScope(context, MeshReadScope, MeshOperateScope))
        {
            await Results.StatusCode(StatusCodes.Status403Forbidden).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (!TryCreateShardFilter(context.Request, out var filter, out var errorResult))
        {
            await (errorResult ?? Results.BadRequest(new { error = "Invalid shard filter parameters." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        if (!TryParseLongQuery(context.Request, "resumeToken", out var resumeToken, out var parseError))
        {
            await (parseError ?? Results.BadRequest(new { error = "Invalid resumeToken value." }))
                .ExecuteAsync(context)
                .ConfigureAwait(false);
            return;
        }

        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers["Content-Type"] = "text/event-stream";
        await context.Response.WriteAsync("retry: 2000\n\n", context.RequestAborted).ConfigureAwait(false);

        try
        {
            await foreach (var diff in service.WatchAsync(resumeToken, filter, context.RequestAborted).ConfigureAwait(false))
            {
                if (diff.Current is null)
                {
                    continue;
                }

                var current = ShardControlPlaneMapper.ToSummary(diff.Current);
                var previous = diff.Previous is null ? null : ShardControlPlaneMapper.ToSummary(diff.Previous);
                var entry = new ShardDiffEntry(diff.Position, current, previous, diff.History);

                var payload = JsonSerializer.Serialize(entry, ShardDiagnosticsJsonContext.Default.ShardDiffEntry);
                await context.Response.WriteAsync($"id: {diff.Position}\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync("event: shard.diff\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.WriteAsync($"data: {payload}\n\n", context.RequestAborted).ConfigureAwait(false);
                await context.Response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task SimulateShardsAsync(HttpContext context)
    {
        var service = context.RequestServices.GetRequiredService<ShardControlPlaneService>();
        var request = await context.Request.ReadFromJsonAsync(
            ShardDiagnosticsJsonContext.Default.ShardSimulationRequest,
            context.RequestAborted).ConfigureAwait(false);

        if (!HasRequiredScope(context, MeshOperateScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        if (request is null)
        {
            await Results.BadRequest(new { error = "Request body is required." }).ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await service.SimulateAsync(request, context.RequestAborted).ConfigureAwait(false);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                    context.Response.Body,
                    response,
                    ShardDiagnosticsJsonContext.Default.ShardSimulationResponse,
                    context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            await Results.BadRequest(new { error = ex.Message }).ExecuteAsync(context).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound)
                .ExecuteAsync(context)
                .ConfigureAwait(false);
        }
    }

    private static bool TryParseLongQuery(HttpRequest request, string key, out long? value, out IResult? error)
    {
        error = null;
        value = null;
        if (!request.Query.TryGetValue(key, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        if (long.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = Results.BadRequest(new { error = $"Invalid {key} value." });
        return false;
    }

    private static bool TryCreateShardFilter(HttpRequest request, out ShardFilter filter, out IResult? error)
    {
        error = null;
        var namespaceFilter = request.Query.TryGetValue("namespace", out var nsValues) ? nsValues.ToString() : null;
        var ownerFilter = request.Query.TryGetValue("owner", out var ownerValues) ? ownerValues.ToString() : null;
        var searchFilter = request.Query.TryGetValue("search", out var searchValues) ? searchValues.ToString() : null;

        IReadOnlyList<ShardStatus> statuses = Array.Empty<ShardStatus>();
        if (request.Query.TryGetValue("status", out var statusValues))
        {
            var parsed = new List<ShardStatus>();
            foreach (var raw in statusValues
                         .Where(static value => !string.IsNullOrWhiteSpace(value))
                         .SelectMany(value => value!.Split(ScopeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            {
                if (!Enum.TryParse(raw, true, out ShardStatus status))
                {
                    filter = default!;
                    error = Results.BadRequest(new { error = $"Invalid status '{raw}'." });
                    return false;
                }

                parsed.Add(status);
            }

            statuses = parsed;
        }

        filter = new ShardFilter(namespaceFilter, ownerFilter, searchFilter, statuses);
        return true;
    }

    private static bool HasRequiredScope(HttpContext context, params string[] requiredScopes)
    {
        if (!context.Request.Headers.TryGetValue("x-mesh-scope", out var scopeValues))
        {
            return false;
        }

        foreach (var header in scopeValues)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            var tokens = header.Split(ScopeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Any(token => requiredScopes.Any(scope => string.Equals(scope, token, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }
        }

        return false;
    }
}
