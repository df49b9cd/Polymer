using System.CommandLine;

namespace OmniRelay.Cli.Modules;

/// <summary>
/// Represents a pluggable CLI feature area that can contribute commands to the root CLI.
/// Avoids reflection-based discovery to stay NativeAOT-friendly.
/// </summary>
internal interface ICliModule
{
    Command Build();
}
