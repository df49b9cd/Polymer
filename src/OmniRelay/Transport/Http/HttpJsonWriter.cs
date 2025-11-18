#pragma warning disable IDE0005
using System.Text.Json;
using Microsoft.AspNetCore.Http;
#pragma warning restore IDE0005
using System.Text.Json.Serialization.Metadata;

namespace OmniRelay.Transport.Http;

internal static class HttpJsonWriter
{
    public static async Task WriteAsync<T>(HttpResponse response, T value, JsonTypeInfo<T> jsonTypeInfo, CancellationToken cancellationToken)
    {
        response.ContentType = "application/json";
        await JsonSerializer.SerializeAsync(response.Body, value, jsonTypeInfo, cancellationToken).ConfigureAwait(false);
    }
}
