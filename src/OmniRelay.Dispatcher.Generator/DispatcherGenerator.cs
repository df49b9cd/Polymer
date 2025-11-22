using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace OmniRelay.Dispatcher.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class DispatcherGenerator : IIncrementalGenerator
{
    private const string AttributeName = "OmniRelay.Dispatcher.CodeGen.DispatcherDefinitionAttribute";
    private const string ConfigMetadataKey = "build_metadata.additionalfiles.OmniRelayDispatcherConfig";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
#pragma warning disable IDE0200
        var dispatcherClasses = context.SyntaxProvider
            .CreateSyntaxProvider<ClassDeclarationSyntax?>(IsCandidate, GetSemanticTarget)
            .Where(static model => model is not null)!;
#pragma warning restore IDE0200

        var dispatcherConfigs = context.AdditionalTextsProvider
            .Combine(context.AnalyzerConfigOptionsProvider)
            .Select(static (pair, _) =>
            {
                var (text, opts) = pair;
                var meta = opts.GetOptions(text);
                meta.TryGetValue(ConfigMetadataKey, out var flag);
                return string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase) ? text : null;
            })
            .Where(static t => t is not null)!;

        var pipeline = context.CompilationProvider
            .Combine(dispatcherClasses.Collect())
            .Combine(dispatcherConfigs.Collect());

        context.RegisterSourceOutput(pipeline, static (spc, triple) =>
        {
            var ((compilation, classes), configs) = triple;
            if (compilation.GetTypeByMetadataName(AttributeName) is null)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        id: "ORGEN001",
                        title: "DispatcherDefinitionAttribute missing",
                        messageFormat: "DispatcherDefinitionAttribute was not found in compilation; dispatcher generation skipped.",
                        category: "Usage",
                        DiagnosticSeverity.Warning,
                        isEnabledByDefault: true),
                    Location.None));
                return;
            }

            var configArray = configs.IsDefault
                ? ImmutableArray<AdditionalText>.Empty
                : configs.Where(static c => c is not null)!.Select(static c => c!).ToImmutableArray();

            if (configArray.Length == 0)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        id: "ORGEN002",
                        title: "Dispatcher config not provided",
                        messageFormat: "No AdditionalFiles were marked with OmniRelayDispatcherConfig; generated dispatcher will default to the path provided at call site.",
                        category: "Usage",
                        DiagnosticSeverity.Info,
                        isEnabledByDefault: true),
                    Location.None));
            }

            foreach (var cls in classes)
            {
                if (cls is null)
                {
                    continue;
                }

                GenerateDispatcher(spc, cls, configArray);
            }
        });
    }

    private static bool IsCandidate(SyntaxNode node, CancellationToken _) =>
        node is ClassDeclarationSyntax { AttributeLists.Count: > 0, Modifiers: var mods }
        && mods.Any(static m => m.IsKind(SyntaxKind.PartialKeyword));

    private static ClassDeclarationSyntax? GetSemanticTarget(GeneratorSyntaxContext context, CancellationToken _)
    {
        if (context.Node is not ClassDeclarationSyntax classDecl)
        {
            return null;
        }

        foreach (var list in classDecl.AttributeLists)
        {
            foreach (var attribute in list.Attributes)
            {
                var symbol = context.SemanticModel.GetSymbolInfo(attribute, _).Symbol as IMethodSymbol;
                if (symbol?.ContainingType.ToDisplayString() == AttributeName)
                {
                    return classDecl;
                }
            }
        }

        return null;
    }

    private static void GenerateDispatcher(SourceProductionContext context, ClassDeclarationSyntax cls, ImmutableArray<AdditionalText> configs)
    {
        var ns = GetNamespace(cls);
        var className = cls.Identifier.Text;

        // Derive service name from attribute argument; default to class name if missing.
        var serviceName = "";
        var attr = cls.AttributeLists
            .SelectMany(static l => l.Attributes)
            .First(a => IsDispatcherAttribute(a));

        foreach (var arg in attr.ArgumentList?.Arguments ?? default)
        {
            if (arg.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                serviceName = literal.Token.ValueText;
                break;
            }
        }

        if (string.IsNullOrWhiteSpace(serviceName))
        {
            serviceName = className;
        }

        var configPath = configs.FirstOrDefault()?.Path ?? "appsettings.dispatcher.json";

        var source = $$"""
// <auto-generated/>
using System;
using OmniRelay.Dispatcher;

namespace {{ns}}
{
    partial class {{className}}
    {
        public static Dispatcher CreateDispatcher(IServiceProvider services, string? configPath = null)
        {
            var options = new DispatcherOptions("{{serviceName}}");
            Configure(services, options, configPath ?? "{{configPath}}");
            return new Dispatcher(options);
        }

        static partial void Configure(IServiceProvider services, DispatcherOptions options, string configPath);
    }
}
""";

        context.AddSource($"{className}.Dispatcher.g.cs", source);
    }

    private static string GetNamespace(ClassDeclarationSyntax cls)
    {
        var current = cls.Parent;
        while (current is not null)
        {
            if (current is NamespaceDeclarationSyntax ns)
            {
                return ns.Name.ToString();
            }

            if (current is FileScopedNamespaceDeclarationSyntax fileNs)
            {
                return fileNs.Name.ToString();
            }

            current = current.Parent;
        }

        return "Global";
    }

    private static bool IsDispatcherAttribute(AttributeSyntax attributeSyntax)
    {
        var name = attributeSyntax.Name.ToString();
        return name.EndsWith("DispatcherDefinition", StringComparison.Ordinal) ||
               name.EndsWith("DispatcherDefinitionAttribute", StringComparison.Ordinal) ||
               name.Equals("DispatcherDefinition", StringComparison.Ordinal) ||
               name.Equals("DispatcherDefinitionAttribute", StringComparison.Ordinal);
    }
}
