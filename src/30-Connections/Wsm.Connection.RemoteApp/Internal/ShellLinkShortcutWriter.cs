using System.Runtime.InteropServices;
using System.Text;
using Volo.Abp.DependencyInjection;

namespace Wsm.Connection.RemoteApp.Internal;

/// <summary>
/// Default <see cref="IRemoteAppShortcutWriter"/>. Writes a <c>.lnk</c> through
/// the shell link COM object (<c>CLSID_ShellLink</c>), pointing at the companion
/// <c>wsm</c> CLI so the connection is resolved live:
/// <c>connect &lt;id&gt; --remoteapp --program "&lt;program&gt;"
/// [--program-args "&lt;args&gt;"] --quiet</c>.
/// </summary>
[ExposeServices(typeof(IRemoteAppShortcutWriter))]
internal sealed class ShellLinkShortcutWriter : IRemoteAppShortcutWriter, ISingletonDependency
{
    public void Create(RemoteAppShortcutRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.LinkPath))
        {
            throw new ArgumentException("A shortcut path is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.CliExecutablePath))
        {
            throw new ArgumentException("A CLI executable path is required.", nameof(request));
        }

        var directory = Path.GetDirectoryName(request.LinkPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var linkType = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"))
            ?? throw new InvalidOperationException("The shell link COM class is not registered.");
        var link = (IShellLinkW)Activator.CreateInstance(linkType)!;
        try
        {
            link.SetPath(request.CliExecutablePath);
            link.SetArguments(BuildArguments(request));
            link.SetWorkingDirectory(Path.GetDirectoryName(request.CliExecutablePath) ?? string.Empty);
            link.SetDescription(request.Description ?? request.Program);
            link.SetShowCmd(1); // SW_SHOWNORMAL
            link.SetIconLocation(request.IconPath ?? MstscClientTracker.ClientExecutablePath(), 0);

            ((IPersistFile)link).Save(request.LinkPath, fRemember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    /// <summary>The <c>wsm connect</c> command line the shortcut runs.</summary>
    internal static string BuildArguments(RemoteAppShortcutRequest request)
    {
        var arguments = new StringBuilder();
        arguments.Append("connect ").Append(request.SandboxId.ToString("D"));
        arguments.Append(" --remoteapp");
        arguments.Append(" --program \"").Append(request.Program).Append('"');

        if (!string.IsNullOrWhiteSpace(request.ProgramArguments))
        {
            arguments.Append(" --program-args \"").Append(request.ProgramArguments).Append('"');
        }

        // The console window is hidden and failures surface in a message box;
        // the shortcut must not leave a console window behind the application.
        arguments.Append(" --quiet");
        return arguments.ToString();
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void SetIDList(IntPtr pidl);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void GetHotkey(out short pwHotkey);

        void SetHotkey(short wHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetShowCmd(int iShowCmd);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

        void Resolve(IntPtr hwnd, uint fFlags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder ppszFileName);
    }
}
