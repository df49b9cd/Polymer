using System.Diagnostics;
using Google.Protobuf;
using Google.Protobuf.Compiler;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OmniRelay.Codegen.Protobuf.Generator;
using Xunit;
using FileOptions = Google.Protobuf.Reflection.FileOptions;

namespace OmniRelay.Tests.Codegen;

public class ProtobufCodeGeneratorTests
{
    [Fact]
    public void Generated_Code_Matches_Golden_File()
    {
        var request = CodeGeneratorRequestFactory.Create();
        var response = CodeGeneratorProcessRunner.Execute(request);

        Assert.Single(response.File);
        var generated = response.File[0].Content.Replace("\r\n", "\n");
        var goldenPath = TestPath.Combine("tests", "OmniRelay.Codegen.Tests", "Generated", "TestService.OmniRelay.g.cs");
        File.WriteAllText(TestPath.Combine("tests", "OmniRelay.Codegen.Tests", "Generated", "TestService.actual.g.cs"), response.File[0].Content);
        var expected = File.ReadAllText(goldenPath).Replace("\r\n", "\n");

        Assert.Equal(expected, generated);
    }

    [Fact]
    public void IncrementalGenerator_Produces_Golden_File()
    {
        var descriptorSet = CodeGeneratorRequestFactory.CreateDescriptorSet();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "yarpcore-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        var descriptorPath = Path.Combine(tempDirectory, "test_service.pb");

        var cancellationToken = TestContext.Current.CancellationToken;

        try
        {
            File.WriteAllBytes(descriptorPath, descriptorSet.ToByteArray());

            var generator = new ProtobufIncrementalGenerator();
            var additionalTexts = new AdditionalText[] { new DescriptorAdditionalText(descriptorPath) };
            var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], additionalTexts: additionalTexts);

            var compilation = CSharpCompilation.Create(
                assemblyName: "OmniRelay.Codegen.Tests",
                references: [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            driver = (CSharpGeneratorDriver)driver.RunGenerators(compilation, cancellationToken);
            var runResult = driver.GetRunResult();

            Assert.True(runResult.GeneratedTrees.Length > 0, "Generator did not produce any output.");

            var generatedRaw = runResult.GeneratedTrees[0].GetText(TestContext.Current.CancellationToken).ToString();
            File.WriteAllText(TestPath.Combine("tests", "OmniRelay.Codegen.Tests", "Generated", "TestService.incremental.g.cs"), generatedRaw);
            var generatedText = generatedRaw.Replace("\r\n", "\n");
            var expected = File.ReadAllText(TestPath.Combine("tests", "OmniRelay.Codegen.Tests", "Generated", "TestService.OmniRelay.g.cs")).Replace("\r\n", "\n");

            Assert.Equal(expected, generatedText);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
        }
    }

    private static class CodeGeneratorRequestFactory
    {
        public static CodeGeneratorRequest Create()
        {
            var file = CreateFileDescriptor();
            var request = new CodeGeneratorRequest();
            request.FileToGenerate.Add(file.Name);
            request.ProtoFile.Add(file);
            return request;
        }

        public static FileDescriptorSet CreateDescriptorSet()
        {
            var set = new FileDescriptorSet();
            set.File.Add(CreateFileDescriptor());
            return set;
        }

        private static FileDescriptorProto CreateFileDescriptor()
        {
            var file = new FileDescriptorProto
            {
                Name = "tests/OmniRelay.Codegen.Tests/Protos/test_service.proto",
                Package = "yarpcore.tests.codegen",
                Options = new FileOptions { CsharpNamespace = "OmniRelay.Tests.Protos" }
            };

            file.MessageType.Add(CreateMessage("UnaryRequest", ("message", FieldDescriptorProto.Types.Type.String)));
            file.MessageType.Add(CreateMessage("UnaryResponse", ("message", FieldDescriptorProto.Types.Type.String)));
            file.MessageType.Add(CreateMessage("StreamRequest", ("value", FieldDescriptorProto.Types.Type.String)));
            file.MessageType.Add(CreateMessage("StreamResponse", ("value", FieldDescriptorProto.Types.Type.String)));

            var service = new ServiceDescriptorProto { Name = "TestService" };
            service.Method.Add(new MethodDescriptorProto
            {
                Name = "UnaryCall",
                InputType = ".yarpcore.tests.codegen.UnaryRequest",
                OutputType = ".yarpcore.tests.codegen.UnaryResponse"
            });
            service.Method.Add(new MethodDescriptorProto
            {
                Name = "ServerStream",
                InputType = ".yarpcore.tests.codegen.StreamRequest",
                OutputType = ".yarpcore.tests.codegen.StreamResponse",
                ServerStreaming = true
            });
            service.Method.Add(new MethodDescriptorProto
            {
                Name = "ClientStream",
                InputType = ".yarpcore.tests.codegen.StreamRequest",
                OutputType = ".yarpcore.tests.codegen.UnaryResponse",
                ClientStreaming = true
            });
            service.Method.Add(new MethodDescriptorProto
            {
                Name = "DuplexStream",
                InputType = ".yarpcore.tests.codegen.StreamRequest",
                OutputType = ".yarpcore.tests.codegen.StreamResponse",
                ClientStreaming = true,
                ServerStreaming = true
            });

            file.Service.Add(service);
            return file;
        }

        private static DescriptorProto CreateMessage(string name, params (string Name, FieldDescriptorProto.Types.Type Type)[] fields)
        {
            var descriptor = new DescriptorProto { Name = name };
            uint number = 1;
            foreach (var field in fields)
            {
                descriptor.Field.Add(new FieldDescriptorProto
                {
                    Name = field.Name,
                    Number = (int)number++,
                    Label = FieldDescriptorProto.Types.Label.Optional,
                    Type = field.Type
                });
            }

            return descriptor;
        }
    }

    private static class CodeGeneratorProcessRunner
    {
        public static CodeGeneratorResponse Execute(CodeGeneratorRequest request)
        {
            var pluginPath = LocatePluginAssembly();
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"\"{pluginPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true
            };

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start protoc-gen-polymer-csharp.");

            using (var codedOutput = new CodedOutputStream(process.StandardInput.BaseStream, leaveOpen: true))
            {
                request.WriteTo(codedOutput);
                codedOutput.Flush();
            }

            process.StandardInput.Close();
            var response = CodeGeneratorResponse.Parser.ParseFrom(process.StandardOutput.BaseStream);
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Code generator exited with code {process.ExitCode}. Error: {response.Error}");
            }

            return response;
        }

        private static string LocatePluginAssembly()
        {
            var solutionRoot = TestPath.Root;
            var pluginDirectory = Path.Combine(solutionRoot, "src", "OmniRelay.Codegen.Protobuf", "bin");
            if (!Directory.Exists(pluginDirectory))
            {
                throw new DirectoryNotFoundException($"Plug-in build directory not found: {pluginDirectory}");
            }

            var candidates = new[]
            {
                Path.Combine(pluginDirectory, "Debug", "net10.0", "OmniRelay.Codegen.Protobuf.dll"),
                Path.Combine(pluginDirectory, "Release", "net10.0", "OmniRelay.Codegen.Protobuf.dll")
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            var matches = Directory.EnumerateFiles(pluginDirectory, "OmniRelay.Codegen.Protobuf.dll", SearchOption.AllDirectories)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}net10.0{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            if (matches.Count == 0)
            {
                throw new FileNotFoundException("Unable to locate OmniRelay.Codegen.Protobuf.dll. Ensure the project was built.");
            }

            return matches[0];
        }
    }

    private static class TestPath
    {
        public static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        public static string Combine(params string[] segments) => Path.Combine([Root, .. segments]);
    }

    private sealed class DescriptorAdditionalText(string path) : AdditionalText
    {
        public override string Path { get; } = path ?? throw new ArgumentNullException(nameof(path));

        public override SourceText? GetText(CancellationToken cancellationToken = default) => null;
    }
}
