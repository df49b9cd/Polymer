using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OmniRelay.Core.Extensions;
using OmniRelay.TestSupport.Assertions;
using System.Collections.Immutable;
using Xunit;

namespace OmniRelay.Core.UnitTests.Extensions;

public class DslExtensionHostTests
{
    [Fact]
    public void Loads_and_executes_valid_signed_package()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("SET hello\nAPPEND  world\nRET");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var package = new ExtensionPackage(ExtensionType.Dsl, "greeting", new Version(1, 0), payload, signature);

        var options = DslExtensionHostOptions.CreateDefault(HashAlgorithmName.SHA256, rsa.ExportSubjectPublicKeyInfo());
        var registry = new ExtensionRegistry();
        var host = new DslExtensionHost(options, registry, NullLogger<DslExtensionHost>.Instance);

        host.TryLoad(package, out var program).ShouldBeTrue();

        host.TryExecute(ref program, "ignored"u8, out var output).ShouldBeTrue();
        Encoding.UTF8.GetString(output).ShouldBe("hello world");
    }

    [Fact]
    public void Rejects_invalid_signature()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("RET");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        // mutate payload so signature fails
        var tampered = Encoding.UTF8.GetBytes("RET ");
        var package = new ExtensionPackage(ExtensionType.Dsl, "tampered", new Version(1, 0), tampered, signature);

        var options = DslExtensionHostOptions.CreateDefault(HashAlgorithmName.SHA256, rsa.ExportSubjectPublicKeyInfo());
        var registry = new ExtensionRegistry();
        var host = new DslExtensionHost(options, registry, NullLogger<DslExtensionHost>.Instance);

        host.TryLoad(package, out _).ShouldBeFalse();
    }

    [Fact]
    public void Rejects_disallowed_opcode()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("UNSAFE some-arg\nRET");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var package = new ExtensionPackage(ExtensionType.Dsl, "bad-op", new Version(1, 0), payload, signature);

        var options = DslExtensionHostOptions.CreateDefault(HashAlgorithmName.SHA256, rsa.ExportSubjectPublicKeyInfo());
        var registry = new ExtensionRegistry();
        var host = new DslExtensionHost(options, registry, NullLogger<DslExtensionHost>.Instance);

        host.TryLoad(package, out _).ShouldBeFalse();
    }

    [Fact]
    public void Enforces_output_budget()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("APPEND abc\nRET");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var package = new ExtensionPackage(ExtensionType.Dsl, "budget", new Version(1, 0), payload, signature);

        var options = new DslExtensionHostOptions(
            HashAlgorithmName.SHA256,
            rsa.ExportSubjectPublicKeyInfo(),
            new ExtensionFailurePolicy(FailOpen: false, ReloadOnFailure: true),
            ImmutableHashSet.Create(DslOpcode.Set, DslOpcode.Append, DslOpcode.Ret),
            MaxInstructions: 8,
            MaxOutputBytes: 2,
            MaxExecutionTime: TimeSpan.FromSeconds(1));

        var registry = new ExtensionRegistry();
        var host = new DslExtensionHost(options, registry, NullLogger<DslExtensionHost>.Instance);

        host.TryLoad(package, out var program).ShouldBeTrue();

        host.TryExecute(ref program, ReadOnlySpan<byte>.Empty, out _).ShouldBeFalse();
    }

    [Fact]
    public void FailOpen_returns_input_and_records_watchdog()
    {
        using var rsa = RSA.Create(2048);
        var payload = Encoding.UTF8.GetBytes("APPEND abcdefghijklmnopqrstuvwxyz\nRET");
        var signature = rsa.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var package = new ExtensionPackage(ExtensionType.Dsl, "failopen", new Version(1, 0), payload, signature);

        var options = new DslExtensionHostOptions(
            HashAlgorithmName.SHA256,
            rsa.ExportSubjectPublicKeyInfo(),
            new ExtensionFailurePolicy(FailOpen: true, ReloadOnFailure: false),
            ImmutableHashSet.Create(DslOpcode.Append, DslOpcode.Ret),
            MaxInstructions: 4,
            MaxOutputBytes: 4, // force output watchdog
            MaxExecutionTime: TimeSpan.FromSeconds(1));

        var registry = new ExtensionRegistry();
        var host = new DslExtensionHost(options, registry, NullLogger<DslExtensionHost>.Instance);

        host.TryLoad(package, out var program).ShouldBeTrue();

        host.TryExecute(ref program, "hi"u8, out var output).ShouldBeTrue();
        Encoding.UTF8.GetString(output).ShouldBe("hi");

        var snapshot = registry.CreateSnapshot();
        snapshot.Extensions.ShouldHaveSingleItem().Status.ShouldBe("watchdog");
    }
}
