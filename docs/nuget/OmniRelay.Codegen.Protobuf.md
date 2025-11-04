# OmniRelay.Codegen.Protobuf

`OmniRelay.Codegen.Protobuf` provides the `protoc-gen-omnirelay-csharp` plugin that generates OmniRelay dispatcher registration helpers and strongly-typed clients from protobuf service definitions.

## Usage

```bash
dotnet publish src/OmniRelay.Codegen.Protobuf/OmniRelay.Codegen.Protobuf.csproj -c Release -o artifacts/codegen

protoc \
  --plugin=protoc-gen-omnirelay-csharp=artifacts/codegen/OmniRelay.Codegen.Protobuf \
  --omnirelay-csharp_out=Generated \
  --proto_path=Protos \
  Protos/test_service.proto
```

Generated files include:

- `Register<Service>` extension methods for the dispatcher.
- Strongly-typed service interfaces matching your protobuf contract.
- OmniRelay clients that wire codecs and middleware automatically.

For incremental generation inside MSBuild, consider the `OmniRelay.Codegen.Protobuf.Generator` analyzer package.

