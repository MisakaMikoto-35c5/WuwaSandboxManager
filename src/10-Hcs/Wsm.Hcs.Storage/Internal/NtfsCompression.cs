using System.Runtime.InteropServices;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Minimal NTFS per-file/directory compression control via
/// <c>FSCTL_SET_COMPRESSION</c>.
/// <para>
/// HCS's synthetic storage rejects a VHDX (or any disk in its parent chain)
/// that carries the NTFS <c>Compressed</c> attribute with
/// <c>0xC03A001A</c> (<c>STATUS_VIRTUAL_DISK_LIMITATION</c>). The host
/// <c>...\Windows\Containers</c> tree is itself compressed, so a directory
/// created under it inherits the attribute and every file written into it is
/// created compressed too. <see cref="File.SetAttributes"/> cannot clear the
/// directory attribute (the flag is not settable that way), so this uses the
/// documented filesystem control code.
/// </para>
/// Every method is best-effort: on a non-NTFS volume (or an access failure)
/// the call is a no-op and the caller carries on.
/// </summary>
internal static class NtfsCompression
{
    /// <summary><c>FSCTL_SET_COMPRESSION</c>.</summary>
    private const uint FsctlSetCompression = 0x0009C040;

    /// <summary><c>COMPRESSION_FORMAT_NONE</c> — decompress.</summary>
    private const ushort CompressionFormatNone = 0;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;

    /// <summary>Required to open a <b>directory</b> handle with <c>CreateFile</c>.</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>True when <paramref name="path"/> currently carries the <c>Compressed</c> attribute.</summary>
    public static bool IsCompressed(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.Compressed) != 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Clears the <c>Compressed</c> attribute from a file or directory (and, for
    /// a directory, makes newly created children uncompressed by inheritance).
    /// Returns <see langword="true"/> when the path is uncompressed afterwards.
    /// </summary>
    public static bool TryUncompress(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path)))
        {
            return false;
        }

        if (!IsCompressed(path))
        {
            return true;
        }

        return TrySetCompression(path, CompressionFormatNone);
    }

    private static bool TrySetCompression(string path, ushort state)
    {
        var handle = CreateFile(
            path,
            GenericRead | GenericWrite,
            shareMode: 0,
            securityAttributes: IntPtr.Zero,
            creationDisposition: OpenExisting,
            flagsAndAttributes: FileFlagBackupSemantics,
            templateFile: IntPtr.Zero);

        if (handle == InvalidHandleValue)
        {
            return false;
        }

        try
        {
            var input = state;
            return DeviceIoControl(
                handle,
                FsctlSetCompression,
                ref input,
                sizeof(ushort),
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(
        IntPtr device,
        uint ioControlCode,
        ref ushort inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
