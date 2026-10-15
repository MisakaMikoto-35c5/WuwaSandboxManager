using System.Runtime.InteropServices;
using Microsoft.Win32;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Loads an <b>offline</b> registry hive — a WCOW image's hive file
/// (<c>Files\Windows\System32\config\SYSTEM</c>) — into
/// <c>HKEY_LOCAL_MACHINE</c> under a temporary key, so its values can be
/// read/written through the managed <see cref="Registry"/> API, then unloads it
/// again on <see cref="Dispose"/>.
/// <para>
/// Editing an image's <c>SYSTEM</c> hive is the only way to change what
/// <c>smss.exe</c> reads before a container's first user-mode process starts: a
/// container's registry <i>is</i> the image's hives (unioned through WCIFS), so
/// there is no earlier hook. The canonical example is
/// <c>Session Manager\NumberOfInitialSessions</c>, which decides whether the
/// container gets an interactive <b>console</b> session at all — see
/// <see cref="IContainerImageCustomizer"/>.
/// </para>
/// <para>
/// <c>RegLoadKey</c>/<c>RegUnLoadKey</c> require <c>SeRestorePrivilege</c> and
/// <c>SeBackupPrivilege</c>; both are enabled on the process token for the
/// lifetime of this object (administrators hold them, but they start disabled).
/// The hive file must not be in use: while any container built from the image is
/// running, the layer is mounted and the file is exclusively locked, so the
/// import/prepare step is the right time to patch it.
/// </para>
/// </summary>
internal sealed class OfflineRegistryHive : IDisposable
{
    /// <summary>Prefix of the temporary HKLM key the hive is mounted under.</summary>
    private const string MountPrefix = "WSM_IMAGE_";

    private static readonly IntPtr HKeyLocalMachine = new(unchecked((int)0x80000002));

    private readonly TokenPrivileges _privileges;
    private bool _disposed;

    private OfflineRegistryHive(string mountName, string hiveFile, TokenPrivileges privileges)
    {
        MountName = mountName;
        HiveFile = hiveFile;
        _privileges = privileges;
    }

    /// <summary>The temporary key name the hive is mounted under (below <c>HKLM</c>).</summary>
    public string MountName { get; }

    /// <summary>The hive file that was loaded.</summary>
    public string HiveFile { get; }

    /// <summary>Loads <paramref name="hiveFile"/> and returns the mounted hive.</summary>
    /// <exception cref="SandboxValidationException">The file does not exist.</exception>
    /// <exception cref="SandboxBackendException">The hive could not be loaded.</exception>
    public static OfflineRegistryHive Load(string hiveFile)
    {
        if (!File.Exists(hiveFile))
        {
            throw new SandboxValidationException($"The offline registry hive '{hiveFile}' does not exist.");
        }

        var privileges = TokenPrivileges.Enable("SeRestorePrivilege", "SeBackupPrivilege");
        var mountName = MountPrefix + Guid.NewGuid().ToString("N")[..12];

        var status = RegLoadKey(HKeyLocalMachine, mountName, hiveFile);
        if (status != 0)
        {
            privileges.Dispose();
            throw new SandboxBackendException(
                $"Loading the offline hive '{hiveFile}' failed (0x{status:X8}). Is a container built from this " +
                "image still running (the layer, and with it the hive file, is then locked)?",
                unchecked((int)status));
        }

        return new OfflineRegistryHive(mountName, hiveFile, privileges);
    }

    /// <summary>
    /// Opens a key inside the mounted hive (the path is relative to its root,
    /// e.g. <c>ControlSet001\Control\Session Manager</c>), or <c>null</c> when it
    /// does not exist.
    /// </summary>
    public RegistryKey? OpenSubKey(string subKey, bool writable)
        => Registry.LocalMachine.OpenSubKey($"{MountName}\\{subKey}", writable);

    /// <summary>The mounted hive's root key (read-only).</summary>
    public RegistryKey? OpenRoot() => Registry.LocalMachine.OpenSubKey(MountName);

    /// <summary>
    /// Opens a subkey writable, creating it when it is missing. The creation path matters:
    /// <c>RegistryKey.CreateSubKey(name, writable: true)</c> hands back a <b>read-only</b> handle,
    /// so writes through it fail with "Cannot write to the registry key".
    /// </summary>
    public RegistryKey? OpenOrCreateSubKey(string subKey)
    {
        var existing = OpenSubKey(subKey, writable: true);
        if (existing is not null)
        {
            return existing;
        }

        using var root = OpenRoot();
        if (root is null)
        {
            return null;
        }

        using (root.CreateSubKey(subKey))
        {
        }

        return OpenSubKey(subKey, writable: true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // UnloadKey fails (ERROR_SHARING_VIOLATION, 0x20) while handles to the
        // hive are still open, so nothing may hold a RegistryKey here. Callers
        // therefore open keys in short-lived using blocks.
        var status = RegUnLoadKey(HKeyLocalMachine, MountName);
        _privileges.Dispose();

        if (status != 0)
        {
            throw new SandboxBackendException(
                $"Unloading the offline hive '{HiveFile}' failed (0x{status:X8}); a key opened from it may " +
                "still be in use.",
                unchecked((int)status));
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

    /// <summary>
    /// Enables privileges on the current process token (best effort: a privilege
    /// the token does not hold simply stays unavailable).
    /// </summary>
    private sealed class TokenPrivileges : IDisposable
    {
        private const uint TokenAdjustPrivileges = 0x0020;
        private const uint TokenQuery = 0x0008;
        private const uint SePrivilegeEnabled = 0x0002;

        private readonly IntPtr _token;

        private TokenPrivileges(IntPtr token) => _token = token;

        public static TokenPrivileges Enable(params string[] names)
        {
            OpenProcessToken(GetCurrentProcess(), TokenAdjustPrivileges | TokenQuery, out var token);

            var privileges = new TokenPrivileges(token);
            if (token == IntPtr.Zero)
            {
                return privileges;
            }

            foreach (var name in names)
            {
                if (!LookupPrivilegeValue(null, name, out var luid))
                {
                    continue;
                }

                var state = new TokenPrivilegesState
                {
                    PrivilegeCount = 1,
                    Luid = luid,
                    Attributes = SePrivilegeEnabled,
                };

                AdjustTokenPrivileges(token, disableAllPrivileges: false, ref state, 0, IntPtr.Zero, IntPtr.Zero);
            }

            return privileges;
        }

        public void Dispose()
        {
            if (_token != IntPtr.Zero)
            {
                CloseHandle(_token);
            }
        }

        /// <summary>Matches the native <c>TOKEN_PRIVILEGES</c> for a single privilege.</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct TokenPrivilegesState
        {
            public uint PrivilegeCount;
            public Luid Luid;
            public uint Attributes;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Luid
        {
            public uint LowPart;
            public int HighPart;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string? system, string name, out Luid luid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(
            IntPtr token, bool disableAllPrivileges, ref TokenPrivilegesState newState, uint bufferLength,
            IntPtr previousState, IntPtr returnLength);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
