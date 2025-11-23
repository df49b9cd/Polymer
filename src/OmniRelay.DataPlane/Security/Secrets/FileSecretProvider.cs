using System.Collections.Concurrent;
using Hugo;
using Microsoft.Extensions.Primitives;

namespace OmniRelay.Security.Secrets;

/// <summary>Loads secrets from encrypted files on disk.</summary>
public sealed class FileSecretProvider : ISecretProvider, IDisposable
{
    private readonly FileSecretProviderOptions _options;
    private readonly ISecretAccessAuditor _auditor;
    private readonly ConcurrentDictionary<string, FileWatchRegistration> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public static Result<FileSecretProvider> TryCreate(FileSecretProviderOptions options, ISecretAccessAuditor auditor)
    {
        if (options is null)
        {
            return Result.Fail<FileSecretProvider>(
                Error.From("FileSecretProviderOptions are required.", "secrets.file.options_missing"));
        }

        if (auditor is null)
        {
            return Result.Fail<FileSecretProvider>(
                Error.From("Auditor is required for FileSecretProvider.", "secrets.file.auditor_missing"));
        }

        if (string.IsNullOrWhiteSpace(options.BaseDirectory))
        {
            return Result.Fail<FileSecretProvider>(
                Error.From("BaseDirectory is required for FileSecretProvider.", "secrets.file.base_directory_missing"));
        }

        return Result.Ok(new FileSecretProvider(options, auditor));
    }

    public FileSecretProvider(FileSecretProviderOptions options, ISecretAccessAuditor auditor)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _auditor = auditor ?? throw new ArgumentNullException(nameof(auditor));
    }

    public ValueTask<SecretValue?> GetSecretAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_options.Secrets.TryGetValue(name, out var path))
        {
            _auditor.RecordAccess("file", name, SecretAccessOutcome.NotFound);
            return ValueTask.FromResult<SecretValue?>(null);
        }

        var resolved = ResolvePath(path);
        if (!File.Exists(resolved))
        {
            _auditor.RecordAccess("file", name, SecretAccessOutcome.NotFound);
            return ValueTask.FromResult<SecretValue?>(null);
        }

        var buffer = File.ReadAllBytes(resolved);
        var metadata = new SecretMetadata(
            name,
            "file",
            DateTimeOffset.UtcNow,
            FromCache: false,
            Version: File.GetLastWriteTimeUtc(resolved).ToString("O"));

        var secret = new SecretValue(metadata, buffer, Watch(name));
        _auditor.RecordAccess("file", name, SecretAccessOutcome.Success);
        return ValueTask.FromResult<SecretValue?>(secret);
    }

    public IChangeToken? Watch(string name)
    {
        if (!_options.Secrets.TryGetValue(name, out var path))
        {
            return null;
        }

        var resolved = ResolvePath(path);
        if (string.IsNullOrEmpty(Path.GetFileName(resolved)))
        {
            return null;
        }

        var registration = _watchers.GetOrAdd(resolved, path => new FileWatchRegistration(path, _options.WatchPollingInterval));
        return registration.Subscribe();
    }

    private string ResolvePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Secret path cannot be null or whitespace.", nameof(path));
        }

        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.Combine(_options.BaseDirectory, path);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var watcher in _watchers.Values)
        {
            watcher.Dispose();
        }

        _watchers.Clear();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private sealed class FileWatchRegistration : IDisposable
    {
        private readonly string _fullPath;
        private readonly string? _directory;
        private readonly string? _fileName;
        private readonly TimeSpan _pollInterval;
        private readonly object _gate = new();
        private readonly List<CancellationTokenSource> _tokens = new();
        private readonly Timer _poller;
        private FileSystemWatcher? _watcher;
        private DateTime _lastWrite;
        private bool _disposed;

        public FileWatchRegistration(string fullPath, TimeSpan pollInterval)
        {
            _fullPath = fullPath;
            _directory = Path.GetDirectoryName(fullPath);
            _fileName = Path.GetFileName(fullPath);
            _pollInterval = pollInterval <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(100) : pollInterval;
            _lastWrite = GetLastWriteTime();
            TryStartWatcher();
            _poller = new Timer(Poll, null, _pollInterval, _pollInterval);
        }

        public CancellationChangeToken Subscribe()
        {
            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                if (_disposed)
                {
                    cts.Cancel();
                }
                else
                {
                    _tokens.Add(cts);
                }
            }

            return new CancellationChangeToken(cts.Token);
        }

        private void Poll(object? state)
        {
            if (_disposed)
            {
                return;
            }

            TryStartWatcher();
            var current = GetLastWriteTime();
            if (current != _lastWrite)
            {
                _lastWrite = current;
                SignalChange();
            }
        }

        private void TryStartWatcher()
        {
            if (_watcher is not null || string.IsNullOrEmpty(_directory) || string.IsNullOrEmpty(_fileName))
            {
                return;
            }

            if (!Directory.Exists(_directory))
            {
                return;
            }

            var watcher = new FileSystemWatcher(_directory, _fileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            FileSystemEventHandler onChange = (_, _) => SignalChange();
            RenamedEventHandler onRename = (_, _) => SignalChange();

            watcher.Changed += onChange;
            watcher.Created += onChange;
            watcher.Deleted += onChange;
            watcher.Renamed += onRename;
            watcher.Error += (_, __) => RestartWatcher();
            _watcher = watcher;
        }

        private void RestartWatcher()
        {
            lock (_gate)
            {
                _watcher?.Dispose();
                _watcher = null;
            }
        }

        private DateTime GetLastWriteTime()
        {
            try
            {
                return File.Exists(_fullPath) ? File.GetLastWriteTimeUtc(_fullPath) : DateTime.MinValue;
            }
            catch
            {
                return DateTime.MinValue;
            }
        }

        private void SignalChange()
        {
            List<CancellationTokenSource>? tokens = null;
            lock (_gate)
            {
                if (_disposed || _tokens.Count == 0)
                {
                    return;
                }

                tokens = new List<CancellationTokenSource>(_tokens);
                _tokens.Clear();
            }

            foreach (var token in tokens)
            {
                try
                {
                    token.Cancel();
                }
                finally
                {
                    token.Dispose();
                }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            _watcher?.Dispose();
            _poller.Dispose();
            SignalChange();
        }
    }
}
