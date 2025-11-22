using System.Security.Cryptography;

namespace OmniRelay.Core.Extensions;

public sealed record ExtensionPackage(
    ExtensionType Type,
    string Name,
    Version Version,
    byte[] Payload,
    byte[] Signature,
    IReadOnlyDictionary<string, string>? Metadata = null)
{
    public bool IsSigned(HashAlgorithmName algo, byte[] publicKey)
    {
        using var rsa = RSA.Create();
        rsa.ImportSubjectPublicKeyInfo(publicKey, out _);
        return rsa.VerifyData(Payload, Signature, algo, RSASignaturePadding.Pkcs1);
    }
}
