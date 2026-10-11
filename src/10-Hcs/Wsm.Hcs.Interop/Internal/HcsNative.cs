using System.Runtime.InteropServices;

namespace Wsm.Hcs.Interop.Internal;

/// <summary>
/// P/Invoke surface over the Host Compute Service client library
/// (<c>computecore.dll</c>). This is the modern, operation based API: long
/// running calls return <c>HCS_E_OPERATION_PENDING</c> and complete through an
/// <c>HCS_OPERATION</c> handle that is awaited with
/// <see cref="HcsWaitForOperationResult"/>.
/// <para>
/// HCS requires the caller to be an administrator or in the Hyper-V
/// Administrators group (otherwise every call fails with <c>0x8037011B</c>).
/// </para>
/// </summary>
internal static class HcsNative
{
    private const string ComputeCore = "computecore.dll";

    public const int HcsEOperationPending = unchecked((int)0xC0370103);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern IntPtr HcsCreateOperation(IntPtr context, IntPtr callback);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForOperationResult(IntPtr operation, uint timeoutMs, out IntPtr resultDocument);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseOperation(IntPtr operation);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateComputeSystem(
        string id,
        string configuration,
        IntPtr operation,
        IntPtr securityDescriptor,
        out IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsOpenComputeSystem(string id, uint requestedAccess, out IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseComputeSystem(IntPtr computeSystem);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsStartComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsShutDownComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsTerminateComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsSaveComputeSystem(IntPtr computeSystem, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsGetComputeSystemProperties(IntPtr computeSystem, IntPtr operation, string propertyQuery);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsEnumerateComputeSystems(string query, IntPtr operation);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateEmptyGuestStateFile(string guestStateFilePath);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateEmptyRuntimeStateFile(string runtimeStateFilePath);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsGrantVmAccess(string vmId, string filePath);

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsGrantVmGroupAccess(string filePath);

    // ---- Process execution inside a compute system (HcsCreateProcess) ----

    [DllImport(ComputeCore, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int HcsCreateProcess(
        IntPtr computeSystem,
        string processParameters,
        IntPtr operation,
        IntPtr securityDescriptor,
        out IntPtr process);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForProcessExit(IntPtr process, uint timeoutMs, out IntPtr resultDocument);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern void HcsCloseProcess(IntPtr process);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsTerminateProcess(IntPtr process, IntPtr operation, string options);

    [DllImport(ComputeCore, ExactSpelling = true)]
    public static extern int HcsWaitForOperationResultAndProcessInfo(
        IntPtr operation,
        uint timeoutMs,
        out HcsProcessInformation processInformation,
        out IntPtr resultDocument);

    /// <summary>Mirrors <c>HCS_PROCESS_INFORMATION</c> from <c>computedefs.h</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct HcsProcessInformation
    {
        public uint ProcessId;
        public uint Reserved;
        public IntPtr StdInput;
        public IntPtr StdOutput;
        public IntPtr StdError;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);
}
