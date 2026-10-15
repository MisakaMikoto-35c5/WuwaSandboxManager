using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

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
}
