namespace OmniRelay.Cli.Core;

/// <summary>
/// Entry point for generated protobuf encoders to register themselves with the CLI registry.
/// Source generators can emit a partial implementation of <see cref="RegisterAll"/>.
/// </summary>
public static partial class ProtobufPayloadRegistration
{
    public static void RegisterGenerated() => RegisterAll(ProtobufPayloadRegistry.Instance);

    static partial void RegisterAll(ProtobufPayloadRegistry registry);
}
