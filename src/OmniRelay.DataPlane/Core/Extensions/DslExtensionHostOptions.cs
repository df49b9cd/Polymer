using System.Collections.Immutable;
using System.Security.Cryptography;

namespace OmniRelay.Core.Extensions;

/// <summary>
/// Configuration for the DSL extension host: signature enforcement, opcode allowlist, and resource limits.
/// </summary>
internal sealed record DslExtensionHostOptions(
    HashAlgorithmName SignatureAlgorithm,
    byte[] PublicKey,
    ExtensionFailurePolicy FailurePolicy,
    ImmutableHashSet<DslOpcode> AllowedOpcodes,
    int MaxInstructions,
    int MaxOutputBytes,
    TimeSpan MaxExecutionTime)
{
    public static DslExtensionHostOptions CreateDefault(HashAlgorithmName signatureAlgorithm, byte[] publicKey) =>
        new(signatureAlgorithm,
            publicKey,
            new ExtensionFailurePolicy(FailOpen: false, ReloadOnFailure: true, Cooldown: TimeSpan.FromSeconds(1)),
            ImmutableHashSet.Create(DslOpcode.Ret, DslOpcode.Set, DslOpcode.Append, DslOpcode.Upper, DslOpcode.Lower, DslOpcode.Truncate),
            MaxInstructions: 512,
            MaxOutputBytes: 32 * 1024,
            MaxExecutionTime: TimeSpan.FromMilliseconds(50));
}
