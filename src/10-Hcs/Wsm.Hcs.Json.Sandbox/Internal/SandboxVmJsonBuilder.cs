using System.Text.Json;
using Volo.Abp.DependencyInjection;

namespace Wsm.Hcs.Json.Sandbox.Internal;

/// <summary>
/// Default <see cref="ISandboxVmJsonBuilder"/>. The shape follows the HCS schema
/// 2.x plus the Sandbox-specific <c>RunInSilo</c> extension observed in the
/// platform's own configuration.
/// </summary>
[ExposeServices(typeof(ISandboxVmJsonBuilder))]
internal sealed class SandboxVmJsonBuilder : ISandboxVmJsonBuilder, ITransientDependency
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
    };

    public string Build(HcsSandboxDefinition definition)
    {
        var services = new List<object>
        {
            Service(HcsSandboxConstants.LsmServiceId),
            Service(HcsSandboxConstants.NlmServiceId),
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
                        ["Id"] = HcsSandboxConstants.HostLayerId,
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
