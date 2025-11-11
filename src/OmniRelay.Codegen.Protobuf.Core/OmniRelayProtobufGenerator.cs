using System.Text;
using Google.Protobuf.Compiler;
using Google.Protobuf.Reflection;

namespace OmniRelay.Codegen.Protobuf.Core;

public sealed class OmniRelayProtobufGenerator
{
    public static CodeGeneratorResponse Generate(CodeGeneratorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var response = new CodeGeneratorResponse
        {
            SupportedFeatures = (ulong)CodeGeneratorResponse.Types.Feature.Proto3Optional
        };

        foreach (var file in GenerateFilesInternal(request))
        {
            response.File.Add(new CodeGeneratorResponse.Types.File
            {
                Name = file.Name,
                Content = file.Content
            });
        }

        return response;
    }

    public static IEnumerable<GeneratedFile> GenerateFiles(CodeGeneratorRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return GenerateFilesInternal(request);
    }

    public static IEnumerable<GeneratedFile> GenerateFiles(FileDescriptorSet descriptorSet, IEnumerable<string>? filesToGenerate = null)
    {
        ArgumentNullException.ThrowIfNull(descriptorSet);

        var request = new CodeGeneratorRequest();
        request.ProtoFile.Add(descriptorSet.File);

        if (filesToGenerate is null)
        {
            request.FileToGenerate.AddRange(descriptorSet.File.Select(static file => file.Name));
        }
        else
        {
            request.FileToGenerate.AddRange(filesToGenerate);
        }

        return GenerateFilesInternal(request);
    }

    private static IEnumerable<GeneratedFile> GenerateFilesInternal(CodeGeneratorRequest request)
    {
        var fileContexts = BuildFileContexts(request);
        var typeLookup = BuildTypeLookup(fileContexts);

        foreach (var fileName in request.FileToGenerate)
        {
            if (!fileContexts.TryGetValue(fileName, out var context))
            {
                throw new InvalidOperationException($"Unable to locate descriptor for proto '{fileName}'.");
            }

            if (context.Services.Count == 0)
            {
                continue;
            }

            foreach (var service in context.Services)
            {
                var emitter = new ServiceEmitter(context, service, typeLookup);
                yield return emitter.Emit();
            }
        }
    }

    private static Dictionary<string, ProtoFileContext> BuildFileContexts(CodeGeneratorRequest request)
    {
        var map = new Dictionary<string, ProtoFileContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in request.ProtoFile)
        {
            var context = new ProtoFileContext(file, ResolveNamespace(file));
            map[file.Name] = context;
        }

        return map;
    }

    private static Dictionary<string, TypeInfo> BuildTypeLookup(Dictionary<string, ProtoFileContext> contexts)
    {
        var lookup = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);

        foreach (var context in contexts.Values)
        {
            foreach (var (protoName, csharpName) in context.TypeNameMap)
            {
                lookup[protoName] = new TypeInfo(context.Namespace, csharpName);
            }
        }

        return lookup;
    }

    private sealed class ProtoFileContext(FileDescriptorProto file, string ns)
    {
        public FileDescriptorProto File { get; } = file ?? throw new ArgumentNullException(nameof(file));

        public string Namespace { get; } = ns ?? throw new ArgumentNullException(nameof(ns));

        public IReadOnlyList<ServiceDescriptorProto> Services { get; } = [.. file.Service];

        public IReadOnlyDictionary<string, string> TypeNameMap { get; } = BuildTypeNameMap(file);

        private static Dictionary<string, string> BuildTypeNameMap(FileDescriptorProto file)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            var prefix = string.IsNullOrWhiteSpace(file.Package) ? string.Empty : file.Package;

            foreach (var message in file.MessageType)
            {
                CollectMessageTypes(message, prefix, null, map);
            }

            return map;
        }

        private static void CollectMessageTypes(DescriptorProto message, string? protoPrefix, string? csharpPrefix, IDictionary<string, string> map)
        {
            var protoName = string.IsNullOrEmpty(protoPrefix)
                ? message.Name
                : string.Concat(protoPrefix, ".", message.Name);

            var csharpName = string.IsNullOrEmpty(csharpPrefix)
                ? message.Name
                : string.Concat(csharpPrefix, ".Types.", message.Name);

            map[protoName] = csharpName;

            foreach (var nested in message.NestedType)
            {
                CollectMessageTypes(nested, protoName, csharpName, map);
            }
        }
    }

    private sealed record TypeInfo(string Namespace, string CsharpName)
    {
        public string Namespace { get; init; } = Namespace;

        public string CsharpName { get; init; } = CsharpName;
    }

    private sealed class ServiceEmitter(
        ProtoFileContext fileContext,
        ServiceDescriptorProto service,
        Dictionary<string, TypeInfo> typeLookup)
    {
        private readonly ProtoFileContext _fileContext = fileContext ?? throw new ArgumentNullException(nameof(fileContext));
        private readonly ServiceDescriptorProto _service = service ?? throw new ArgumentNullException(nameof(service));
        private readonly Dictionary<string, TypeInfo> _typeLookup = typeLookup ?? throw new ArgumentNullException(nameof(typeLookup));

        public GeneratedFile Emit()
        {
            var builder = new IndentedStringBuilder();
            builder.AppendLine("// <auto-generated>");
            builder.AppendLine("// Generated by protoc-gen-omnirelay-csharp. Do not edit.");
            builder.AppendLine("// </auto-generated>");
            builder.AppendLine("#nullable enable");
            builder.AppendLine();
            builder.AppendLine("using System;");
            builder.AppendLine("using System.Collections.Generic;");
            builder.AppendLine("using System.Threading;");
            builder.AppendLine("using System.Threading.Tasks;");
            builder.AppendLine("using OmniRelay.Core;");
            builder.AppendLine("using OmniRelay.Core.Clients;");
            builder.AppendLine("using OmniRelay.Core.Transport;");
            builder.AppendLine("using Hugo;");
            builder.AppendLine("using OmniRelay.Dispatcher;");
            builder.AppendLine("using static Hugo.Go;");
            builder.AppendLine();
            builder.AppendLine($"namespace {_fileContext.Namespace};");
            builder.AppendLine();

            var className = $"{_service.Name}OmniRelay";
            builder.AppendLine($"public static class {className}");
            builder.AppendLine("{");
            builder.PushIndent();

            var methods = BuildMethodModels();
            foreach (var method in methods)
            {
                builder.AppendLine($"private static readonly ProtobufCodec<{method.InputType}, {method.OutputType}> {method.CodecFieldName} = new(defaultEncoding: \"protobuf\");");
            }

            builder.AppendLine();
            GenerateRegisterMethod(builder, methods);
            builder.AppendLine();
            GenerateInterface(builder, methods);
            builder.AppendLine();
            GenerateClientClass(builder, methods);
            builder.AppendLine();
            GenerateClientFactory(builder);
            builder.PopIndent();
            builder.AppendLine("}");

            var fileName = BuildOutputFileName();
            return new GeneratedFile(fileName, builder.ToString());
        }

        private string BuildOutputFileName()
        {
            var baseName = Path.GetFileNameWithoutExtension(_fileContext.File.Name);
            var directory = Path.GetDirectoryName(_fileContext.File.Name);
            var fileName = $"{baseName}.{_service.Name}.OmniRelay.g.cs";

            if (string.IsNullOrEmpty(directory))
            {
                return fileName.Replace('\\', '/');
            }

            return Path.Combine(directory, fileName).Replace('\\', '/');
        }

        private List<MethodModel> BuildMethodModels()
        {
            var models = new List<MethodModel>(_service.Method.Count);
            foreach (var method in _service.Method)
            {
                var kind = RpcKind.Unary;
                if (method.ClientStreaming && method.ServerStreaming)
                {
                    kind = RpcKind.DuplexStreaming;
                }
                else if (method.ClientStreaming)
                {
                    kind = RpcKind.ClientStreaming;
                }
                else if (method.ServerStreaming)
                {
                    kind = RpcKind.ServerStreaming;
                }

                var inputType = ResolveClrType(method.InputType);
                var outputType = ResolveClrType(method.OutputType);

                var methodName = method.Name;
                var handlerName = methodName + "Async";
                var sanitized = SanitizeIdentifier(methodName);

                var codecField = $"__{sanitized}Codec";
                var unaryField = $"_{ToCamelCase(methodName)}UnaryClient";
                var streamField = $"_{ToCamelCase(methodName)}StreamClient";
                var clientStreamField = $"_{ToCamelCase(methodName)}ClientStream";
                var duplexField = $"_{ToCamelCase(methodName)}DuplexClient";

                models.Add(new MethodModel(
                    methodName,
                    methodName,
                    inputType,
                    outputType,
                    kind,
                    handlerName,
                    codecField,
                    unaryField,
                    streamField,
                    clientStreamField,
                    duplexField));
            }

            return models;
        }

        private string ResolveClrType(string protoType)
        {
            var normalized = protoType.TrimStart('.');
            if (!_typeLookup.TryGetValue(normalized, out var info))
            {
                throw new InvalidOperationException($"Unknown proto type '{protoType}' referenced by {_service.Name}.");
            }

            return $"global::{info.Namespace}.{info.CsharpName}";
        }

        private void GenerateRegisterMethod(IndentedStringBuilder builder, IReadOnlyList<MethodModel> methods)
        {
            builder.AppendLine($"public static void Register{_service.Name}(this global::OmniRelay.Dispatcher.Dispatcher dispatcher, I{_service.Name} implementation)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("ArgumentNullException.ThrowIfNull(dispatcher);");
            builder.AppendLine("ArgumentNullException.ThrowIfNull(implementation);");
            builder.AppendLine();

            foreach (var method in methods)
            {
                switch (method.Kind)
                {
                    case RpcKind.Unary:
                        EmitRegistration(builder, "RegisterUnary", method, "CreateUnaryHandler");
                        break;
                    case RpcKind.ServerStreaming:
                        EmitRegistration(builder, "RegisterStream", method, "CreateServerStreamHandler");
                        break;
                    case RpcKind.ClientStreaming:
                        EmitRegistration(builder, "RegisterClientStream", method, "CreateClientStreamHandler");
                        break;
                    case RpcKind.DuplexStreaming:
                        EmitRegistration(builder, "RegisterDuplex", method, "CreateDuplexHandler");
                        break;
                }

                builder.AppendLine();
            }

            builder.PopIndent();
            builder.AppendLine("}");
        }

        private static void EmitRegistration(IndentedStringBuilder builder, string methodName, MethodModel method, string adapterFactory)
        {
            builder.AppendLine($"dispatcher.{methodName}(\"{method.ProcedureName}\", builder =>");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine($"builder.WithEncoding({method.CodecFieldName}.Encoding);");
            builder.AppendLine($"builder.Handle(ProtobufCallAdapters.{adapterFactory}({method.CodecFieldName}, implementation.{method.HandlerName}));");
            builder.PopIndent();
            builder.AppendLine("}).ThrowIfFailure();");
        }

        private void GenerateInterface(IndentedStringBuilder builder, IReadOnlyList<MethodModel> methods)
        {
            builder.AppendLine($"public interface I{_service.Name}");
            builder.AppendLine("{");
            builder.PushIndent();

            foreach (var method in methods)
            {
                var signature = method.Kind switch
                {
                    RpcKind.Unary => $"ValueTask<Response<{method.OutputType}>> {method.HandlerName}(Request<{method.InputType}> request, CancellationToken cancellationToken)",
                    RpcKind.ServerStreaming => $"ValueTask {method.HandlerName}(Request<{method.InputType}> request, ProtobufCallAdapters.ProtobufServerStreamWriter<{method.InputType}, {method.OutputType}> stream, CancellationToken cancellationToken)",
                    RpcKind.ClientStreaming => $"ValueTask<Response<{method.OutputType}>> {method.HandlerName}(ProtobufCallAdapters.ProtobufClientStreamContext<{method.InputType}, {method.OutputType}> context, CancellationToken cancellationToken)",
                    RpcKind.DuplexStreaming => $"ValueTask {method.HandlerName}(ProtobufCallAdapters.ProtobufDuplexStreamContext<{method.InputType}, {method.OutputType}> context, CancellationToken cancellationToken)",
                    _ => throw new ArgumentOutOfRangeException(nameof(methods), method.Kind, null)
                };

                builder.AppendLine($"{signature};");
            }

            builder.PopIndent();
            builder.AppendLine("}");
        }

        private void GenerateClientClass(IndentedStringBuilder builder, IReadOnlyList<MethodModel> methods)
        {
            builder.AppendLine($"public sealed class {_service.Name}Client");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("private readonly global::OmniRelay.Dispatcher.Dispatcher _dispatcher;");
            builder.AppendLine("private readonly string _service;");
            builder.AppendLine("private readonly string? _outboundKey;");

            foreach (var method in methods)
            {
                switch (method.Kind)
                {
                    case RpcKind.Unary:
                        builder.AppendLine($"private UnaryClient<{method.InputType}, {method.OutputType}>? {method.UnaryClientField};");
                        break;
                    case RpcKind.ServerStreaming:
                        builder.AppendLine($"private StreamClient<{method.InputType}, {method.OutputType}>? {method.StreamClientField};");
                        break;
                    case RpcKind.ClientStreaming:
                        builder.AppendLine($"private ClientStreamClient<{method.InputType}, {method.OutputType}>? {method.ClientStreamField};");
                        break;
                    case RpcKind.DuplexStreaming:
                        builder.AppendLine($"private DuplexStreamClient<{method.InputType}, {method.OutputType}>? {method.DuplexClientField};");
                        break;
                }
            }

            builder.AppendLine();
            builder.AppendLine($"internal {_service.Name}Client(global::OmniRelay.Dispatcher.Dispatcher dispatcher, string service, string? outboundKey)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("ArgumentNullException.ThrowIfNull(dispatcher);");
            builder.AppendLine("ArgumentException.ThrowIfNullOrWhiteSpace(service);");
            builder.AppendLine("_dispatcher = dispatcher;");
            builder.AppendLine("_service = service;");
            builder.AppendLine("_outboundKey = outboundKey;");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();

            foreach (var method in methods)
            {
                switch (method.Kind)
                {
                    case RpcKind.Unary:
                        EmitUnaryClientMethod(builder, method);
                        break;
                    case RpcKind.ServerStreaming:
                        EmitStreamClientMethod(builder, method);
                        break;
                    case RpcKind.ClientStreaming:
                        EmitClientStreamClientMethod(builder, method);
                        break;
                    case RpcKind.DuplexStreaming:
                        EmitDuplexClientMethod(builder, method);
                        break;
                }
            }

            AppendPrepareRequestMetaHelper(builder);

            builder.PopIndent();
            builder.AppendLine("}");
        }

        private static void EmitUnaryClientMethod(IndentedStringBuilder builder, MethodModel method)
        {
            builder.AppendLine($"public ValueTask<Result<Response<{method.OutputType}>>> {method.HandlerName}({method.InputType} request, RequestMeta? meta = null, CancellationToken cancellationToken = default)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine($"var requestMeta = PrepareRequestMeta(meta, _service, \"{method.ProcedureName}\", {method.CodecFieldName}.Encoding);");
            builder.AppendLine($"var client = {method.UnaryClientField} ??= _dispatcher.CreateUnaryClient<{method.InputType}, {method.OutputType}>(_service, {method.CodecFieldName}, _outboundKey);");
            builder.AppendLine($"return client.CallAsync(new Request<{method.InputType}>(requestMeta, request), cancellationToken);");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();
        }

        private static void EmitStreamClientMethod(IndentedStringBuilder builder, MethodModel method)
        {
            builder.AppendLine($"public IAsyncEnumerable<Result<Response<{method.OutputType}>>> {method.HandlerName}({method.InputType} request, RequestMeta? meta = null, CancellationToken cancellationToken = default)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine($"var requestMeta = PrepareRequestMeta(meta, _service, \"{method.ProcedureName}\", {method.CodecFieldName}.Encoding);");
            builder.AppendLine($"var client = {method.StreamClientField} ??= _dispatcher.CreateStreamClient<{method.InputType}, {method.OutputType}>(_service, {method.CodecFieldName}, _outboundKey);");
            builder.AppendLine($"return client.CallAsync(new Request<{method.InputType}>(requestMeta, request), new StreamCallOptions(StreamDirection.Server), cancellationToken);");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();
        }

        private static void EmitClientStreamClientMethod(IndentedStringBuilder builder, MethodModel method)
        {
            builder.AppendLine($"public ValueTask<Result<ClientStreamClient<{method.InputType}, {method.OutputType}>.ClientStreamSession>> {method.HandlerName}(RequestMeta? meta = null, CancellationToken cancellationToken = default)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine($"var requestMeta = PrepareRequestMeta(meta, _service, \"{method.ProcedureName}\", {method.CodecFieldName}.Encoding);");
            builder.AppendLine($"var client = {method.ClientStreamField} ??= _dispatcher.CreateClientStreamClient<{method.InputType}, {method.OutputType}>(_service, {method.CodecFieldName}, _outboundKey);");
            builder.AppendLine("return client.StartAsync(requestMeta, cancellationToken);");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();
        }

        private static void EmitDuplexClientMethod(IndentedStringBuilder builder, MethodModel method)
        {
            builder.AppendLine($"public ValueTask<Result<DuplexStreamClient<{method.InputType}, {method.OutputType}>.DuplexStreamSession>> {method.HandlerName}(RequestMeta? meta = null, CancellationToken cancellationToken = default)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine($"var requestMeta = PrepareRequestMeta(meta, _service, \"{method.ProcedureName}\", {method.CodecFieldName}.Encoding);");
            builder.AppendLine($"var client = {method.DuplexClientField} ??= _dispatcher.CreateDuplexStreamClient<{method.InputType}, {method.OutputType}>(_service, {method.CodecFieldName}, _outboundKey);");
            builder.AppendLine("return client.StartAsync(requestMeta, cancellationToken);");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();
        }

        private void GenerateClientFactory(IndentedStringBuilder builder)
        {
            builder.AppendLine($"public static {_service.Name}Client Create{_service.Name}Client(global::OmniRelay.Dispatcher.Dispatcher dispatcher, string service, string? outboundKey = null)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("ArgumentNullException.ThrowIfNull(dispatcher);");
            builder.AppendLine("ArgumentException.ThrowIfNullOrWhiteSpace(service);");
            builder.AppendLine($"return new {_service.Name}Client(dispatcher, service, outboundKey);");
            builder.PopIndent();
            builder.AppendLine("}");
        }

        private static void AppendPrepareRequestMetaHelper(IndentedStringBuilder builder)
        {
            builder.AppendLine("private static RequestMeta PrepareRequestMeta(RequestMeta? meta, string service, string procedure, string encoding)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("if (meta is null)");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("return new RequestMeta(service: service, procedure: procedure, encoding: encoding);");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine();
            builder.AppendLine("var value = meta;");
            builder.AppendLine("if (string.IsNullOrWhiteSpace(value.Service))");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("value = value with { Service = service };");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine("if (string.IsNullOrWhiteSpace(value.Procedure))");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("value = value with { Procedure = procedure };");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine("if (string.IsNullOrWhiteSpace(value.Encoding))");
            builder.AppendLine("{");
            builder.PushIndent();
            builder.AppendLine("value = value with { Encoding = encoding };");
            builder.PopIndent();
            builder.AppendLine("}");
            builder.AppendLine("return value;");
            builder.PopIndent();
            builder.AppendLine("}");
        }

        private static string ToCamelCase(string identifier)
        {
            if (string.IsNullOrEmpty(identifier))
            {
                return identifier;
            }

            if (identifier.Length == 1)
            {
                return identifier.ToLowerInvariant();
            }

            return char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);
        }

        private enum RpcKind
        {
            Unary,
            ServerStreaming,
            ClientStreaming,
            DuplexStreaming
        }

        private sealed record MethodModel(
            string Name,
            string ProcedureName,
            string InputType,
            string OutputType,
            RpcKind Kind,
            string HandlerName,
            string CodecFieldName,
            string UnaryClientField,
            string StreamClientField,
            string ClientStreamField,
            string DuplexClientField)
        {
            public string Name { get; init; } = Name;

            public string ProcedureName { get; init; } = ProcedureName;

            public string InputType { get; init; } = InputType;

            public string OutputType { get; init; } = OutputType;

            public RpcKind Kind { get; init; } = Kind;

            public string HandlerName { get; init; } = HandlerName;

            public string CodecFieldName { get; init; } = CodecFieldName;

            public string UnaryClientField { get; init; } = UnaryClientField;

            public string StreamClientField { get; init; } = StreamClientField;

            public string ClientStreamField { get; init; } = ClientStreamField;

            public string DuplexClientField { get; init; } = DuplexClientField;
        }
    }

    private static string ResolveNamespace(FileDescriptorProto file)
    {
        var optionNamespace = file.Options?.CsharpNamespace;
        if (!string.IsNullOrWhiteSpace(optionNamespace))
        {
            return optionNamespace!;
        }

        if (string.IsNullOrWhiteSpace(file.Package))
        {
            return "Protobuf";
        }

        var segments = file.Package.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeIdentifier)
            .Select(ToPascalCase);
        return string.Join('.', segments);
    }

    private static string SanitizeIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        }

        return builder.ToString();
    }

    private static string ToPascalCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var builder = new StringBuilder();
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                continue;
            }

            builder.Append(char.ToUpperInvariant(part[0]));
            if (part.Length > 1)
            {
                builder.Append(part.AsSpan(1));
            }
        }

        return builder.Length == 0 ? value : builder.ToString();
    }

    private sealed class IndentedStringBuilder
    {
        private readonly StringBuilder _builder = new();
        private int _indentLevel;
        private const string Indent = "    ";

        public void PushIndent() => _indentLevel++;

        public void PopIndent()
        {
            if (_indentLevel > 0)
            {
                _indentLevel--;
            }
        }

        public void AppendLine() => _builder.AppendLine();

        public void AppendLine(string text)
        {
            if (text.Length == 0)
            {
                _builder.AppendLine();
                return;
            }

            for (var i = 0; i < _indentLevel; i++)
            {
                _builder.Append(Indent);
            }

            _builder.AppendLine(text);
        }

        public override string ToString() => _builder.ToString();
    }
}

public sealed record GeneratedFile(string Name, string Content)
{
    public string Name { get; init; } = Name;

    public string Content { get; init; } = Content;
}
