using System.Text.Json;

namespace WindowsSandboxManager.Infrastructure.Hcs;

/// <summary>A host folder exposed to the guest through a vSMB share.</summary>
public sealed record HcsMappedFolder(string HostPath, string SandboxPath, bool ReadOnly);

/// <summary>
/// Everything the HCS backend needs to build a Windows Sandbox compute system.
/// All paths are explicit, which is how custom base-image and user-data
/// locations are supported (the platform's own stack hardcodes them).
/// </summary>
public sealed record HcsSandboxDefinition
{
    public required string Id { get; init; }

    /// <summary>Base OS image layer files root (<c>...\Layers\&lt;id&gt;\Files</c>).</summary>
    public required string BaseLayerPath { get; init; }

    /// <summary>User-data VHDX path (custom storage location).</summary>
    public required string StorageDiskPath { get; init; }

    /// <summary>Guest state file path (<c>sandbox.vmgs</c>).</summary>
    public required string GuestStatePath { get; init; }

    public string Owner { get; init; } = "WindowsSandbox";

    public uint MemoryMb { get; init; } = 4096;

    public int CpuCount { get; init; } = 4;

    public bool EnableVirtualGpu { get; init; } = true;

    public bool EnableNetworking { get; init; }

    /// <summary>HNS endpoint id; required for the guest to actually get a NIC.</summary>
    public string? NetworkEndpointId { get; init; }

    public IReadOnlyList<HcsMappedFolder> MappedFolders { get; init; } = Array.Empty<HcsMappedFolder>();

    /// <summary><c>RestoreState.TemplateSystemId</c> used to clone from a template.</summary>
    public string? TemplateSystemId { get; init; }

    /// <summary><c>RestoreState.SaveStateFilePath</c> used to resume a full-state checkpoint.</summary>
    public string? RestoreStateFilePath { get; init; }

    /// <summary>Named pipe for the EnhancedModeVideo (RDP) endpoint.</summary>
    public string? RdpNamedPipe { get; init; }

    public IReadOnlyList<string> RdpAccessSids { get; init; } = Array.Empty<string>();

    /// <summary>Extra HvSocket service GUIDs to expose (beyond LSM/NLM).</summary>
    public IReadOnlyList<Guid> AdditionalHvSocketServices { get; init; } = Array.Empty<Guid>();

    public int SchemaMajor { get; init; } = 2;

    public int SchemaMinor { get; init; } = 1;
}

/// <summary>
/// Builds the JSON configuration document passed to
/// <c>HcsCreateComputeSystem</c> for a Windows Sandbox (DisposableVM) container.
/// The shape follows the HCS schema 2.x plus the Sandbox-specific
/// <c>RunInSilo</c> extension observed in the platform's own configuration.
/// </summary>
public static class HcsSandboxJson
{
    /// <summary>Host layer identifier used by the container filesystem namespace.</summary>
    public const string HostLayerId = "8264f677-40b0-4ca5-bf9a-944ac2da8087";

    // From WindowsSandboxServer.Constants.
    public static readonly Guid LsmServiceId = new("F58797F6-C9F3-4D63-9BD4-E52AC020E586");
    public static readonly Guid NlmServiceId = new("A715AC94-B745-4889-9A0F-772D85A3CFA4");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public static string Build(HcsSandboxDefinition definition)
    {
        var services = new List<object>
        {
            Service(LsmServiceId),
            Service(NlmServiceId),
        };
        foreach (var guid in definition.AdditionalHvSocketServices)
        {
            services.Add(Service(guid));
        }

        var shares = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["Name"] = "os",
                ["Path"] = definition.BaseLayerPath,
                ["Options"] = new Dictionary<string, object?>
                {
                    ["ReadOnly"] = true,
                    ["TakeBackupPrivilege"] = true,
                    ["NoLocks"] = true,
                    ["ReparseBaseLayer"] = true,
                    ["PseudoOplocks"] = true,
                    ["PseudoDirnotify"] = true,
                    ["SupportCloudFiles"] = true,
                },
            },
        };

        foreach (var folder in definition.MappedFolders)
        {
            shares.Add(new Dictionary<string, object?>
            {
                ["Name"] = ShareName(folder.SandboxPath),
                ["Path"] = folder.HostPath,
                ["Options"] = new Dictionary<string, object?>
                {
                    ["ReadOnly"] = folder.ReadOnly,
                    ["TakeBackupPrivilege"] = true,
                    ["NoLocks"] = true,
                },
            });
        }

        var devices = new Dictionary<string, object?>
        {
            ["Scsi"] = new Dictionary<string, object?>
            {
                ["primary"] = new Dictionary<string, object?>
                {
                    ["Attachments"] = new Dictionary<string, object?>
                    {
                        ["0"] = new Dictionary<string, object?>
                        {
                            ["Type"] = "VirtualDisk",
                            ["Path"] = definition.StorageDiskPath,
                            ["CachingMode"] = "ReadOnlyCached",
                            ["NoWriteHardening"] = true,
                            ["DisableExpansionOptimization"] = true,
                            ["IgnoreRelativeLocator"] = true,
                            ["CaptureIoAttributionContext"] = true,
                        },
                    },
                },
            },
            ["HvSocket"] = new Dictionary<string, object?>
            {
                ["HvSocketConfig"] = new Dictionary<string, object?>
                {
                    ["DefaultBindSecurityDescriptor"] = "D:P(A;;FA;;;SY)",
                    ["DefaultConnectSecurityDescriptor"] = "D:P(A;;FA;;;SY)",
                    ["ServiceTable"] = services,
                },
            },
            ["VirtualSmb"] = new Dictionary<string, object?>
            {
                ["Shares"] = shares,
                ["DirectFileMappingInMB"] = 2048,
            },
            ["GuestCrashReporting"] = new Dictionary<string, object?>
            {
                ["WindowsCrashSettings"] = new Dictionary<string, object?>
                {
                    ["DumpType"] = "Full",
                    ["MaxDumpSize"] = 4362076160L,
                },
            },
            ["Battery"] = new Dictionary<string, object?>(),
            ["KernelIntegration"] = new Dictionary<string, object?>(),
        };

        if (definition.EnableVirtualGpu)
        {
            devices["EnhancedModeVideo"] = new Dictionary<string, object?>
            {
                ["ConnectionOptions"] = new Dictionary<string, object?>
                {
                    ["AccessSids"] = definition.RdpAccessSids.ToArray(),
                    ["NamedPipe"] = definition.RdpNamedPipe ?? $@"\\.\pipe\{definition.Id}",
                },
            };
        }

        if (!string.IsNullOrWhiteSpace(definition.NetworkEndpointId))
        {
            devices["NetworkAdapters"] = new Dictionary<string, object?>
            {
                ["0"] = new Dictionary<string, object?>
                {
                    ["EndpointId"] = definition.NetworkEndpointId,
                },
            };
        }

        var virtualMachine = new Dictionary<string, object?>
        {
            ["StopOnReset"] = true,
            ["Chipset"] = new Dictionary<string, object?>
            {
                ["Uefi"] = new Dictionary<string, object?>
                {
                    ["BootThis"] = new Dictionary<string, object?>
                    {
                        ["DeviceType"] = "VmbFs",
                        ["DevicePath"] = @"\EFI\Microsoft\Boot\bootmgfw.efi",
                    },
                },
            },
            ["ComputeTopology"] = new Dictionary<string, object?>
            {
                ["Memory"] = new Dictionary<string, object?>
                {
                    ["SizeInMB"] = definition.MemoryMb,
                    ["Backing"] = "Virtual",
                    ["BackingPageSize"] = "Small",
                    ["EnablePrivateCompressionStore"] = true,
                    ["EnableHotHint"] = true,
                    ["EnableColdHint"] = true,
                    ["SharedMemoryMB"] = 2048,
                    ["EnableEpf"] = true,
                    ["EnableDeferredCommit"] = true,
                },
                ["Processor"] = new Dictionary<string, object?>
                {
                    ["Count"] = definition.CpuCount,
                    ["SynchronizeHostFeatures"] = true,
                    ["EnableSchedulerAssist"] = true,
                },
            },
            ["Devices"] = devices,
            ["GuestState"] = new Dictionary<string, object?>
            {
                ["GuestStateFilePath"] = definition.GuestStatePath,
            },
            ["RunInSilo"] = new Dictionary<string, object?>
            {
                ["SiloBaseOsPath"] = definition.BaseLayerPath,
                ["NotifySiloJobCreated"] = true,
                ["FileSystemLayers"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["Id"] = HostLayerId,
                        ["Path"] = @"C:\",
                        ["PathType"] = "AbsolutePath",
                    },
                },
            },
            ["LaunchOptions"] = new Dictionary<string, object?>
            {
                ["Type"] = "None",
            },
            ["GuestConnection"] = new Dictionary<string, object?>(),
        };

        if (!string.IsNullOrWhiteSpace(definition.TemplateSystemId) ||
            !string.IsNullOrWhiteSpace(definition.RestoreStateFilePath))
        {
            var restore = new Dictionary<string, object?>();
            if (!string.IsNullOrWhiteSpace(definition.TemplateSystemId))
            {
                restore["TemplateSystemId"] = definition.TemplateSystemId;
            }

            if (!string.IsNullOrWhiteSpace(definition.RestoreStateFilePath))
            {
                restore["SaveStateFilePath"] = definition.RestoreStateFilePath;
            }

            virtualMachine["RestoreState"] = restore;
        }

        var root = new Dictionary<string, object?>
        {
            ["Owner"] = definition.Owner,
            ["SchemaVersion"] = new Dictionary<string, object?>
            {
                ["Major"] = definition.SchemaMajor,
                ["Minor"] = definition.SchemaMinor,
            },
            ["VirtualMachine"] = virtualMachine,
            ["ShouldTerminateOnLastHandleClosed"] = true,
        };

        return JsonSerializer.Serialize(root, Options);
    }

    private static object Service(Guid id) => new Dictionary<string, object?>
    {
        ["ServiceId"] = id.ToString(),
        ["BindSecurityDescriptor"] = "D:P(A;;FA;;;SY)",
        ["ConnectSecurityDescriptor"] = "D:P(A;;FA;;;SY)",
    };

    private static string ShareName(string sandboxPath)
    {
        var name = sandboxPath.Replace('\\', '_').Replace(':', '_').Trim('_');
        return string.IsNullOrEmpty(name) ? "mapped" : name;
    }
}
