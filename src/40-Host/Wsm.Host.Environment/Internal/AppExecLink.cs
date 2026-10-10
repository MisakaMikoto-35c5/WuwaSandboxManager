using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Wsm.Host.Environment.Internal;

/// <summary>
/// Reads the target of an AppExecutionAlias reparse point
/// (<c>IO_REPARSE_TAG_APPEXECLINK</c>). The aliases under
/// <c>%LocalAppData%\Microsoft\WindowsApps</c> are not symbolic links, so
/// <see cref="File.ResolveLinkTarget(string, bool)"/> does not resolve them; this
/// helper reads the reparse data directly and extracts the packaged executable
/// path so we can locate the package install directory without elevation.
/// </summary>
internal static class AppExecLink
{
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const int MaxReparseDataSize = 16 * 1024;
    private const uint OpenExisting = 3;
    private const uint FileShareReadWriteDelete = 0x00000007;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint IoReparseTagAppExecLink = 0x8000001B;

    /// <summary>
    /// Returns the packaged executable path the alias points at, or
    /// <c>null</c> when the file is not an AppExecutionAlias or cannot be read.
    /// </summary>
    public static string? TryGetTarget(string linkPath)
    {
        try
        {
            using var handle = CreateFile(
                linkPath,
                desiredAccess: 0,
                shareMode: FileShareReadWriteDelete,
                securityAttributes: nint.Zero,
                creationDisposition: OpenExisting,
                flagsAndAttributes: FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                templateFile: nint.Zero);

            if (handle.IsInvalid)
            {
                return null;
            }

            var buffer = Marshal.AllocHGlobal(MaxReparseDataSize);
            try
            {
                if (!DeviceIoControl(handle, FsctlGetReparsePoint, nint.Zero, 0, buffer, MaxReparseDataSize, out var bytesReturned, nint.Zero)
                    || bytesReturned < 8)
                {
                    return null;
                }

                var tag = unchecked((uint)Marshal.ReadInt32(buffer));
                if (tag != IoReparseTagAppExecLink)
                {
                    return null;
                }

                var dataLength = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
                if (dataLength <= 0 || dataLength > MaxReparseDataSize - 8)
                {
                    return null;
                }

                var data = new byte[dataLength];
                Marshal.Copy(buffer + 8, data, 0, dataLength);
                return ExtractFirstPath(data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractFirstPath(byte[] data)
    {
        // The AppExecLink buffer is a version DWORD followed by several
        // null-terminated UTF-16 strings (package id, entry point, executable
        // path, application type). We only need the fully qualified path.
        var text = Encoding.Unicode.GetString(data);
        foreach (var part in text.Split('\0'))
        {
            if (part.Length >= 3 && char.IsLetter(part[0]) && part[1] == ':' && (part[2] == '\\' || part[2] == '/'))
            {
                return part;
            }
        }

        return null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        nint inBuffer,
        uint inBufferSize,
        nint outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
