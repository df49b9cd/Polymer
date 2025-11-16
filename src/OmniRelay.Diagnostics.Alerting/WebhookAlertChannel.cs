using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace OmniRelay.Diagnostics.Alerting;

public sealed class WebhookAlertChannel : IAlertChannel
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly string? _authenticationToken;

    public WebhookAlertChannel(string name, Uri endpoint, HttpClient httpClient, IReadOnlyDictionary<string, string> headers, string? authenticationToken)
    {
        Name = name;
        _endpoint = endpoint;
        _httpClient = httpClient;
        _headers = headers;
        _authenticationToken = authenticationToken;
    }

    public string Name { get; }

    public async ValueTask SendAsync(AlertEvent alert, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        foreach (var header in _headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        if (!string.IsNullOrWhiteSpace(_authenticationToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _authenticationToken);
        }

        var json = JsonSerializer.Serialize(alert, WebhookAlertJsonContext.Default.AlertEvent);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}
