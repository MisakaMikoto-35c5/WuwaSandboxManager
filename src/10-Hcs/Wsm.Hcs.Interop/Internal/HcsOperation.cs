using System.Runtime.InteropServices;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Interop.Internal;

/// <summary>
/// Owns an <c>HCS_OPERATION</c> handle and completes it. The result document is
/// released with <c>LocalFree</c> as required by the HCS documentation.
/// </summary>
internal sealed class HcsOperation : IDisposable
{
    private IntPtr _handle;

    public HcsOperation()
    {
        _handle = HcsNative.HcsCreateOperation(IntPtr.Zero, IntPtr.Zero);
        if (_handle == IntPtr.Zero)
        {
            throw new SandboxBackendException("HcsCreateOperation failed.", Marshal.GetLastWin32Error());
        }
    }

    public IntPtr Handle => _handle;

    /// <summary>
    /// Waits for the operation to complete and returns its result document.
    /// Throws when either the initial call or the wait reported a failure.
    /// </summary>
    public string? Complete(int callResult, string operationName)
    {
        var waitResult = HcsNative.HcsWaitForOperationResult(_handle, uint.MaxValue, out var document);
        var text = TakeString(document);

        if (callResult != HcsNative.HcsEOperationPending && callResult < 0)
        {
            throw HcsErrors.ToException(callResult, operationName, text);
        }

        if (waitResult < 0)
        {
            throw HcsErrors.ToException(waitResult, operationName, text);
        }

        return text;
    }

    private static string? TakeString(IntPtr pointer)
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
            HcsNative.LocalFree(pointer);
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            HcsNative.HcsCloseOperation(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

/// <summary>Maps HCS/HRESULT failures to meaningful domain exceptions.</summary>
internal static class HcsErrors
{
    public const int HcsEHypervNotInstalled = unchecked((int)0x80370102);
    public const int HcsESystemAlreadyExists = unchecked((int)0x8037010F);
    public const int HcsENotFound = unchecked((int)0x80370110);
    public const int HcsETerminated = unchecked((int)0x80370117);
    public const int HcsEAccessDenied = unchecked((int)0x8037011B);
    public const int HcsEOperationTimeout = unchecked((int)0xC0370104);

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

        if (hresult == HcsESystemAlreadyExists)
        {
            return new SandboxDomainException($"A compute system with that id already exists ({operationName}).{suffix}");
        }

        if (hresult == HcsENotFound)
        {
            return new SandboxNotFoundException($"The compute system was not found ({operationName}).{suffix}");
        }

        if (hresult == HcsEHypervNotInstalled)
        {
            return new SandboxNotSupportedException($"The Hyper-V hypervisor is not available ({operationName}).{suffix}");
        }

        if (hresult == HcsETerminated)
        {
            return new SandboxDomainException($"The compute system has already terminated ({operationName}).{suffix}");
        }

        return new SandboxBackendException(
            $"HCS '{operationName}' failed with HRESULT 0x{hresult:X8}.{suffix}", hresult);
    }
}
