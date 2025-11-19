using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using OmniRelay.Core.Shards;
using OmniRelay.Core.Shards.ControlPlane;

namespace OmniRelay.Core.Diagnostics;

internal static class ShardDiagnosticsEndpointExtensions
{
    private const string MeshReadScope = "mesh.read";
    private const string MeshOperateScope = "mesh.operate";
    private static readonly char[] ScopeSeparators = [' ', ',', ';'];

    [RequiresDynamicCode("Minimal API handlers use reflection during binding.")]
    [RequiresUnreferencedCode("Minimal API handlers use reflection during binding.")]
    public static void MapShardDiagnosticsEndpoints(this WebApplication app)
    {
        app.MapGet("/control/shards", async Task<IResult> (HttpContext context, ShardControlPlaneService service) =>
        {
            if (!HasRequiredScope(context, MeshReadScope, MeshOperateScope))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!TryCreateShardFilter(context.Request, out var filter, out var errorResult))
            {
                return errorResult ?? Results.BadRequest(new { error = "Invalid shard filter parameters." });
            }

            int? pageSize = null;
            if (context.Request.Query.TryGetValue("pageSize", out var sizeValues))
            {
                if (!int.TryParse(sizeValues, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedPageSize))
                {
                    return Results.BadRequest(new { error = "Invalid pageSize value." });
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
                return Results.Json(response, ShardDiagnosticsJsonContext.Default.ShardListResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
        });

        app.MapGet("/control/shards/diff", async Task<IResult> (HttpContext context, ShardControlPlaneService service) =>
        {
            if (!HasRequiredScope(context, MeshOperateScope))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (!TryCreateShardFilter(context.Request, out var filter, out var errorResult))
            {
                return errorResult ?? Results.BadRequest(new { error = "Invalid shard filter parameters." });
            }

            if (!TryParseLongQuery(context.Request, "fromVersion", out var fromVersion, out var parseError))
            {
                return parseError ?? Results.BadRequest(new { error = "Invalid fromVersion value." });
            }

            if (!TryParseLongQuery(context.Request, "toVersion", out var toVersion, out parseError))
            {
                return parseError ?? Results.BadRequest(new { error = "Invalid toVersion value." });
            }

            var response = await service.DiffAsync(fromVersion, toVersion, filter, context.RequestAborted).ConfigureAwait(false);
            return Results.Json(response, ShardDiagnosticsJsonContext.Default.ShardDiffResponse);
        });

        app.MapGet("/control/shards/watch", async (HttpContext context, ShardControlPlaneService service) =>
        {
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
        });

        app.MapPost("/control/shards/simulate", async Task<IResult> (HttpContext context, ShardSimulationRequest request, ShardControlPlaneService service) =>
        {
            if (!HasRequiredScope(context, MeshOperateScope))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "Request body is required." });
            }

            try
            {
                var response = await service.SimulateAsync(request, context.RequestAborted).ConfigureAwait(false);
                return Results.Json(response, ShardDiagnosticsJsonContext.Default.ShardSimulationResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound);
            }
        });
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
