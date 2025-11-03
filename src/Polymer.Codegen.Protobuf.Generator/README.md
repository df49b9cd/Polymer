# Polymer.Codegen.Protobuf.Generator

A Roslyn incremental generator that turns compiled protobuf descriptor sets (`.pb` files) into dispatcher registration helpers and typed Polymer clients.

## Usage

```xml
<ItemGroup>
  <PackageReference Include="Polymer.Codegen.Protobuf.Generator" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>

<ItemGroup>
  <Protobuf Include="Protos/test_service.proto"
            GrpcServices="None"
            GenerateDescriptorSet="true"
            DescriptorSetOutputPath="$(IntermediateOutputPath)protos/test_service.pb" />
  <AdditionalFiles Include="$(IntermediateOutputPath)protos/test_service.pb" />
</ItemGroup>

<Target Name="EnsureDescriptorDirectory" BeforeTargets="ProtobufCompile">
  <MakeDir Directories="$(IntermediateOutputPath)protos" />
</Target>
```

The package already contains the Polymer runtime dependencies it needs (including `Polymer.dll` and `Google.Protobuf.dll`).

## Versioning

Pre-release builds follow `0.x.y` until the API stabilises. Once we cut a stable release, the generator will match the Polymer runtime major/minor version.
