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
    private const string WinlogonSubKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

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

    /// <summary>Folder inside the container that receives the payload files.</summary>
    private const string PayloadFolder = "wsm";

    /// <summary>The payload files copied into the image, by file name.</summary>
    private static readonly string[] PayloadFiles =
    {
        "wsm-winlogon-wrap.cmd",
        "wsm-desktop-read.cmd",
        "wsm-desktop-read.ps1",
    };

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
        InstallPayload(layerPath, out var payloadPaths);

        string before;
        string after;

        using (var hive = OfflineRegistryHive.Load(hiveFile))
        {
            using var root = hive.OpenRoot()
                ?? throw new SandboxBackendException(
                    $"The offline hive '{hiveFile}' has no root key.", 0);

            using var winlogon = root.CreateSubKey(WinlogonSubKey, writable: true)
                ?? throw new SandboxBackendException(
                    $"Cannot open or create '{WinlogonSubKey}' in '{hiveFile}'.", 0);

            before = winlogon.GetValue(UserinitValue) as string ?? string.Empty;
            after = AppendUserinit(before, ContainerDesktopPaths.WrapCommand);
            winlogon.SetValue(UserinitValue, after, RegistryValueKind.String);

            if (!string.IsNullOrWhiteSpace(options?.Shell))
            {
                winlogon.SetValue(ShellValue, options!.Shell!, RegistryValueKind.String);
                _logger.LogInformation("The base image will start the shell '{Shell}' after logon.", options.Shell);
            }

            if (!string.IsNullOrWhiteSpace(options?.AutoLogonUser))
            {
                // A read side that starts at logon needs a logon; these are the same
                // values the engine writes at container start, baked into the image
                // instead (so a container that is not started by WSM still works).
                winlogon.SetValue(AutoAdminLogonValue, "1", RegistryValueKind.String);
                winlogon.SetValue(DefaultUserNameValue, options.AutoLogonUser!, RegistryValueKind.String);
                winlogon.SetValue(DefaultPasswordValue, options.AutoLogonPassword ?? string.Empty, RegistryValueKind.String);
                _logger.LogInformation(
                    "The base image will log on '{User}' automatically (blank password: {Blank}).",
                    options.AutoLogonUser, string.IsNullOrEmpty(options.AutoLogonPassword));
            }

            _logger.LogInformation(
                "Container base image '{Hive}': {Key}\\{Value} '{Before}' -> '{After}'.",
                hiveFile, WinlogonSubKey, UserinitValue, before, after);
        }

        _logger.LogInformation(
            "The base image at '{Layer}' now starts the window-mirror read side inside the container's " +
            "session at logon ({Payload}); restart any container built from it.",
            layerPath, string.Join(", ", payloadPaths));

        return new ContainerImageDesktopPrep(
            hiveFile, before, after, payloadPaths, options?.Shell, options?.AutoLogonUser);
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

    private static string InstallPayload(string layerPath, out IReadOnlyList<string> containerPaths)
    {
        var directory = Path.Combine(Path.GetFullPath(layerPath), "Files", PayloadFolder);
        Directory.CreateDirectory(directory);

        var installed = new List<string>();
        foreach (var file in PayloadFiles)
        {
            var resource = $"Wsm.Hcs.Storage.Images.{file}";
            using var stream = typeof(ContainerImageCustomizer).Assembly.GetManifestResourceStream(resource)
                ?? throw new SandboxBackendException($"The embedded image payload '{resource}' is missing.", 0);
            using var target = File.Create(Path.Combine(directory, file));
            stream.CopyTo(target);
            installed.Add($@"C:\{PayloadFolder}\{file}");
        }

        containerPaths = installed;
        return directory;
    }
}
