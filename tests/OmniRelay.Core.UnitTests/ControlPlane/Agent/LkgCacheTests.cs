using System.Text.Json;
using OmniRelay.ControlPlane.Agent;
using OmniRelay.Identity;
using Xunit;

namespace OmniRelay.Core.UnitTests.ControlPlane.Agent;

public sealed class LkgCacheTests
{
    [Fact]
    public async Task SaveAndLoad_WithSignature_ValidatesIntegrity()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"lkg-{Guid.NewGuid():N}.json");
        var cache = new LkgCache(tempPath, new LkgCacheOptions
        {
            SigningKey = "signing-key"u8.ToArray(),
            RequireSignature = true
        });

        try
        {
            var save = await cache.SaveAsync("v1", 1, "payload"u8.ToArray(), "resume"u8.ToArray(), TestContext.Current.CancellationToken);
            save.IsSuccess.ShouldBeTrue();

            var load = await cache.TryLoadAsync(TestContext.Current.CancellationToken);
            load.IsSuccess.ShouldBeTrue();
            load.Value.ShouldNotBeNull();
            load.Value!.Version.ShouldBe("v1");
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    [Fact]
    public async Task TryLoadAsync_WhenCorrupted_ReturnsFailure()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"lkg-{Guid.NewGuid():N}.json");
        var cache = new LkgCache(tempPath, new LkgCacheOptions
        {
            SigningKey = "signing-key"u8.ToArray(),
            RequireSignature = true
        });

        try
        {
            var save = await cache.SaveAsync("v1", 1, "payload"u8.ToArray(), "resume"u8.ToArray(), TestContext.Current.CancellationToken);
            save.IsSuccess.ShouldBeTrue();

            LkgCache.LkgEnvelope? envelope;
            await using (var stream = File.OpenRead(tempPath))
            {
                envelope = await JsonSerializer.DeserializeAsync(stream, LkgCacheJsonContext.Default.LkgEnvelope, TestContext.Current.CancellationToken);
            }

            envelope.ShouldNotBeNull();
            var tamperedHash = envelope!.Hash.ToArray();
            tamperedHash[0] ^= 0xFF;
            envelope = envelope with { Hash = tamperedHash };

            await using (var write = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(write, envelope, LkgCacheJsonContext.Default.LkgEnvelope, TestContext.Current.CancellationToken);
            }

            var load = await cache.TryLoadAsync(TestContext.Current.CancellationToken);
            load.IsFailure.ShouldBeTrue();
            load.Error!.Code.ShouldBe(LkgCacheErrorCodes.HashMismatch);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
