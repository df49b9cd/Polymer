using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;

namespace OmniRelay.Diagnostics;

internal sealed class OmniRelayMetadataDocumentTransformer : IOpenApiDocumentTransformer
{
    private readonly IOptions<OmniRelayDocumentationOptions> _options;

    public OmniRelayMetadataDocumentTransformer(IOptions<OmniRelayDocumentationOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var metadata = _options.Value?.Metadata;
        if (metadata is null || metadata.Count == 0)
        {
            return Task.CompletedTask;
        }

        var info = document.Info;
        if (info is null)
        {
            return Task.CompletedTask;
        }

        info.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
        foreach (var pair in metadata)
        {
            info.Extensions[pair.Key] = new MetadataStringExtension(pair.Value);
        }

        return Task.CompletedTask;
    }

    private sealed class MetadataStringExtension : IOpenApiExtension
    {
        private readonly string _value;

        public MetadataStringExtension(string value) => _value = value ?? string.Empty;

        public void Write(IOpenApiWriter writer, OpenApiSpecVersion specVersion)
        {
            ArgumentNullException.ThrowIfNull(writer);
            writer.WriteValue(_value);
        }
    }
}
