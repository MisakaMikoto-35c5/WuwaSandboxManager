using System.Runtime.InteropServices;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Process.Internal;

/// <summary>
/// P/Invoke surface over the Host Compute Service process API
/// (<c>computecore.dll</c>). This mirrors the small subset of the operation
/// based API that the container engine needs to launch a command inside a
/// running compute system: <c>HcsCreateProcess</c> followed by
/// <c>HcsWaitForProcessExit</c>.
/// </summary>
internal static class HcsProcessNative
{
    private const string ComputeCore = "computecore.dll";

    /// <summary><c>HCS_E_OPERATION_PENDING</c>: the call is tracked by the operation handle.</summary>
    public const int HcsEOperationPending = unchecked((int)0xC0370103);

    /// <summary><c>HCS_E_OPERATION_TIMEOUT</c>: the operation did not finish within the timeout.</summary>
    public const int HcsEOperationTimeout = unchecked((int)0xC0370104);

    /// <summary><c>GENERIC_ALL</c>; HCS rejects an access mask of 0 with <c>E_INVALIDARG</c>.</summary>
    public const uint GenericAll = 0x10000000;

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsOpenComputeSystem(string id, uint requestedAccess, out IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseComputeSystem(IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern IntPtr HcsCreateOperation(IntPtr context, IntPtr callback);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForOperationResult(IntPtr operation, uint timeoutMs, out IntPtr resultDocument);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseOperation(IntPtr operation);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateProcess(
        IntPtr computeSystem,
        string processParameters,
        IntPtr operation,
        IntPtr securityDescriptor,
        out IntPtr process);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseProcess(IntPtr process);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsTerminateProcess(IntPtr process, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForProcessExit(IntPtr process, uint timeoutMs, out IntPtr result);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);
}

/// <summary>
/// Owns an <c>HCS_OPERATION</c> handle and completes it. The result document is
/// released with <c>LocalFree</c> as the HCS documentation requires.
/// </summary>
internal sealed class HcsProcessOperation : IDisposable
{
    private IntPtr _handle;

    public HcsProcessOperation()
    {
        _handle = HcsProcessNative.HcsCreateOperation(IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            throw new SandboxBackendException("HcsCreateOperation failed.", Marshal.GetLastWin32Error());
        }
    }

    public IntPtr Handle => _handle;

    /// <summary>Waits for the tracked operation and returns its result document.</summary>
    public string? Complete(int callResult, string operationName)
    {
        var waitResult = HcsProcessNative.HcsWaitForOperationResult(_handle, uint.MaxValue, out var document);
        var text = TakeString(document);

        if (callResult != HcsProcessNative.HcsEOperationPending && callResult < 0)
        {
            throw HcsProcessErrors.ToException(callResult, operationName, text);
        }

        if (waitResult < 0)
        {
            throw HcsProcessErrors.ToException(waitResult, operationName, text);
        }

        return text;
    }

    public static string? TakeString(IntPtr pointer)
    {
        if (pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(pointer);
        }
        finally
        {
            HcsProcessNative.LocalFree(pointer);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            HcsProcessNative.HcsCloseOperation(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Maps the HCS/HRESULT failures seen while launching a process.</summary>
internal static class HcsProcessErrors
{
    public const int HcsEAccessDenied = unchecked((int)0x8037011B);
    public const int HcsENotFound = unchecked((int)0x80370110);

    public const string ElevationMessage =
        "The Host Compute Service (HCS) requires an elevated process. " +
        "Run WindowsSandboxManager as Administrator (or as a member of the Hyper-V Administrators group).";

    public static SandboxDomainException ToException(int hresult, string operationName, string? detail)
    {
        var suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" {detail}";

        if (hresult == HcsEAccessDenied)
        {
            return new SandboxNotSupportedException($"{ElevationMessage}{suffix}");
        }

        if (hresult == HcsENotFound)
        {
            return new SandboxNotFoundException($"The compute system was not found ({operationName}).{suffix}");
        }

        return new SandboxBackendException(
            $"HCS '{operationName}' failed with HRESULT 0x{hresult:X8}.{suffix}", hresult);
    }
}
