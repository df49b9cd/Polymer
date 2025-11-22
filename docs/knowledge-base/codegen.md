# Code Generation

## Protobuf Plug-in (`src/OmniRelay.Codegen.Protobuf`)
- Ships `protoc-gen-omnirelay-csharp`, a console plug-in invoked by `protoc` to emit OmniRelay RPC client/server stubs.
- Supports the same transport/middleware concepts as the runtime; generated code registers procedures with dispatcher-friendly metadata.

## Roslyn Incremental Generator (`src/OmniRelay.Codegen.Protobuf.Generator`)
- Exposes an analyzer package referenced by application projects. During compilation it scans `[OmniRelayService]` attributes, generates strongly-typed dispatchers/clients, and wires codecs/transports automatically.
- Analyzer output is also used by the CLI for validation (referenced via `ProjectReference` in `OmniRelay.IntegrationTests`).

## Usage Patterns
- In `csproj`, reference `OmniRelay.Codegen.Protobuf` (as a `DotNetCliToolReference` or via tooling manifest) to enable `dotnet omnirelay-protoc` pipelines.
- For Roslyn integration, add `<PackageReference Include="OmniRelay.Codegen.Protobuf.Generator" PrivateAssets="all" />` so generated files flow into builds without polluting consumers.
- Tests under `tests/OmniRelay.Codegen.Tests` and `tests/OmniRelay.Codegen.Protobuf.Core` exercise incremental builds plus stub correctness.

## Descriptor Support
- Integration tests in `tests/OmniRelay.Codegen.Tests` generate descriptor sets from `Protos/test_service.proto` and run them through the incremental generator to ensure current `.proto` contracts stay compatible.

Refer to README “Code generation” section and `docs/reference/codegen.md` (if present) for advanced configuration (custom namespaces, multi-module builds, analyzer diagnostics).
