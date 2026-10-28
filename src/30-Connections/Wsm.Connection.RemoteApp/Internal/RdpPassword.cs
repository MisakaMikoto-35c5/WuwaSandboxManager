using System.Runtime.InteropServices;
using System.Text;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// Encodes the credential the way <c>mstsc.exe</c> expects it in an <c>.rdp</c>
/// file: the UTF-16LE password bytes are protected with DPAPI (current-user
/// scope) and written as a lowercase hex string after <c>password 51:b:</c>.
/// This mirrors what remoteapptool, the Google Cloud VS extension and other
/// .rdp writers do; mstsc decrypts the blob transparently on connect, so the
/// clear-text password never sits in the file.
/// </summary>
internal static class RdpPassword
{
    private const int CryptProtectUiForbidden = 0x1;

    /// <summary>Returns the lowercase hex DPAPI blob for <paramref name="password"/>.</summary>
    public static string Protect(string password)
    {
        var bytes = Encoding.Unicode.GetBytes(password);
        var input = new DataBlob
        {
            Size = bytes.Length,
            Data = Marshal.AllocHGlobal(bytes.Length),
        };

        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);

            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return Convert.ToHexStringLower(protectedBytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
