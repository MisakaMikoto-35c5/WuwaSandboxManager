using System.Collections.Concurrent;
using System.Diagnostics;

namespace Wsm.Ui.WinUI;

/// <summary>
/// Owns the RDP session windows the GUI presents. Every session runs in its own
/// <c>wsm connect</c> child process — never in this process: the in-process
/// mstscax runtime keeps only the first session of a process alive and tears
/// every later one down 10-40s after connect, so hosting several sessions in the
/// GUI process breaks all but the first. One child process per session sidesteps
/// that process-global limit (and matches the platform's own one-session-per-
/// process client).
/// <para>
/// Tracked per sandbox id so Stop/Disconnect/app-exit can end the session even
/// though it lives outside this process. The child disconnects the mstscax
/// client gracefully when its window closes (<c>RdpHostForm.OnFormClosed</c>),
/// so ending a session asks the window to close first and kills only as a
/// fallback.
/// </para>
/// </summary>
internal static class SessionProcessManager
{
    private static readonly ConcurrentDictionary<Guid, Process> Processes = new();

    /// <summary>
    /// Raised (on a thread-pool thread) when a session process exits on its own —
    /// the user closed the window, or the connect failed. Carries the sandbox id,
    /// the exit code and the child's captured output (stderr first). Sessions
    /// ended through <see cref="EndAsync"/>/<see cref="EndAllAsync"/> do not raise
    /// it: a managed shutdown is not a status change.
    /// </summary>
    public static event Action<Guid, int, string>? SessionEnded;

    public static bool IsConnected(Guid id)
    {
        try
        {
            return Processes.TryGetValue(id, out var process) && !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // The monitor disposed the process between the lookup and the check.
            return false;
        }
    }

    /// <summary>
    /// Starts a session for <paramref name="id"/> unless one is already running.
    /// Returns true when a new process was spawned; false when the sandbox
    /// already has a live session window.
    /// </summary>
    public static bool Start(string cliExePath, Guid id, bool remoteApp, string? program)
    {
        if (IsConnected(id))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(cliExePath)
        {
            // The CLI is a console app; the RDP window it shows is unaffected.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("connect");
        startInfo.ArgumentList.Add(id.ToString());
        if (remoteApp)
        {
            startInfo.ArgumentList.Add("--remoteapp");
        }
        if (!string.IsNullOrWhiteSpace(program))
        {
            startInfo.ArgumentList.Add("--program");
            startInfo.ArgumentList.Add(program);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{cliExePath}'.");

        if (!Processes.TryAdd(id, process))
        {
            // Only reachable when a stale entry's monitor has not cleaned up yet:
            // replace it when that process is already dead, otherwise this start
            // lost a race and the duplicate must go.
            if (Processes.TryGetValue(id, out var existing)
                && existing.HasExited
                && Processes.TryRemove(KeyValuePair.Create(id, existing))
                && Processes.TryAdd(id, process))
            {
                existing.Dispose();
            }
            else
            {
                TryKill(process);
                process.Dispose();
                return false;
            }
        }

        _ = MonitorAsync(id, process);
        return true;
    }

    /// <summary>
    /// Ends the session for <paramref name="id"/> if one is running: asks the
    /// child's window to close (a graceful client disconnect) and kills the
    /// process when it does not exit in time.
    /// </summary>
    public static async Task EndAsync(Guid id)
    {
        if (!Processes.TryRemove(id, out var process))
        {
            return;
        }

        await CloseAndWaitAsync(process).ConfigureAwait(false);
        process.Dispose();
    }

    public static async Task EndAllAsync()
    {
        var all = Processes.ToArray();
        foreach (var pair in all)
        {
            Processes.TryRemove(pair.Key, out _);
        }

        await Task.WhenAll(all.Select(pair => CloseAndWaitAsync(pair.Value))).ConfigureAwait(false);
        foreach (var pair in all)
        {
            pair.Value.Dispose();
        }
    }

    private static async Task MonitorAsync(Guid id, Process process)
    {
        var output = string.Empty;
        var exitCode = -1;
        try
        {
            // Start reading both streams before awaiting the exit: a child that
            // fills an unread pipe buffer would otherwise never exit.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            output = string.Join(
                Environment.NewLine,
                new[] { (await stderr).Trim(), (await stdout).Trim() }
                    .Where(text => !string.IsNullOrEmpty(text)));

            try
            {
                exitCode = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                // EndAsync disposed the process while the monitor was awaiting
                // the exit: a managed shutdown, so the exit code is moot (the
                // SessionEnded event is suppressed below anyway).
            }
        }
        catch (Exception ex)
        {
            GuiErrorLog.Write(ex, "SessionProcessManager.Monitor");
        }

        // A managed End removed the registration first: that path owns the
        // disposal and suppresses the status event.
        if (Processes.TryRemove(KeyValuePair.Create(id, process)))
        {
            process.Dispose();
            SessionEnded?.Invoke(id, exitCode, output);
        }
    }

    private static async Task CloseAndWaitAsync(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            var closed = false;
            try
            {
                closed = process.CloseMainWindow();
            }
            catch
            {
                // The window is not up yet (endpoint acquisition can take a
                // while); the kill below covers that case.
            }

            if (closed)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException)
                {
                    // Fall through to the kill.
                }
            }

            if (!process.HasExited)
            {
                process.Kill();
            }

            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            GuiErrorLog.Write(ex, "SessionProcessManager.End");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch
        {
            // Best effort: a duplicate that already exited is harmless.
        }
    }
}
