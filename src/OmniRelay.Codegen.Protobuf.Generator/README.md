# OmniRelay.Codegen.Generator

`OmniRelay.Codegen.Generator` is the Roslyn incremental generator that powers OmniRelay's protobuf story. Namespaces remain under `OmniRelay.*` for now, but the package/assembly ships as `OmniRelay.Codegen.Generator`. It consumes descriptor sets (`.pb` files) and emits:

- `Register<Service>` extension methods that wire server implementations into a `Dispatcher`.
- Strongly typed service interfaces mirroring the protobuf service definition.
- Typed OmniRelay clients (`Create<Service>Client`) that wrap dispatcher outbounds with correct codecs for unary + streaming RPCs.

The NuGet package ships the generator plus its runtime dependencies (`OmniRelay.dll`, `OmniRelay.Codegen.Protobuf.Core.dll`, `Google.Protobuf.dll`) so consumers do not have to reference the runtime separately.

## Selecting HTTP/3-enabled outbounds in generated clients

Generated clients accept an optional outbound key. Bind an outbound in configuration that enables HTTP/3 (for example, `enableHttp3: true`, `requestVersion: "3.0"`, and `versionPolicy: "request-version-or-lower"`) and pass that outbound key to the generated client:

```csharp
var client = PaymentsOmniRelay.CreatePaymentsClient(dispatcher, service: "payments", outboundKey: "grpc-h3");
```

This keeps generated code transport-agnostic while letting configuration control HTTP/3 negotiation and fallback.

## Using The protoc Plugin

The console project in `src/OmniRelay.Codegen.Protobuf` builds `protoc-gen-omnirelay-csharp`. Point `protoc` (or `Grpc.Tools`) at the published binary to generate C# during your build:

```bash
dotnet publish src/OmniRelay.Codegen.Protobuf/OmniRelay.Codegen.Protobuf.csproj -c Release -o artifacts/codegen

protoc \
  --plugin=protoc-gen-omnirelay-csharp=artifacts/codegen/OmniRelay.Codegen.Protobuf \
  --omnirelay-csharp_out=Generated \
  --proto_path=Protos \
  Protos/test_service.proto
```

The generated files can be added to your project directly, or referenced through MSBuild `<Compile Include="Generated/**/*.cs" />`.

## Using The Incremental Generator

Reference the incremental generator as an analyzer and surface descriptor sets via `AdditionalFiles`. The sample below mirrors the pattern used in `tests/OmniRelay.Codegen.Tests`:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\src\OmniRelay.Codegen.Protobuf.Generator\OmniRelay.Codegen.Protobuf.Generator.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="true" />
</ItemGroup>

<ItemGroup>
  <PackageReference Include="Grpc.Tools" Version="2.71.0">
    <PrivateAssets>all</PrivateAssets>
    <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
  </PackageReference>
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

The generator watches the descriptor set and services regenerate on every design-time/build invocation; no extra build steps are required. Because the generator includes OmniRelay runtime assemblies, the emitted clients compile without additional references.

## Generated Surface

For each protobuf service, the generator emits:

- A server registration helper: `dispatcher.Register<Namespace><Service>(ITestService implementation)` that registers unary + streaming handlers with codecs and metadata configured.
- A service interface that expresses the dispatcher-friendly method signatures.
- A typed client exposing `Unary`, `ServerStream`, `ClientStream`, and `DuplexStream` methods that internally call `Dispatcher.Create*Client`.
- Codec fields (JSON/protobuf) scoped per RPC to avoid redundant allocations.

See `tests/OmniRelay.Codegen.Tests/Generated/TestService.OmniRelay.g.cs` for the full emitted shape.

## Versioning

Pre-release builds follow `0.x.y` while we stabilise the generator APIs. Once OmniRelay reaches a stable release, generator versions will align with the OmniRelay runtime major/minor version.
