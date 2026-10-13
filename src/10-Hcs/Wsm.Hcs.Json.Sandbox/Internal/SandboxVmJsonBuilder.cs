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
        // HCS serializes HvSocketConfig.ServiceTable as a JSON *object* keyed by
        // the service GUID (each value has no ServiceId member). Emitting an
        // array here is what made HCS reject the document at Construct time
        // (0x8037010D): the property is a map, not a list.
        var serviceTable = new Dictionary<string, object?>
        {
            [HcsSandboxConstants.LsmServiceId.ToString()] = Service("D:P(A;;FA;;;SY)", "D:P(A;;FA;;;SY)", allowWildcardBinds: true),
            [HcsSandboxConstants.NlmServiceId.ToString()] = Service("D:P(A;;FA;;;LS)", "D:P(A;;FA;;;LS)", allowWildcardBinds: true),
        };
        foreach (var service in definition.AdditionalHvSocketServices)
        {
            serviceTable[service.Id.ToString()] = Service(
                service.BindSecurityDescriptor,
                service.ConnectSecurityDescriptor,
                service.AllowWildcardBinds);
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
                    ["ServiceTable"] = serviceTable,
                },
            },
            ["VirtualSmb"] = new Dictionary<string, object?>
            {
                ["Shares"] = shares,
                ["DirectFileMappingInMB"] = 2048,
            },
            ["Licensing"] = new Dictionary<string, object?>
            {
                ["ContainerID"] = Guid.Empty.ToString(),
                ["PackageFamilyNames"] = Array.Empty<string>(),
            },
            ["Battery"] = new Dictionary<string, object?>(),
            ["KernelIntegration"] = new Dictionary<string, object?>(),
        };

        // HCS creates a "Microsoft Guest Crash Dump" virtual device from this
        // section. Its DumpFileName must point at an existing directory or Start
        // fails with 0x80070003; omit the section entirely when no path is known.
        if (!string.IsNullOrWhiteSpace(definition.DumpFilePath))
        {
            devices["GuestCrashReporting"] = new Dictionary<string, object?>
            {
                ["WindowsCrashSettings"] = new Dictionary<string, object?>
                {
                    ["DumpFileName"] = definition.DumpFilePath,
                    ["DumpType"] = "Full",
                    ["MaxDumpSize"] = 4362076160L,
                },
            };
        }

        if (definition.EnableVirtualGpu)
        {
            var connectionOptions = new Dictionary<string, object?>
            {
                ["NamedPipe"] = definition.RdpNamedPipe ?? $@"\\.\pipe\{definition.Id}",
            };

            // AccessSids is optional; an empty array is not meaningful and some
            // HCS builds reject it, so only emit it when the caller supplied SIDs.
            if (definition.RdpAccessSids.Count > 0)
            {
                connectionOptions["AccessSids"] = definition.RdpAccessSids.ToArray();
            }

            devices["EnhancedModeVideo"] = new Dictionary<string, object?>
            {
                ["ConnectionOptions"] = connectionOptions,
            };
        }

        // A NetworkAdapter is only valid with a real HNS endpoint id. HCS does
        // not create network resources, and the HNS endpoint service is not
        // implemented yet, so the adapter is omitted entirely (no networking in
        // the guest) unless an explicit endpoint id was supplied.
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

    private static object Service(string bindDescriptor, string connectDescriptor, bool allowWildcardBinds) => new Dictionary<string, object?>
    {
        ["BindSecurityDescriptor"] = bindDescriptor,
        ["ConnectSecurityDescriptor"] = connectDescriptor,
        ["AllowWildcardBinds"] = allowWildcardBinds,
    };

    private static string ShareName(string sandboxPath)
    {
        var name = sandboxPath.Replace('\\', '_').Replace(':', '_').Trim('_');
        return string.IsNullOrEmpty(name) ? "mapped" : name;
    }
}
