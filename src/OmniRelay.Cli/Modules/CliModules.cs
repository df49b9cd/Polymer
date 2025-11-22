using System.CommandLine;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Central registry for built-in CLI modules. New feature packs can be added here
/// without reflection to keep the executable NativeAOT-friendly.
/// </summary>
internal static class CliModules
{
    public static IEnumerable<ICliModule> GetDefaultModules() =>
        new ICliModule[]
        {
            new ConfigCommandsModule(),
            new RequestModule(),
            new BenchmarkModule(),
            new ServeModule(),
            new IntrospectModule(),
            new ScriptModule(),
            new MeshModule()
        };
}

internal sealed class ServeModule : ICliModule { public Command Build() => ProgramServeModule.CreateServeCommand(); }
internal sealed class IntrospectModule : ICliModule { public Command Build() => ProgramIntrospectModule.CreateIntrospectCommand(); }
internal sealed class ScriptModule : ICliModule { public Command Build() => ProgramScriptModule.CreateScriptCommand(); }
internal sealed class MeshModule : ICliModule { public Command Build() => ProgramMeshModule.CreateMeshCommand(); }
internal sealed class RequestModule : ICliModule { public Command Build() => Program.CreateRequestCommand(); }
internal sealed class BenchmarkModule : ICliModule { public Command Build() => Program.CreateBenchmarkCommand(); }
