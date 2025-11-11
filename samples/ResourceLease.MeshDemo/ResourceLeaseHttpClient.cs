using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Options;
using OmniRelay.Dispatcher;

namespace OmniRelay.Samples.ResourceLease.MeshDemo;

internal sealed class ResourceLeaseHttpClient
{
    private readonly HttpClient _httpClient;
    private readonly MeshDemoOptions _options;
    private static ResourceLeaseJsonContext JsonContext => ResourceLeaseJson.Context;

    public ResourceLeaseHttpClient(HttpClient httpClient, IOptions<MeshDemoOptions> options)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _options = options.Value;
    }

    public async Task<ResourceLeaseEnqueueResponse> EnqueueAsync(ResourceLeaseItemPayload payload, CancellationToken cancellationToken)
    {
        var request = new ResourceLeaseEnqueueRequest(payload);
        return await SendAsync<ResourceLeaseEnqueueRequest, ResourceLeaseEnqueueResponse>(
            "resourcelease.mesh::enqueue",
            request,
            JsonContext.ResourceLeaseEnqueueRequest,
            JsonContext.ResourceLeaseEnqueueResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ResourceLeaseLeaseResponse?> LeaseAsync(string peerId, CancellationToken cancellationToken)
    {
        var request = new ResourceLeaseLeaseRequest(peerId);
        return await SendAsync<ResourceLeaseLeaseRequest, ResourceLeaseLeaseResponse>(
            "resourcelease.mesh::lease",
            request,
            JsonContext.ResourceLeaseLeaseRequest,
            JsonContext.ResourceLeaseLeaseResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteAsync(ResourceLeaseOwnershipHandle ownership, CancellationToken cancellationToken)
    {
        var request = new ResourceLeaseCompleteRequest(ownership);
        await SendAsync<ResourceLeaseCompleteRequest, ResourceLeaseAcknowledgeResponse>(
            "resourcelease.mesh::complete",
            request,
            JsonContext.ResourceLeaseCompleteRequest,
            JsonContext.ResourceLeaseAcknowledgeResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task HeartbeatAsync(ResourceLeaseOwnershipHandle ownership, CancellationToken cancellationToken)
    {
        var request = new ResourceLeaseHeartbeatRequest(ownership);
        await SendAsync<ResourceLeaseHeartbeatRequest, ResourceLeaseAcknowledgeResponse>(
            "resourcelease.mesh::heartbeat",
            request,
            JsonContext.ResourceLeaseHeartbeatRequest,
            JsonContext.ResourceLeaseAcknowledgeResponse,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task FailAsync(ResourceLeaseOwnershipHandle ownership, bool requeue, string reason, CancellationToken cancellationToken)
    {
        var request = new ResourceLeaseFailRequest(
            ownership,
            reason,
            ErrorCode: "mesh.demo.failure",
            Requeue: requeue,
            Metadata: new Dictionary<string, string>
            {
                ["source"] = "mesh-demo"
            });

        await SendAsync<ResourceLeaseFailRequest, ResourceLeaseAcknowledgeResponse>(
            "resourcelease.mesh::fail",
            request,
            JsonContext.ResourceLeaseFailRequest,
            JsonContext.ResourceLeaseAcknowledgeResponse,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TResponse> SendAsync<TRequest, TResponse>(
        string procedure,
        TRequest body,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, _httpClient.BaseAddress);
        httpRequest.Headers.TryAddWithoutValidation("Rpc-Procedure", procedure);
        httpRequest.Headers.TryAddWithoutValidation("Rpc-Service", _options.ServiceName);
        httpRequest.Headers.TryAddWithoutValidation("Rpc-Encoding", "application/json");
        httpRequest.Headers.TryAddWithoutValidation("Rpc-Caller", "mesh-demo-host");
        httpRequest.Content = JsonContent.Create(body, requestTypeInfo);

        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidOperationException($"RPC '{procedure}' returned no payload.");
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}
