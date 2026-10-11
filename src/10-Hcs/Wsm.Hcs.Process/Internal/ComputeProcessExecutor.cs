using System.Text.Json;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Process.Internal;

/// <summary>
/// Default <see cref="IComputeProcessExecutor"/>: launches a command inside a
/// running HCS compute system through <c>HcsCreateProcess</c> and waits for it
/// with <c>HcsWaitForProcessExit</c>. This is the primitive the container engine
/// uses to bring up the guest services of the shared Windows Sandbox image.
/// <para>
/// The process is created with no stdio pipes (the sandbox workload is
/// fire-and-forget); only the exit code is observed. HCS reports it in the
/// <c>ProcessStatus</c> document (<c>ExitCode</c>, a <c>uint32</c>). The wait is
/// bounded so a hung command can never pin a worker thread forever; on timeout
/// or cancellation the process is terminated.
/// </para>
/// </summary>
[ExposeServices(typeof(IComputeProcessExecutor))]
internal sealed class ComputeProcessExecutor : IComputeProcessExecutor, ISingletonDependency
{
    /// <summary>Slice used for the (interruptible) wait loop.</summary>
    private const uint WaitSliceMs = 5_000;

    /// <summary>Overall bound for a single command before it is terminated.</summary>
    private const long DefaultTimeoutMs = 10 * 60 * 1_000;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public Task<int> ExecuteAsync(
        string computeSystemId,
        string commandLine,
        string? workingDirectory = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(computeSystemId))
        {
            throw new SandboxValidationException("A compute system id is required to execute a process.");
        }

        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new SandboxValidationException("A command line is required to execute a process.");
        }

        // The native calls block, so run them on the thread pool rather than on
        // the caller's thread.
        return Task.Run(() => Execute(computeSystemId, commandLine, workingDirectory, ct), ct);
    }

    private static int Execute(string computeSystemId, string commandLine, string? workingDirectory, CancellationToken ct)
    {
        var system = Open(computeSystemId);
        var process = IntPtr.Zero;

        try
        {
            using var operation = new HcsProcessOperation();

            var parameters = JsonSerializer.Serialize(
                new Dictionary<string, object?>
                {
                    ["CommandLine"] = commandLine,
                    ["WorkingDirectory"] = string.IsNullOrWhiteSpace(workingDirectory) ? @"C:\" : workingDirectory,
                    // No stdio: the caller only needs the exit code.
                    ["CreateStdInPipe"] = false,
                    ["CreateStdOutPipe"] = false,
                    ["CreateStdErrPipe"] = false,
                    ["RestrictedToken"] = false,
                    ["EmulateConsole"] = false,
                    ["UseLegacyConsole"] = false,
                },
                Options);

            var hr = HcsProcessNative.HcsCreateProcess(
                system, parameters, operation.Handle, IntPtr.Zero, out process);
            operation.Complete(hr, nameof(HcsProcessNative.HcsCreateProcess));

            if (process == IntPtr.Zero)
            {
                throw new SandboxBackendException(
                    "HcsCreateProcess returned a null process handle.", 0);
            }

            return WaitForExit(computeSystemId, process, ct);
        }
        finally
        {
            if (process != IntPtr.Zero)
            {
                HcsProcessNative.HcsCloseProcess(process);
            }

            HcsProcessNative.HcsCloseComputeSystem(system);
        }
    }

    /// <summary>
    /// Waits for the process to exit in bounded slices so cancellation and an
    /// overall timeout are honoured rather than blocking indefinitely inside the
    /// native call.
    /// </summary>
    private static int WaitForExit(string computeSystemId, IntPtr process, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + DefaultTimeoutMs;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var waitHr = HcsProcessNative.HcsWaitForProcessExit(process, WaitSliceMs, out var result);
            var document = HcsProcessOperation.TakeString(result);

            if (waitHr >= 0)
            {
                var (exited, exitCode) = ReadStatus(document);
                if (exited)
                {
                    return exitCode;
                }

                // The slice elapsed but the process is still running; loop.
            }
            else if (waitHr != HcsProcessNative.HcsEOperationTimeout)
            {
                throw HcsProcessErrors.ToException(
                    waitHr, nameof(HcsProcessNative.HcsWaitForProcessExit), document);
            }

            if (Environment.TickCount64 >= deadline)
            {
                TryTerminate(process);
                throw new SandboxBackendException(
                    $"The process in compute system '{computeSystemId}' did not exit within " +
                    $"{DefaultTimeoutMs / 1_000} seconds and was terminated.", 0);
            }
        }
    }

    private static void TryTerminate(IntPtr process)
    {
        try
        {
            using var operation = new HcsProcessOperation();
            var hr = HcsProcessNative.HcsTerminateProcess(process, operation.Handle, "{}");
            operation.Complete(hr, nameof(HcsProcessNative.HcsTerminateProcess));
        }
        catch
        {
            // Best effort: the caller is already failing.
        }
    }

    private static IntPtr Open(string computeSystemId)
    {
        var hr = HcsProcessNative.HcsOpenComputeSystem(computeSystemId, HcsProcessNative.GenericAll, out var handle);
        if (hr >= 0 && handle != IntPtr.Zero)
        {
            return handle;
        }

        if (handle != IntPtr.Zero)
        {
            HcsProcessNative.HcsCloseComputeSystem(handle);
        }

        throw hr < 0
            ? HcsProcessErrors.ToException(hr, nameof(HcsProcessNative.HcsOpenComputeSystem), computeSystemId)
            : new SandboxBackendException(
                $"HcsOpenComputeSystem returned a null handle for '{computeSystemId}'.", 0);
    }

    /// <summary>
    /// Reads <c>ProcessStatus.ExitCode</c> from a completed wait document. The
    /// exit code is a <c>uint32</c>, so large status/crash codes are preserved via
    /// an unchecked cast rather than overflowing <see cref="int"/>.
    /// </summary>
    internal static int ParseExitCode(string? document)
    {
        var (exited, exitCode) = ReadStatus(document);
        if (!exited)
        {
            throw new SandboxBackendException(
                "HCS reported a process status that has not exited.", 0);
        }

        return exitCode;
    }

    /// <summary>
    /// Parses a <c>ProcessStatus</c> document. A missing or unparsable document
    /// surfaces as a backend error instead of a false success.
    /// </summary>
    private static (bool Exited, int ExitCode) ReadStatus(string? document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw new SandboxBackendException(
                "HCS did not report a process status for the process.", 0);
        }

        try
        {
            using var json = JsonDocument.Parse(document);
            var root = json.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                // "Exited" absent is treated as exited (older documents carried
                // only ProcessId/ExitCode).
                var exited = !root.TryGetProperty("Exited", out var exitedElement) ||
                             exitedElement.ValueKind != JsonValueKind.False;

                if (!root.TryGetProperty("ExitCode", out var codeElement))
                {
                    if (!exited)
                    {
                        return (false, 0);
                    }
                }
                else if (codeElement.ValueKind == JsonValueKind.Number)
                {
                    if (codeElement.TryGetUInt32(out var unsigned))
                    {
                        return (exited, unchecked((int)unsigned));
                    }

                    if (codeElement.TryGetInt32(out var signed))
                    {
                        return (exited, signed);
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the error below.
        }

        throw new SandboxBackendException(
            $"HCS returned an unexpected process status document: {document}", 0);
    }
}
