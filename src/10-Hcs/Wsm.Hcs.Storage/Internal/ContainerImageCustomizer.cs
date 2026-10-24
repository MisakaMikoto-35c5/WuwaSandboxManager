using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="IContainerImageCustomizer"/>: patches a prepared base image
/// layer's <c>SYSTEM</c> hive so the container boots an interactive console
/// session. See the interface for why this is an image-level change.
/// </summary>
[ExposeServices(typeof(IContainerImageCustomizer))]
internal sealed class ContainerImageCustomizer : IContainerImageCustomizer, ISingletonDependency
{
    /// <summary>
    /// <c>smss.exe</c> creates <c>SessionId</c> 0 (services) plus one interactive
    /// console session for this value; <c>1</c> (the container images' active
    /// control set) leaves the console session out entirely.
    /// </summary>
    internal const int InteractiveSessionCount = 2;

    private const string SessionManagerSubKey = @"Control\Session Manager";
    private const string InitialSessionsValue = "NumberOfInitialSessions";
    private const string ControlSetPrefix = "ControlSet";

    /// <summary>Where <c>Userinit</c> lives - the documented logon extension point.</summary>
    private const string WinlogonSubKey = @"Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>The value our logon hook is appended to.</summary>
    internal const string UserinitValue = "Userinit";

    /// <summary>The value <c>Userinit</c> has when an image sets none.</summary>
    internal const string DefaultUserinit = @"C:\Windows\system32\userinit.exe";

    /// <summary>The shell <c>winlogon</c> starts after logon.</summary>
    private const string ShellValue = "Shell";

    /// <summary>Auto-logon switch, user and password values under the same key.</summary>
    private const string AutoAdminLogonValue = "AutoAdminLogon";
    private const string DefaultUserNameValue = "DefaultUserName";
    private const string DefaultPasswordValue = "DefaultPassword";
    private const string ForceAutoLogonValue = "ForceAutoLogon";

    /// <summary>The LSA switch that permits auto-logon with a blank password.</summary>
    private const string LimitBlankPasswordValue = "LimitBlankPasswordUse";

    /// <summary>
    /// The <c>Policies\System</c> key and its switch that skips the Ctrl+Alt+Del screen -
    /// unreachable inside a container.
    /// </summary>
    private const string PoliciesSystemSubKey = @"Microsoft\Windows\CurrentVersion\Policies\System";
    private const string DisableCadValue = "DisableCAD";

    /// <summary>Folder inside the container that receives the payload files.</summary>
    private const string PayloadFolder = "wsm";

    /// <summary>The payload files copied into the image, by file name.</summary>
    private static readonly string[] PayloadFiles =
    {
        "wsm-winlogon-wrap.cmd",
        "wsm-desktop-read.cmd",
        "wsm-desktop-read.ps1",
    };

    /// <summary>The persistent shell payload, installed additionally by the desktop-session patch.</summary>
    private const string ShellPayloadFile = "wsm-shell.cmd";

    private readonly ILogger<ContainerImageCustomizer> _logger;

    public ContainerImageCustomizer(ILogger<ContainerImageCustomizer> logger) => _logger = logger;

    public ContainerImageSessionPrep EnableInteractiveSession(string layerPath)
    {
        var hiveFile = ResolveSystemHive(layerPath);
        var updated = new List<string>();

        using (var hive = OfflineRegistryHive.Load(hiveFile))
        {
            using var root = hive.OpenRoot()
                ?? throw new SandboxBackendException(
                    $"The offline hive '{hiveFile}' has no root key.", 0);

            var controlSets = root.GetSubKeyNames()
                .Where(name => name.StartsWith(ControlSetPrefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (controlSets.Length == 0)
            {
                throw new SandboxBackendException(
                    $"The image hive '{hiveFile}' has no {ControlSetPrefix}* key; it does not look like a " +
                    "Windows SYSTEM hive.",
                    0);
            }

            foreach (var controlSet in controlSets)
            {
                using var key = hive.OpenSubKey($@"{controlSet}\{SessionManagerSubKey}", writable: true);
                if (key is null)
                {
                    _logger.LogWarning(
                        "The image hive '{Hive}' has no '{Key}' key; skipping it.", hiveFile, controlSet);
                    continue;
                }

                var before = key.GetValue(InitialSessionsValue);
                key.SetValue(InitialSessionsValue, InteractiveSessionCount, RegistryValueKind.DWord);
                updated.Add(controlSet);

                _logger.LogInformation(
                    "Container base image '{Hive}': {ControlSet}\\{Key}\\{Value} {Before} -> {After}.",
                    hiveFile, controlSet, SessionManagerSubKey, InitialSessionsValue,
                    before is null ? "<unset>" : before, InteractiveSessionCount);
            }
        }

        _logger.LogInformation(
            "The base image at '{Layer}' now boots an interactive console session ({Count} control set(s) updated: {Sets}).",
            layerPath, updated.Count, string.Join(", ", updated));

        return new ContainerImageSessionPrep(hiveFile, updated);
    }

    /// <summary>
    /// The <c>SYSTEM</c> hive inside a prepared base image layer. A
    /// <c>vhd-with-hives</c> layer (what <c>ProcessBaseImage</c> produces) keeps
    /// the real file at <c>Files\Windows\System32\config\SYSTEM</c> and
    /// <b>hard-links</b> <c>Hives\SYSTEM_BASE</c> to it, so patching either
    /// changes both; a plain extracted layer only has the former.
    /// </summary>
    internal static string ResolveSystemHive(string layerPath)
    {
        var root = Path.GetFullPath(layerPath);
        var files = Path.Combine(root, "Files", "Windows", "System32", "config", "SYSTEM");
        if (File.Exists(files))
        {
            return files;
        }

        var hardLinked = Path.Combine(root, "Hives", "SYSTEM_BASE");
        if (File.Exists(hardLinked))
        {
            return hardLinked;
        }

        throw new SandboxValidationException(
            $"'{root}' is not a prepared WCOW base image layer (no " +
            @"'Files\Windows\System32\config\SYSTEM'). Import a Windows Server image first.");
    }

    /// <summary>The <c>SOFTWARE</c> hive of a prepared base image layer (same layout rules).</summary>
    internal static string ResolveSoftwareHive(string layerPath)
    {
        var root = Path.GetFullPath(layerPath);
        var files = Path.Combine(root, "Files", "Windows", "System32", "config", "SOFTWARE");
        if (File.Exists(files))
        {
            return files;
        }

        var hardLinked = Path.Combine(root, "Hives", "SOFTWARE_BASE");
        if (File.Exists(hardLinked))
        {
            return hardLinked;
        }

        throw new SandboxValidationException(
            $"'{root}' is not a prepared WCOW base image layer (no " +
            @"'Files\Windows\System32\config\SOFTWARE'). Import a Windows Server image first.");
    }

    /// <inheritdoc />
    public ContainerImageDesktopPrep EnableWindowMirror(
        string layerPath, ContainerImageDesktopOptions? options = null)
    {
        var hiveFile = ResolveSoftwareHive(layerPath);
        InstallPayload(layerPath, PayloadFiles, out var payloadPaths);

        string before;
        string after;

        using (var hive = OfflineRegistryHive.Load(hiveFile))
        {
            using var root = hive.OpenRoot()
                ?? throw new SandboxBackendException(
                    $"The offline hive '{hiveFile}' has no root key.", 0);

            using var winlogon = hive.OpenOrCreateSubKey(WinlogonSubKey)
                ?? throw new SandboxBackendException(
                    $"Cannot open or create '{WinlogonSubKey}' in '{hiveFile}'.", 0);

            before = winlogon.GetValue(UserinitValue) as string ?? string.Empty;
            after = AppendUserinit(before, ContainerDesktopPaths.WrapCommand);

            // Write through reg.exe, always. The managed write is refused even for SYSTEM
            // ("Cannot write to the registry key." - the loaded hive keeps its own key security),
            // while reg.exe writes the same values successfully. Measured on this very hive, so
            // there is no reason to keep a path that silently refuses the patch.
            WriteWithRegExe(hive.MountName, WinlogonSubKey, UserinitValue, after);

            if (!string.IsNullOrWhiteSpace(options?.Shell))
            {
                WriteWithRegExe(hive.MountName, WinlogonSubKey, ShellValue, options!.Shell!);
                _logger.LogInformation("The base image will start the shell '{Shell}' after logon.", options.Shell);
            }

            if (!string.IsNullOrWhiteSpace(options?.AutoLogonUser))
            {
                // A read side that starts at logon needs a logon; these are the same values the
                // engine writes at container start, baked into the image instead (so a container
                // that is not started by WSM still works).
                WriteWithRegExe(hive.MountName, WinlogonSubKey, AutoAdminLogonValue, "1");
                WriteWithRegExe(hive.MountName, WinlogonSubKey, DefaultUserNameValue, options.AutoLogonUser!);
                WriteWithRegExe(hive.MountName, WinlogonSubKey, DefaultPasswordValue, options.AutoLogonPassword ?? string.Empty);
                // Force it on every pass: without it winlogon can stay at LogonUI after the first
                // try (and in a container there is nobody to type credentials).
                WriteWithRegExe(hive.MountName, WinlogonSubKey, ForceAutoLogonValue, "1");
                _logger.LogInformation(
                    "The base image will log on '{User}' automatically (blank password: {Blank}).",
                    options.AutoLogonUser, string.IsNullOrEmpty(options.AutoLogonPassword));
            }
            else
            {
                // No logon settings asked for: remove what an earlier patch may have left behind.
                // Auto-logon that cannot complete is worse than none - the container's winlogon
                // then exits after the failed attempt instead of sitting at LogonUI (measured).
                foreach (var name in new[]
                         {
                             AutoAdminLogonValue, DefaultUserNameValue, DefaultPasswordValue, ForceAutoLogonValue,
                         })
                {
                    RemoveWithRegExe(hive.MountName, WinlogonSubKey, name);
                }
            }

            if (!string.IsNullOrWhiteSpace(options?.AutoLogonUser) && string.IsNullOrEmpty(options.AutoLogonPassword))
            {
                // Container images ship the built-in accounts with no password, and Windows
                // refuses a blank-password auto-logon unless the LSA allows it. Without this the
                // container stops at LogonUI and nothing that needs a logon runs.
                AllowBlankPasswordAutoLogon(layerPath);
            }

            _logger.LogInformation(
                "Container base image '{Hive}': {Key}\\{Value} '{Before}' -> '{After}'.",
                hiveFile, WinlogonSubKey, UserinitValue, before, after);
        }

        _logger.LogInformation(
            "The base image at '{Layer}' now carries the window-mirror read side ({Payload}). The " +
            "image's logon hook starts it, which needs a container that can complete an interactive " +
            "logon - measured NOT to happen for process-isolated containers on this host (see " +
            "docs/investigation/13-container-desktop-and-rdp.md section 23.4). Start it directly in " +
            "the container's session instead; restart any container built from this image.",
            layerPath, string.Join(", ", payloadPaths));

        return new ContainerImageDesktopPrep(
            hiveFile, before, after, payloadPaths, options?.Shell, options?.AutoLogonUser);
    }

    /// <inheritdoc />
    public ContainerImageDesktopSessionPrep EnableDesktopSession(
        string layerPath, DesktopSessionOptions? options = null)
    {
        options ??= new DesktopSessionOptions();

        // The console session first: without NumberOfInitialSessions=2 winlogon never
        // starts and none of the logon settings below ever fire.
        var session = EnableInteractiveSession(layerPath);

        // Then the services that let the logon COMPLETE: winlogon's interactive logon is
        // arbitrated by the LocalSessionManager, which lives in TermService (SessionEnv runs
        // the session's shell infrastructure), and the stock images ship both disabled, so
        // the console session sits at LogonUI and is torn down again (measured, doc section
        // 25.8). This must be an image-level change: 'sc config' inside the container writes
        // the SYSTEM hive through the lazy flusher, and a hard compute-system terminate
        // seconds later loses the write (measured).
        EnableSessionServices(layerPath);

        var payloadFiles = options.InstallWindowMirrorReadSide
            ? PayloadFiles.Append(ShellPayloadFile).ToArray()
            : new[] { ShellPayloadFile };
        InstallPayload(layerPath, payloadFiles, out var payloadPaths);

        var hiveFile = ResolveSoftwareHive(layerPath);
        using (var hive = OfflineRegistryHive.Load(hiveFile))
        {
            using var root = hive.OpenRoot()
                ?? throw new SandboxBackendException(
                    $"The offline hive '{hiveFile}' has no root key.", 0);

            using var winlogon = hive.OpenOrCreateSubKey(WinlogonSubKey)
                ?? throw new SandboxBackendException(
                    $"Cannot open or create '{WinlogonSubKey}' in '{hiveFile}'.", 0);

            // All writes go through reg.exe (see EnableWindowMirror for why the managed
            // write is refused). ForceAutoLogon on every pass: without it winlogon can
            // stay at LogonUI after the first try, and in a container nobody can type.
            WriteWithRegExe(hive.MountName, WinlogonSubKey, ShellValue, ContainerDesktopPaths.ShellCommand);
            WriteWithRegExe(hive.MountName, WinlogonSubKey, AutoAdminLogonValue, "1");
            WriteWithRegExe(hive.MountName, WinlogonSubKey, DefaultUserNameValue, options.UserName);
            WriteWithRegExe(hive.MountName, WinlogonSubKey, DefaultPasswordValue, options.Password);
            WriteWithRegExe(hive.MountName, WinlogonSubKey, ForceAutoLogonValue, "1");

            // No one can press Ctrl+Alt+Del inside a container: keep LogonUI from ever
            // gating the console session behind a secure-attention screen.
            WriteWithRegExe(
                hive.MountName, PoliciesSystemSubKey, DisableCadValue, "1", "REG_DWORD");
        }

        // Auto-logon is refused while LSA limits blank/known-password use; the password is
        // only effective once the engine's start-time 'net user' sets the same value on the
        // account (doc section 11.7 measured exactly that pairing to log on successfully).
        AllowBlankPasswordAutoLogon(layerPath);

        _logger.LogInformation(
            "The base image at '{Layer}' now boots a console session, logs on '{User}' automatically " +
            "and runs '{Shell}' as its shell ({Payload}). The engine must set the same password at " +
            "container start; restart any container built from this image.",
            layerPath, options.UserName, ContainerDesktopPaths.ShellCommand, string.Join(", ", payloadPaths));

        return new ContainerImageDesktopSessionPrep(
            session, hiveFile, ContainerDesktopPaths.ShellCommand, options.UserName, payloadPaths);
    }

    /// <summary>
    /// Deletes one value from a loaded offline hive through <c>reg.exe</c>, ignoring the case
    /// where the value was never there.
    /// </summary>
    private static void RemoveWithRegExe(string mountName, string subKey, string name)
    {
        var arguments = $@"delete ""HKLM\{mountName}\{subKey}"" /v ""{name}"" /f";
        var startInfo = new ProcessStartInfo("reg.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return;
        }

        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    /// <summary>
    /// Writes one value into a loaded offline hive through <c>reg.exe</c>. Measured: this works
    /// where the managed write is refused, even for SYSTEM - including inside the LSA key.
    /// </summary>
    private static void WriteWithRegExe(
        string mountName, string subKey, string name, string value, string type = "REG_SZ")
    {
        var arguments = $@"add ""HKLM\{mountName}\{subKey}"" /v ""{name}"" /t {type} /d ""{value}"" /f";
        var startInfo = new ProcessStartInfo("reg.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = Process.Start(startInfo)
            ?? throw new SandboxBackendException("Could not start reg.exe to patch the image hive.", 0);
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new SandboxBackendException(
                $"reg.exe could not write '{name}' (exit {process.ExitCode}): {output.Trim()}", 0);
        }
    }

    /// <summary>
    /// Sets <c>TermService</c> and <c>SessionEnv</c> to auto-start in every control set of
    /// the image's SYSTEM hive. winlogon's interactive logon is arbitrated by the
    /// LocalSessionManager (part of TermService); with the service disabled the console
    /// session's logon never completes, winlogon sits at LogonUI and the session is torn
    /// down again (measured on servercore:ltsc2022).
    /// </summary>
    private void EnableSessionServices(string layerPath)
    {
        var hiveFile = ResolveSystemHive(layerPath);
        using var hive = OfflineRegistryHive.Load(hiveFile);
        using var root = hive.OpenRoot()
            ?? throw new SandboxBackendException($"The offline hive '{hiveFile}' has no root key.", 0);

        foreach (var controlSet in root.GetSubKeyNames()
                     .Where(name => name.StartsWith(ControlSetPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var service in new[] { "TermService", "SessionEnv" })
            {
                WriteWithRegExe(
                    hive.MountName, $@"{controlSet}\Services\{service}", "Start", "2", "REG_DWORD");
                _logger.LogInformation(
                    "The base image '{Hive}': {Set}\\Services\\{Service}\\Start -> 2 (auto-start).",
                    hiveFile, controlSet, service);
            }
        }
    }

    /// <summary>
    /// Sets <c>Lsa\LimitBlankPasswordUse = 0</c> in every control set of the image's SYSTEM
    /// hive, so auto-logon works with the blank password container images ship.
    /// </summary>
    private void AllowBlankPasswordAutoLogon(string layerPath)
    {
        var hiveFile = ResolveSystemHive(layerPath);
        using var hive = OfflineRegistryHive.Load(hiveFile);
        using var root = hive.OpenRoot()
            ?? throw new SandboxBackendException($"The offline hive '{hiveFile}' has no root key.", 0);

        foreach (var controlSet in root.GetSubKeyNames()
                     .Where(name => name.StartsWith(ControlSetPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            // reg.exe, for the same reason as above: the LSA key refuses the managed write.
            // REG_DWORD matters - LSA ignores a string value and would keep refusing a
            // blank-password logon.
            WriteWithRegExe(
                hive.MountName, $@"{controlSet}\Control\Lsa", LimitBlankPasswordValue, "0", "REG_DWORD");
            _logger.LogInformation(
                "The base image '{Hive}': {Set}\\Control\\Lsa\\{Value} -> 0 (blank-password auto-logon).",
                hiveFile, controlSet, LimitBlankPasswordValue);
        }
    }

    /// <summary>Appends our hook to the comma-separated <c>Userinit</c> list, idempotently.</summary>
    internal static string AppendUserinit(string current, string hook)
    {
        var entries = (string.IsNullOrWhiteSpace(current) ? DefaultUserinit : current)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (!entries.Any(e => string.Equals(e, hook, StringComparison.OrdinalIgnoreCase)))
        {
            entries.Add(hook);
        }

        return string.Join(",", entries);
    }

    private string InstallPayload(string layerPath, IReadOnlyList<string> files, out IReadOnlyList<string> containerPaths)
    {
        var directory = Path.Combine(Path.GetFullPath(layerPath), "Files", PayloadFolder);
        Directory.CreateDirectory(directory);

        var installed = new List<string>();
        foreach (var file in files)
        {
            var target = Path.Combine(directory, file);
            var resource = $"Wsm.Hcs.Storage.Images.{file}";
            try
            {
                using var stream = typeof(ContainerImageCustomizer).Assembly.GetManifestResourceStream(resource)
                    ?? throw new SandboxBackendException($"The embedded image payload '{resource}' is missing.", 0);
                using var output = File.Create(target);
                stream.CopyTo(output);
            }
            catch (IOException ex)
            {
                // A layer file can be pinned by whatever created the image, and refusing to patch
                // the registry because of one locked payload file would be the wrong trade.
                _logger.LogWarning(
                    "Could not refresh the image payload '{File}' ({Message}); keeping the copy already " +
                    "in the image.", target, ex.Message);
            }

            installed.Add($@"C:\{PayloadFolder}\{file}");
        }

        containerPaths = installed;
        return directory;
    }
}
