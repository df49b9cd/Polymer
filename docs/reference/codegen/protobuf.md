# Protobuf Code Generation

OmniRelay ships a protoc plug-in, `protoc-gen-omnirelay-csharp`, that generates dispatcher registration helpers and typed clients on top of the runtime `ProtobufCodec`. This document outlines how to run the generator and how to consume the emitted code.

## Building the plug-in

The plug-in lives at `src/OmniRelay.Codegen.Protobuf/`. It is built automatically when you run `dotnet build` for the repository. The compiled assembly can be found under `src/OmniRelay.Codegen.Protobuf/bin/<Configuration>/net10.0/OmniRelay.Codegen.Protobuf.dll`.

If you need a self-contained binary, publish the project:

```bash
cd src/OmniRelay.Codegen.Protobuf
 dotnet publish -c Release
```

## Invoking protoc

Because the repo already depends on `Grpc.Tools`, you can add the plug-in invocation to a project file:

```xml
<ItemGroup>
  <PackageReference Include="Grpc.Tools" Version="2.71.0" />
  <Protobuf Include="Protos/test_service.proto" GrpcServices="None">
    <Generator>OmniRelayCSharp</Generator>
  </Protobuf>
</ItemGroup>

<Target Name="OmniRelayCodegen" BeforeTargets="BeforeCompile">
  <Exec Command="$(Protobuf_ProtocPath) --plugin=protoc-gen-omnirelay-csharp=$(SolutionDir)src/OmniRelay.Codegen.Protobuf/bin/$(Configuration)/net10.0/OmniRelay.Codegen.Protobuf.dll --omnirelay-csharp_out=$(ProjectDir)Generated $(ProtoRoot)Protos/test_service.proto" />
</Target>
```

Alternatively, call `protoc` directly:

```bash
protoc \
  --plugin=protoc-gen-omnirelay-csharp=src/OmniRelay.Codegen.Protobuf/bin/Debug/net10.0/OmniRelay.Codegen.Protobuf.dll \
  --omnirelay-csharp_out=Generated \
  --proto_path=Protos \
  Protos/test_service.proto
```

The generated C# file mirrors the proto namespace. See `tests/OmniRelay.Codegen.Tests/Generated/TestService.OmniRelay.g.cs` for a complete example.

## Roslyn incremental generator (MSBuild integration)

For projects that already produce [descriptor sets](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md) you can skip the `protoc` plug-in entirely and let MSBuild feed OmniRelay’s incremental generator. The generator ships from `src/OmniRelay.Codegen.Protobuf.Generator/` and can be consumed as a project reference or packaged analyzer.

1. Reference the generator as an analyzer:

   ```xml
   <ItemGroup>
     <ProjectReference Include="..\..\src\OmniRelay.Codegen.Protobuf.Generator\OmniRelay.Codegen.Protobuf.Generator.csproj"
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

3. Build the project. MSBuild writes the generated files under `obj/<tfm>/generated/OmniRelay.Codegen.Generator/...` and the types become available to your project just like the protoc plug-in output.

The `tests/OmniRelay.Codegen.Tests` project follows this pattern using `Protos/test_service.proto`, so CI exercises the descriptor-set flow on every run.

## Packaging the incremental generator

`src/OmniRelay.Codegen.Protobuf.Generator` is configured as an analyzer package. Running `dotnet pack` (or any build because `GeneratePackageOnBuild` is enabled) produces `OmniRelay.Codegen.Generator.<version>.nupkg` under `bin/<Configuration>/`. The package includes the generator plus its runtime dependencies under `analyzers/dotnet/cs`, so consuming projects only need a single `PackageReference` instead of manual analyzer wiring:

```xml
<ItemGroup>
  <PackageReference Include="OmniRelay.Codegen.Generator" Version="0.2.0" PrivateAssets="all" />
</ItemGroup>
```

Pre-release builds use the `0.x.y` version band. When OmniRelay reaches a stable release, align the generator's `VersionPrefix` with the runtime version in CI before pushing to NuGet (the project file is ready for `dotnet pack` in a release pipeline).

### CI/CD

`.github/workflows/publish-generator.yml` publishes the analyzer to GitHub Packages. It runs on:

- pushes of tags that match `generator-v*` (e.g., `generator-v0.2.0`)
- manual `workflow_dispatch` runs where you supply a `version` input.

The job restores dependencies, runs the full test suite, packs the analyzer with `/p:ContinuousIntegrationBuild=true`, and pushes the resulting `.nupkg` to `https://nuget.pkg.github.com/${repository_owner}/index.json` using the built-in `GITHUB_TOKEN` (which needs `packages: write` permissions). To publish:

1. Create and push a tag with the naming scheme above (or trigger the workflow manually and provide the desired semantic version).
2. Verify the GitHub Actions run succeeds; the package will appear under the repository's Packages tab and can be consumed with the `PackageReference` shown earlier.

## Runtime integration

Generated code exposes two entry points:

- `TestServiceOmniRelay.RegisterTestService(dispatcher, implementation)` wires the dispatcher to a service implementation that uses strongly-typed requests/responses.
- `TestServiceOmniRelay.CreateTestServiceClient(dispatcher, serviceName, outboundKey)` returns a lazy client that creates `UnaryClient`/`StreamClient`/etc only when the corresponding RPC is invoked.

All generated clients use `ProtobufCodec`, so as long as transports supply a Protobuf outbound the calls will negotiate the correct media type (see `ProtobufCodec` + HTTP metadata handlers).

## Tests

- Golden coverage: `tests/OmniRelay.Codegen.Tests/Codegen/ProtobufCodeGeneratorTests.cs`
- Integration coverage: `tests/OmniRelay.CodeGen.IntegrationTests/GrpcCodegenIntegrationTests.cs`

These tests regenerate the code for `Protos/test_service.proto`, ensure it matches the checked-in baseline, and exercise unary calls over both HTTP and gRPC.
