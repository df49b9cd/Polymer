# Protobuf Code Generation

Polymer ships a protoc plug-in, `protoc-gen-polymer-csharp`, that generates dispatcher registration helpers and typed clients on top of the runtime `ProtobufCodec`. This document outlines how to run the generator and how to consume the emitted code.

## Building the plug-in

The plug-in lives at `src/Polymer.Codegen.Protobuf/`. It is built automatically when you run `dotnet build` for the repository. The compiled assembly can be found under `src/Polymer.Codegen.Protobuf/bin/<Configuration>/net10.0/Polymer.Codegen.Protobuf.dll`.

If you need a self-contained binary, publish the project:

```bash
cd src/Polymer.Codegen.Protobuf
 dotnet publish -c Release
```

## Invoking protoc

Because the repo already depends on `Grpc.Tools`, you can add the plug-in invocation to a project file:

```xml
<ItemGroup>
  <PackageReference Include="Grpc.Tools" Version="2.71.0" />
  <Protobuf Include="Protos/test_service.proto" GrpcServices="None">
    <Generator>PolymerCSharp</Generator>
  </Protobuf>
</ItemGroup>

<Target Name="PolymerCodegen" BeforeTargets="BeforeCompile">
  <Exec Command="$(Protobuf_ProtocPath) --plugin=protoc-gen-PolymerCSharp=$(SolutionDir)src/Polymer.Codegen.Protobuf/bin/$(Configuration)/net10.0/Polymer.Codegen.Protobuf.dll --PolymerCSharp_out=$(ProjectDir)Generated $(ProtoRoot)Protos/test_service.proto" />
</Target>
```

Alternatively, call `protoc` directly:

```bash
protoc \
  --plugin=protoc-gen-polymer-csharp=src/Polymer.Codegen.Protobuf/bin/Debug/net10.0/Polymer.Codegen.Protobuf.dll \
  --polymer-csharp_out=Generated \
  --proto_path=Protos \
  Protos/test_service.proto
```

The generated C# file mirrors the proto namespace. See `tests/Polymer.Tests/Generated/TestService.Polymer.g.cs` for a complete example.

## Roslyn incremental generator (MSBuild integration)

For projects that already produce [descriptor sets](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md) you can skip the `protoc` plug-in entirely and let MSBuild feed Polymer’s incremental generator. The generator ships from `src/Polymer.Codegen.Protobuf.Generator/` and can be consumed as a project reference or packaged analyzer.

1. Reference the generator as an analyzer:

   ```xml
   <ItemGroup>
     <ProjectReference Include="..\..\src\Polymer.Codegen.Protobuf.Generator\Polymer.Codegen.Protobuf.Generator.csproj"
                       OutputItemType="Analyzer"
                       ReferenceOutputAssembly="true" />
   </ItemGroup>
   ```

2. Provide a descriptor set (`.pb`) via `AdditionalFiles`. The generator reads binary descriptor sets, so you can pre-generate them with `Grpc.Tools`/`protoc` or let MSBuild emit one during the build:

   ```xml
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

   The `Protobuf` item continues to emit DTOs, while the incremental generator consumes the descriptor set to create dispatcher/client helpers.

3. Build the project. MSBuild writes the generated files under `obj/<tfm>/generated/Polymer.Codegen.Protobuf.Generator/...` and the types become available to your project just like the protoc plug-in output.

The repository contains a working sample wired this way: `tests/Polymer.Tests/Projects/ProtobufIncrementalSample/`. It uses the `GenerateDescriptorSet` flow above and builds successfully with `dotnet build`.

## Packaging the incremental generator

`src/Polymer.Codegen.Protobuf.Generator` is configured as an analyzer package. Running `dotnet pack` (or any build because `GeneratePackageOnBuild` is enabled) produces `Polymer.Codegen.Protobuf.Generator.<version>.nupkg` under `bin/<Configuration>/`. The package includes the generator plus its runtime dependencies under `analyzers/dotnet/cs`, so consuming projects only need a single `PackageReference` instead of manual analyzer wiring:

```xml
<ItemGroup>
  <PackageReference Include="Polymer.Codegen.Protobuf.Generator" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>
```

Pre-release builds use the `0.x.y` version band. When Polymer reaches a stable release, align the generator's `VersionPrefix` with the Polymer runtime version in CI before pushing to NuGet (the project file is ready for `dotnet pack` in a release pipeline).

## Runtime integration

Generated code exposes two entry points:

- `TestServicePolymer.RegisterTestService(dispatcher, implementation)` wires the dispatcher to a service implementation that uses strongly-typed requests/responses.
- `TestServicePolymer.CreateTestServiceClient(dispatcher, serviceName, outboundKey)` returns a lazy client that creates `UnaryClient`/`StreamClient`/etc only when the corresponding RPC is invoked.

All generated clients use `ProtobufCodec`, so as long as transports supply a Protobuf outbound the calls will negotiate the correct media type (see `ProtobufCodec` + HTTP metadata handlers).

## Tests

- Golden coverage: `tests/Polymer.Tests/Codegen/ProtobufCodeGeneratorTests.cs`
- Integration coverage: `tests/Polymer.Tests/Codegen/GeneratedServiceIntegrationTests.cs`

These tests regenerate the code for `Protos/test_service.proto`, ensure it matches the checked-in baseline, and exercise unary calls over both HTTP and gRPC.
