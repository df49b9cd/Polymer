# OmniRelay.Codegen.Protobuf.Generator

`OmniRelay.Codegen.Protobuf.Generator` is a Roslyn incremental generator that consumes protobuf descriptor sets (`.pb`) and emits OmniRelay dispatcher registration helpers, service interfaces, and typed clients during compilation.

## Getting Started

```xml
<ItemGroup>
  <PackageReference Include="OmniRelay.Codegen.Protobuf.Generator" Version="0.*" PrivateAssets="all" />
</ItemGroup>

<ItemGroup>
  <Protobuf Include="Protos/*.proto"
            GrpcServices="None"
            GenerateDescriptorSet="true"
            DescriptorSetOutputPath="$(IntermediateOutputPath)protos/service.pb" />
  <AdditionalFiles Include="$(IntermediateOutputPath)protos/service.pb" />
</ItemGroup>
```

The generator places output under `obj/<tfm>/generated/OmniRelay.Codegen.Generator/` and integrates seamlessly with OmniRelay runtime assemblies for immediate consumption.

See `docs/reference/codegen/protobuf.md` for a complete walkthrough and `tests/OmniRelay.Codegen.Tests` for a working descriptor-set example used in CI.
