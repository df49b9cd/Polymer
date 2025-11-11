using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Loader;
using Google.Protobuf.Reflection;
using Microsoft.CodeAnalysis;
using OmniRelay.Codegen.Protobuf.Core;

namespace OmniRelay.Codegen.Protobuf.Generator;

[Generator]
public sealed class ProtobufIncrementalGenerator : IIncrementalGenerator
{
    private static readonly Lazy<string?> DependencyDirectory = new(ResolveDependencyDirectory);
    private static int _resolverInitialized;

    private static readonly DiagnosticDescriptor DescriptorReadError = new(
        id: "POLYPROT001",
        title: "Failed to read descriptor set",
        messageFormat: "Unable to read protobuf descriptor '{0}': {1}",
        category: "OmniRelay.Codegen",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor DescriptorParseError = new(
        id: "POLYPROT002",
        title: "Failed to parse descriptor set",
        messageFormat: "Unable to parse protobuf descriptor '{0}': {1}",
        category: "OmniRelay.Codegen",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        EnsureDependencyResolver();

        var descriptorSets = context.AdditionalTextsProvider
            .Where(static file => file.Path.EndsWith(".pb", StringComparison.OrdinalIgnoreCase))
            .Select((file, _) => ReadDescriptorSet(file.Path))
            .Where(static result => result is not null);

        context.RegisterSourceOutput(descriptorSets, (spc, result) =>
        {
            if (result is null)
            {
                return;
            }

            if (result.ReadException is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(DescriptorReadError, Location.None, result.Path, result.ReadException.Message));
                return;
            }

            if (result.ParseException is not null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(DescriptorParseError, Location.None, result.Path, result.ParseException.Message));
                return;
            }

            if (result.DescriptorSet is null)
            {
                return;
            }

            var generator = new OmniRelayProtobufGenerator();
            foreach (var file in OmniRelayProtobufGenerator.GenerateFiles(result.DescriptorSet))
            {
                var hintName = CreateHintName(result.Path, file.Name);
                spc.AddSource(hintName, file.Content);
            }
        });
    }

    private static void EnsureDependencyResolver()
    {
        if (System.Threading.Interlocked.CompareExchange(ref _resolverInitialized, 1, 0) != 0)
        {
            return;
        }

        var directory = DependencyDirectory.Value;
        if (string.IsNullOrEmpty(directory))
        {
            return;
        }

#pragma warning disable IL2026, IL3002
        AssemblyLoadContext.Default.Resolving += ResolveAssemblyFromDependencies;
        PreloadDependency(directory, "Google.Protobuf");
        PreloadDependency(directory, "OmniRelay.Codegen.Protobuf.Core");
        PreloadDependency(directory, "OmniRelay");
#pragma warning restore IL2026, IL3002
    }

    private static DescriptorResult ReadDescriptorSet(string path)
    {
#pragma warning disable RS1035 // Do not do file IO in analyzers
        try
        {
            var bytes = File.ReadAllBytes(path);
            try
            {
                var descriptorSet = FileDescriptorSet.Parser.ParseFrom(bytes);
                return new DescriptorResult(path, descriptorSet, null, null);
            }
            catch (Exception parseEx)
            {
                return new DescriptorResult(path, null, null, parseEx);
            }
        }
        catch (Exception readEx)
        {
            return new DescriptorResult(path, null, readEx, null);
        }
#pragma warning restore RS1035
    }

    private static string CreateHintName(string descriptorPath, string generatedFileName)
    {
        var descriptorName = Path.GetFileNameWithoutExtension(descriptorPath);
        var sanitized = generatedFileName
            .Replace('/', '_')
            .Replace('\\', '_')
            .Replace('.', '_');
        return $"{descriptorName}_{sanitized}.g.cs";
    }

    private sealed record DescriptorResult(
        string Path,
        FileDescriptorSet? DescriptorSet,
        Exception? ReadException,
        Exception? ParseException);

#pragma warning disable RS1035 // Do not do file IO in analyzers
#pragma warning disable IL2026, IL3002
    [RequiresUnreferencedCode("Calls System.Runtime.Loader.AssemblyLoadContext.LoadFromAssemblyPath(String)")]
    [RequiresAssemblyFiles("Calls System.Reflection.Assembly.Location")]
    private static Assembly? ResolveAssemblyFromDependencies(AssemblyLoadContext context, AssemblyName name)
    {
        // Prevent recursion
        if (string.Equals(name.Name, typeof(ProtobufIncrementalGenerator).Assembly.GetName().Name, StringComparison.Ordinal))
        {
            return null;
        }

        var baseDirectory = Path.GetDirectoryName(typeof(ProtobufIncrementalGenerator).Assembly.Location);
        if (!string.IsNullOrEmpty(baseDirectory))
        {
            var localCandidate = Path.Combine(baseDirectory, $"{name.Name}.dll");
            if (File.Exists(localCandidate))
            {
                return context.LoadFromAssemblyPath(localCandidate);
            }
        }

        var dependencyDirectory = DependencyDirectory.Value;
        if (string.IsNullOrEmpty(dependencyDirectory))
        {
            return null;
        }

        var candidate = Path.Combine(dependencyDirectory, $"{name.Name}.dll");
        if (File.Exists(candidate))
        {
            return context.LoadFromAssemblyPath(candidate);
        }

        return null;
    }
#pragma warning restore IL2026, IL3002

    private static string? ResolveDependencyDirectory()
    {
        var assembly = typeof(ProtobufIncrementalGenerator).Assembly;
        var metadata = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute =>
                string.Equals(attribute.Key, "AnalyzerDependencyPath", StringComparison.Ordinal));

        if (metadata is null || string.IsNullOrWhiteSpace(metadata.Value))
        {
            return null;
        }

        try
        {
            var path = Path.GetFullPath(metadata.Value);
            return Directory.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static void PreloadDependency(string directory, string assemblyName)
    {
        try
        {
            var candidate = Path.Combine(directory, $"{assemblyName}.dll");
            if (File.Exists(candidate))
            {
                LoadDependency(candidate);
            }
        }
        catch
        {
            // best effort only; resolver will attempt to load on demand.
        }
    }
#pragma warning restore RS1035

#pragma warning disable IL2026, IL3002
    private static void LoadDependency(string path)
    {
        _ = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }
#pragma warning restore IL2026, IL3002
}
