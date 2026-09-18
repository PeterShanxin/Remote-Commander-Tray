using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;
using RemoteCommanderTray.Core;

namespace RemoteCommanderTray.Windows;

/// <summary>Owns one atomically contained process generation and both output streams.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentProcess : IAgentProcess
{
    private readonly AgentLaunchSpec _spec;
    private readonly JobObject _job = new();
    private readonly object _gate = new();
    private ContainedProcess? _child;
    private Task _output = Task.CompletedTask, _error = Task.CompletedTask;
    private Task? _termination;
    private volatile bool _disposed;
    private int _exitRaised;

    public WindowsAgentProcess(AgentLaunchSpec spec) => _spec = spec;
    public int? ProcessId { get; private set; }
    public event EventHandler<AgentOutputLine>? OutputReceived;
    public event EventHandler<int?>? Exited;
    public bool HasExited
    {
        get
        {
            lock (_gate)
            {
                try { return _child is null || _disposed || _child.Process.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_child is not null || _termination is not null)
                throw new InvalidOperationException("A process generation cannot be started twice.");
            var child = ContainedProcess.Create(_spec, _job);
            _child = child;
            ProcessId = child.Process.Id;
            _output = Task.Run(() => ReadOutput(child.Output, false));
            _error = Task.Run(() => ReadOutput(child.Error, true));
            child.Resume();
            _ = ObserveExitAsync(child);
        }
    }

    private Task TerminateAsync(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_termination is null || _termination.IsFaulted)
                _termination = Task.Run(() => _job.TerminateAndWait(timeout));
            return _termination;
        }
    }

    public async Task StopAsync(TimeSpan gracePeriod, CancellationToken cancellationToken = default)
    {
        // Cancellation must not abandon a live tree. Only the bounded drain determines
        // whether it is safe to replace this generation.
        await TerminateAsync(gracePeriod).ConfigureAwait(false);
        await Task.WhenAll(_output, _error).WaitAsync(gracePeriod).ConfigureAwait(false);
    }

    private async Task ObserveExitAsync(ContainedProcess child)
    {
        int? code = null;
        try
        {
            await child.Process.WaitForExitAsync().ConfigureAwait(false);
            code = child.Process.ExitCode;
            await StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
        {
            // Still report the root's exit. The supervisor retries Stop and refuses a
            // replacement if the job cannot be confirmed empty.
        }
        if (!_disposed && Interlocked.Exchange(ref _exitRaised, 1) == 0)
            Exited?.Invoke(this, code); // Never call user handlers under _gate.
    }

    private void ReadOutput(StreamReader reader, bool isError)
    {
        // A tool result may be enormous or omit its newline. Bound every line, including
        // verbose mode, rather than letting ReadLine allocate an unbounded string.
        const int maxLine = 16 * 1024;
        var buffer = new char[2048];
        var line = new StringBuilder();
        var oversized = false;
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i < count; i++)
                {
                    var ch = buffer[i];
                    if (ch == '\n')
                    {
                        Emit(oversized ? "<oversized agent line omitted>" : line.ToString().TrimEnd('\r'), isError);
                        line.Clear(); oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length == maxLine) { line.Clear(); oversized = true; }
                        else line.Append(ch);
                    }
                }
            }
            if (oversized || line.Length > 0)
                Emit(oversized ? "<oversized agent line omitted>" : line.ToString(), isError);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The owned job was stopped or its pipe was closed during shutdown.
        }
    }

    private void Emit(string text, bool error)
    {
        if (!_disposed) OutputReceived?.Invoke(this, new AgentOutputLine(text, error));
    }

    public void Dispose()
    {
        if (_disposed) return;
        try { StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(); }
        finally
        {
            _disposed = true;
            _job.Dispose();
            _child?.Dispose();
        }
    }
}
