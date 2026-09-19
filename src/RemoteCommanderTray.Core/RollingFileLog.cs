using System.Text;

namespace RemoteCommanderTray.Core;

/// <summary>Where a log line came from.</summary>
public enum LogSource
{
    /// <summary>The tray itself.</summary>
    Tray,

    /// <summary>The agent's stdout.</summary>
    Agent,

    /// <summary>The agent's stderr.</summary>
    AgentError,
}

/// <summary>
/// Append-only log with size-based rotation, so an agent that chatters forever cannot
/// fill the disk.
/// </summary>
/// <remarks>
/// Regex redaction is defense in depth, not a privacy boundary. Ordinary agent input
/// must first pass the allowlisted AgentLogPolicy; opt-in raw logs remain sensitive.
/// Diagnostics never copies this file or logs left by earlier versions.
/// </remarks>
public sealed class RollingFileLog : IDisposable
{
    private readonly string _path;
    private readonly long _maxBytes;
    private readonly int _retainedFiles;
    private readonly object _gate = new();
    private bool _disposed;

    public RollingFileLog(string path, long maxBytes, int retainedFiles)
    {
        _path = path;
        _maxBytes = Math.Max(16 * 1024, maxBytes);
        _retainedFiles = Math.Max(0, retainedFiles);
    }

    public string Path => _path;

    /// <summary>Writes one timestamped line. Never throws: logging must not break the tray.</summary>
    public void Write(LogSource source, string message)
    {
        if (_disposed)
        {
            return;
        }

        var tag = source switch
        {
            LogSource.Agent => "agent",
            LogSource.AgentError => "agent!",
            _ => "tray",
        };

        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{tag}] {SecretRedactor.Redact(message)}";

        lock (_gate)
        {
            try
            {
                var directory = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                RotateIfNeeded(line.Length);
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // A log that cannot be written is not worth crashing the tray over.
            }
        }
    }

    /// <summary>Returns the last <paramref name="count"/> lines, newest last. Used by diagnostics.</summary>
    public IReadOnlyList<string> Tail(int count)
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                var lines = File.ReadAllLines(_path);
                return lines.Length <= count ? lines : lines[^count..];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private void RotateIfNeeded(int incomingLength)
    {
        var info = new FileInfo(_path);
        if (!info.Exists || info.Length + incomingLength <= _maxBytes)
        {
            return;
        }

        if (_retainedFiles == 0)
        {
            File.Delete(_path);
            return;
        }

        var oldest = $"{_path}.{_retainedFiles}";
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var i = _retainedFiles - 1; i >= 1; i--)
        {
            var from = $"{_path}.{i}";
            if (File.Exists(from))
            {
                File.Move(from, $"{_path}.{i + 1}", overwrite: true);
            }
        }

        File.Move(_path, $"{_path}.1", overwrite: true);
    }

    public void Dispose() => _disposed = true;
}
