using System.Text.Json;
using Google.Protobuf;

namespace OmniRelay.Cli.Core;

// Hand-rolled encoder for the test echo payloads to keep NativeAOT-friendly protobuf support
// without relying on runtime descriptor loading.
public static partial class ProtobufPayloadRegistration
{
    private static bool manualRegistered;

    public static void RegisterManualEncoders()
    {
        if (manualRegistered)
        {
            return;
        }

        ProtobufPayloadRegistry.Instance.Register("echo.EchoRequest", EncodeEchoRequest);
        manualRegistered = true;
    }

    private static ReadOnlyMemory<byte> EncodeEchoRequest(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return EncodeMessage(string.Empty);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var message = document.RootElement.TryGetProperty("message", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

            return EncodeMessage(message);
        }
        catch (JsonException)
        {
            // Preserve existing behavior: surface the parser error to the caller.
            return EncodeMessage(string.Empty);
        }
    }

    private static ReadOnlyMemory<byte> EncodeMessage(string message)
    {
        var size = CodedOutputStream.ComputeTagSize(1) + CodedOutputStream.ComputeStringSize(message);
        var buffer = new byte[size];
        var writer = new CodedOutputStream(buffer);
        writer.WriteTag(1, WireFormat.WireType.LengthDelimited);
        writer.WriteString(message);
        writer.CheckNoSpaceLeft();
        return buffer;
    }
}
