using System.Text;
using OmniRelay.Security.Secrets;
using Xunit;

namespace OmniRelay.Core.UnitTests.Security;

public sealed class SecretProviderTests
{
    private readonly TestAuditor _auditor = new();

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask EnvironmentSecretProvider_ReturnsValue()
    {
        Environment.SetEnvironmentVariable("TEST_SECRET_VALUE", "env-secret");
        var provider = new EnvironmentSecretProvider(_auditor);

        try
        {
            var secret = await provider.GetSecretAsync("TEST_SECRET_VALUE", TestContext.Current.CancellationToken);
            secret.ShouldNotBeNull();
            var secretValue = secret ?? throw new InvalidOperationException("Secret was null");
            using (secretValue)
            {
                secretValue.AsString().ShouldBe("env-secret");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TEST_SECRET_VALUE", null);
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask FileSecretProvider_LoadsSecret()
    {
        using var tempFile = new TempFile("file-secret");
        var options = new FileSecretProviderOptions();
        options.Secrets["tls"] = tempFile.Path;
        var provider = new FileSecretProvider(options, _auditor);

        var secret = await provider.GetSecretAsync("tls", TestContext.Current.CancellationToken);
        secret.ShouldNotBeNull();
        var secretValue = secret ?? throw new InvalidOperationException("Secret was null");
        using (secretValue)
        {
            Encoding.UTF8.GetString(secretValue.AsMemory().Span).ShouldBe("file-secret");
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public async ValueTask CompositeSecretProvider_RespectsOrder()
    {
        var inline = new InMemorySecretProvider(_auditor);
        inline.SetSecret("tls", Encoding.UTF8.GetBytes("inline-secret"));
        var env = new EnvironmentSecretProvider(_auditor, prefix: null);

        var composite = new CompositeSecretProvider(
            new ISecretProvider[] { inline, env },
            _auditor);

        var secret = await composite.GetSecretAsync("tls", TestContext.Current.CancellationToken);
        secret.ShouldNotBeNull();
        var secretValue = secret ?? throw new InvalidOperationException("Secret was null");
        using (secretValue)
        {
            secretValue.AsString().ShouldBe("inline-secret");
        }
    }

    [Fact(Timeout = TestTimeouts.Default)]
    public void FileSecretProvider_WatchDetectsChanges()
    {
        using var tempFile = new TempFile("first");
        var options = new FileSecretProviderOptions();
        options.Secrets["key"] = tempFile.Path;
        var provider = new FileSecretProvider(options, _auditor);

        var token = provider.Watch("key");
        token.ShouldNotBeNull();

        File.WriteAllText(tempFile.Path, "second");
        File.SetLastWriteTimeUtc(tempFile.Path, DateTime.UtcNow.AddSeconds(1));

        SpinWait.SpinUntil(() => token!.HasChanged, TimeSpan.FromSeconds(2)).ShouldBeTrue();
    }

    private sealed class TestAuditor : ISecretAccessAuditor
    {
        public List<(string Provider, string Secret, SecretAccessOutcome Outcome)> Records { get; } = new();

        public void RecordAccess(string providerName, string secretName, SecretAccessOutcome outcome, Exception? exception = null)
        {
            Records.Add((providerName, secretName, outcome));
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string contents)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"secret-{Guid.NewGuid():N}.txt");
            File.WriteAllText(Path, contents);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch
            {
                // ignored
            }
        }
    }
}
