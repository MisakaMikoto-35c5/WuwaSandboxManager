using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// Starts and tracks the stock Remote Desktop client processes for a session.
/// <para>
/// <c>mstsc.exe</c> does not stay the long-lived client when it is handed an
/// <c>.rdp</c> file: it spawns the actual client (the window that shows the
/// application) as a separate <c>mstsc</c> process and exits. The session is
/// therefore tracked by <b>process id snapshots</b>: everything that is an
/// <c>mstsc</c> process beyond the pre-launch baseline belongs to the session.
/// </para>
/// <para>
/// The members are virtual so the session host can be unit-tested without
/// spawning the real client.
/// </para>
/// </summary>
internal class MstscClientTracker
{
    private const string ClientImageName = "mstsc";

    /// <summary>Ids of the <c>mstsc</c> processes currently running.</summary>
    public virtual HashSet<int> Snapshot()
    {
        var ids = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName(ClientImageName))
        {
            using (process)
            {
                ids.Add(process.Id);
            }
        }

        return ids;
    }

    /// <summary>Starts <c>mstsc.exe</c> on <paramref name="rdpFilePath"/>.</summary>
    public virtual void Launch(string rdpFilePath)
    {
        var startInfo = new ProcessStartInfo(ClientExecutablePath())
        {
            // Never via the shell: the .rdp must reach mstsc directly, not the
            // shell's file association (which can surface its own prompts).
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(rdpFilePath);

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("mstsc.exe could not be started.");

        // Bind the stub to the kill-on-close job: the real client is (usually) a
        // child of it and inherits the job membership, so even a hard kill of
        // this host takes the client down instead of orphaning it.
        ProcessJob.Bind(process);
        process.Dispose();
    }

    /// <summary>The full path of the stock Remote Desktop client.</summary>
    internal static string ClientExecutablePath()
        => Path.Combine(Environment.SystemDirectory, "mstsc.exe");

    /// <summary>
    /// The live <c>mstsc</c> processes beyond <paramref name="baseline"/> — the
    /// session's clients (including the one that is still showing the
    /// RemoteApp/security UI before it connects).
    /// </summary>
    public virtual List<Process> FindNewClients(HashSet<int> baseline)
    {
        var clients = new List<Process>();
        foreach (var process in Process.GetProcessesByName(ClientImageName))
        {
            if (baseline.Contains(process.Id))
            {
                process.Dispose();
                continue;
            }

            clients.Add(process);
        }

        return clients;
    }

    /// <summary>
    /// Ends every client beyond <paramref name="baseline"/>: the window is asked
    /// to close first (a graceful client disconnect) and the process is killed
    /// when it does not exit within <paramref name="grace"/>.
    /// </summary>
    public virtual void CloseClients(HashSet<int> baseline, TimeSpan grace)
    {
        foreach (var client in FindNewClients(baseline))
        {
            using (client)
            {
                try
                {
                    if (client.HasExited)
                    {
                        continue;
                    }

                    var windowClosed = false;
                    try
                    {
                        windowClosed = client.CloseMainWindow();
                    }
                    catch
                    {
                        // The window may not be up yet; the kill below covers it.
                    }

                    if (windowClosed && client.WaitForExit(grace))
                    {
                        continue;
                    }

                    if (!client.HasExited)
                    {
                        client.Kill();
                        client.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
                {
                    // A client that already exited is not an error.
                }
            }
        }
    }
}
