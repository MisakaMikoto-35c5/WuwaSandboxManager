# 10 · HCS JSON Schema 与原生 API 参考 / HCS JSON Schema and Native API Reference

> 结构定义来自 `hcsshim/internal/hcs/schema2`（HCS Swagger 生成，2.x）与 Microsoft Learn。
> Struct definitions come from `hcsshim/internal/hcs/schema2` (HCS Swagger-generated, 2.x) and
> Microsoft Learn.

## 1. 顶层 / Top level

```
ComputeSystem {
  Owner                             : string
  SchemaVersion                     : { Major, Minor }
  HostingSystemId                   : string
  HostedSystem                      : object
  Container                         : Container
  VirtualMachine                    : VirtualMachine
  ShouldTerminateOnLastHandleClosed : bool
}
```

## 2. VirtualMachine

```
VirtualMachine {
  Version           : Version
  StopOnReset       : bool
  Chipset           : Chipset
  ComputeTopology   : Topology
  Devices           : Devices
  GuestState        : GuestState
  RestoreState      : RestoreState
  RegistryChanges   : RegistryChanges
  StorageQoS        : StorageQoS
  DebugOptions      : DebugOptions
  GuestConnection   : GuestConnection
  SecuritySettings  : SecuritySettings
  ResourcePartitionId : string
}
```

**中文**：Windows Sandbox 另在 `VirtualMachine` 下使用非标准扩展 `RunInSilo`。
**English**: Windows Sandbox additionally uses a non-standard `RunInSilo` extension under
`VirtualMachine`.

```
Chipset { Uefi: { ApplySecureBootTemplate, SecureBootTemplateId, BootThis: UefiBootEntry } , ... }
UefiBootEntry { DeviceType, DevicePath, ... }   // Sandbox: VmbFs / \EFI\Microsoft\Boot\bootmgfw.efi
Topology { Memory: VirtualMachineMemory, Processor: VirtualMachineProcessor, Numa }
VirtualMachineMemory { SizeInMB, Backing, BackingPageSize, EnablePrivateCompressionStore, ... }
VirtualMachineProcessor { Count, Maximum, Weight }   // Count = vCPU 数
```

## 3. Devices

```
Devices {
  ComPorts, Scsi, NetworkAdapters, VideoMonitor, Keyboard, Mouse,
  HvSocket, EnhancedModeVideo, GuestCrashReporting, VirtualSmb,
  Plan9, Battery, FlexibleIov, SharedMemory, VirtualPci
}
Scsi { Attachments: map<string, Attachment> }
Attachment {
  Type, Path, IgnoreFlushes, CachingMode, NoWriteHardening,
  DisableExpansionOptimization, IgnoreRelativeLocator,
  CaptureIoAttributionContext, ReadOnly, SupportCompressedVolumes,
  AlwaysAllowSparseFiles, ExtensibleVirtualDiskType
}
VirtualSmb { Shares: VirtualSmbShare[], DirectFileMappingInMB: int64 }
NetworkAdapter { EndpointId, MacAddress, IovSettings }
EnhancedModeVideo { ConnectionOptions: RdpConnectionOptions }
RdpConnectionOptions { AccessSids: string[], NamedPipe: string }
HvSocket2 { HvSocketConfig: { DefaultBindSecurityDescriptor, DefaultConnectSecurityDescriptor, ServiceTable } }
```

**中文**：Sandbox 用法：
`Scsi.primary.Attachments["0"].Path` = `sandbox.vhdx`；
`VirtualSmb.Shares[0]` = `{ Name:"os", Path:<Layer>\Files, Options:{ ReadOnly, ReparseBaseLayer, … } }`；
`EnhancedModeVideo.ConnectionOptions.NamedPipe` = `\\.\pipe\<VMId>`。
**English**: Sandbox usage: `Scsi.primary.Attachments["0"].Path` = `sandbox.vhdx`;
`VirtualSmb.Shares[0]` = the base layer; `EnhancedModeVideo.ConnectionOptions.NamedPipe` = the RDP pipe.

## 4. GuestState / RestoreState / SaveOptions

```
GuestState {
  GuestStateFilePath    : string   // sandbox.vmgs
  GuestStateFileType    : string
  RuntimeStateFilePath  : string
  ForceTransientState   : bool
}
RestoreState {
  SaveStateFilePath     : string   // 全状态恢复 / resume from full state
  TemplateSystemId      : string   // 从模板克隆 / clone from template
}
SaveOptions {
  SaveType              : "ToFile" | "AsTemplate"
  SaveStateFilePath     : string
}
```

## 5. Layer / Storage

```
Layer   { Id, Path, PathType ("AbsolutePath"|"VirtualSmbShareName"), Cache }
Storage { Layers: Layer[], Path, QoS: StorageQoS, PrivilegedContainerRootPath }
```

## 6. 原生 API：`computecore.dll`（operation 模型）/ Native API

```c
HCS_OPERATION HcsCreateOperation(void* context, void* callback);
HRESULT HcsWaitForOperationResult(HCS_OPERATION op, DWORD timeoutMs, PWSTR* resultDocument); // free with LocalFree
void    HcsCloseOperation(HCS_OPERATION op);

HRESULT HcsCreateComputeSystem(PCWSTR id, PCWSTR configuration, HCS_OPERATION op,
                               void* securityDescriptor, HCS_SYSTEM* computeSystem);
HRESULT HcsOpenComputeSystem(PCWSTR id, DWORD requestedAccess, HCS_SYSTEM* computeSystem);
void    HcsCloseComputeSystem(HCS_SYSTEM system);
HRESULT HcsStartComputeSystem(HCS_SYSTEM system, HCS_OPERATION op, PCWSTR options);
HRESULT HcsShutDownComputeSystem(HCS_SYSTEM system, HCS_OPERATION op, PCWSTR options);
HRESULT HcsTerminateComputeSystem(HCS_SYSTEM system, HCS_OPERATION op, PCWSTR options);
HRESULT HcsSaveComputeSystem(HCS_SYSTEM system, HCS_OPERATION op, PCWSTR options);
HRESULT HcsGetComputeSystemProperties(HCS_SYSTEM system, HCS_OPERATION op, PCWSTR propertyQuery);
HRESULT HcsEnumerateComputeSystems(PCWSTR query, HCS_OPERATION op);
HRESULT HcsCreateProcess(HCS_SYSTEM system, PCWSTR processParameters, HCS_OPERATION op,
                         void* securityDescriptor, HCS_PROCESS* process);
HRESULT HcsCreateEmptyGuestStateFile(PCWSTR guestStateFilePath);
HRESULT HcsCreateEmptyRuntimeStateFile(PCWSTR runtimeStateFilePath);
HRESULT HcsGrantVmAccess(PCWSTR vmId, PCWSTR filePath);
```

**中文**：长任务返回 `HCS_E_OPERATION_PENDING (0xC0370103)`，随后必须 `HcsWaitForOperationResult`。
**English**: Long calls return `HCS_E_OPERATION_PENDING (0xC0370103)`; you must then call
`HcsWaitForOperationResult`.

## 7. 原生 API：`vmcompute.dll` / `cmclient.dll`

**中文**：`vmcompute.dll`（层/镜像）：`CreateBaseImageVHD`、`CreateBaseImageVHDWithFolders`、
`CreateDifferencingVHD`、`CreateLayer`、`CreateSandboxLayer`、`ImportLayer`、`ExportLayer`、
`MountVhdForSetup`、`GetLayerMountPath`、`PrepareLayer`、`ActivateLayer`、`DeactivateLayer`、
`DestroyLayer`、`LayerExists`。
**English**: `vmcompute.dll` (layers/images): as listed.

**中文**：`cmclient.dll`（容器/存储，见 [06](06-cmservice-and-cms-api.md)）：`CmsCreateContainer`、
`CmsCreateStorage`、`CmsMapVirtualDiskToContainer`、`CmsCreateProcessInContainer`、
`CmsRegisterForContainerNotifications`、`CmsCreatePersistedStorage` 等。
**English**: `cmclient.dll` (containers/storage, see [06](06-cmservice-and-cms-api.md)): as listed.

## 8. 关键 HRESULT / Key HRESULTs

| HRESULT | 名称 Name | 中文 | English |
|---|---|---|---|
| `0xC0370103` | `HCS_E_OPERATION_PENDING` | 异步进行中 | async pending |
| `0xC0370104` | `HCS_E_OPERATION_TIMEOUT` | 超时 | timeout |
| `0x80370102` | `HCS_E_HYPERV_NOT_INSTALLED` | 无 hypervisor | no hypervisor |
| `0x8037010F` | `HCS_E_SYSTEM_ALREADY_EXISTS` | 已存在 | already exists |
| `0x80370110` | `HCS_E_SYSTEM_NOT_FOUND` | 未找到 | not found |
| `0x80370117` | `HCS_E_SYSTEM_TERMINATED` | 已终止 | terminated |
| `0x8037011B` | `HCS_E_ACCESS_DENIED` | 需要提权 | requires elevation |
| `0x800401F6` | `CO_E_APPSINGLEUSE` | Sandbox 单实例 | Sandbox single instance |

## 9. Windows Sandbox 的 HCS JSON 骨架 / Sandbox HCS JSON skeleton

```json
{
  "Owner": "WindowsSandbox",
  "SchemaVersion": { "Major": 2, "Minor": 1 },
  "VirtualMachine": {
    "StopOnReset": true,
    "Chipset": { "Uefi": { "BootThis": { "DeviceType": "VmbFs",
                 "DevicePath": "\\EFI\\Microsoft\\Boot\\bootmgfw.efi" } } },
    "ComputeTopology": {
      "Memory": { "SizeInMB": 4096, "Backing": "Virtual" },
      "Processor": { "Count": 4 }
    },
    "Devices": {
      "Scsi": { "primary": { "Attachments": { "0": { "Type": "VirtualDisk",
                "Path": "...\\ContainerStorages\\<id>\\sandbox.vhdx" } } } },
      "VirtualSmb": { "Shares": [ { "Name": "os", "Path": "...\\Layers\\<id>\\Files",
                     "Options": { "ReadOnly": true, "ReparseBaseLayer": true } } ] },
      "HvSocket": { "HvSocketConfig": { "ServiceTable": [ { "ServiceId": "F58797F6-…" },
                                                          { "ServiceId": "A715AC94-…" } ] } },
      "EnhancedModeVideo": { "ConnectionOptions": { "NamedPipe": "\\\\.\\pipe\\<id>" } }
    },
    "GuestState": { "GuestStateFilePath": "...\\ContainerStorages\\<id>\\sandbox.vmgs" },
    "RunInSilo": { "SiloBaseOsPath": "...\\Layers\\<id>\\Files",
                   "FileSystemLayers": [ { "Id": "8264f677-40b0-4ca5-bf9a-944ac2da8087",
                                           "Path": "C:\\", "PathType": "AbsolutePath" } ] },
    "LaunchOptions": { "Type": "None" },
    "GuestConnection": {}
  },
  "ShouldTerminateOnLastHandleClosed": true
}
```

## 证据 / Evidence

- `hcsshim/internal/hcs/schema2/*.go`（`compute_system`, `virtual_machine`, `devices`, `scsi`, `attachment`, `virtual_smb`, `guest_state`, `restore_state`, `save_options`, `layer`, `storage`, `topology`, `uefi`, `rdp_connection_options`）。
- `hcsshim/internal/computecore/{computecore.go,zsyscall_windows.go}`（签名、`LocalFree`）。
- Microsoft Learn：HCS Overview / API Reference / JSON Schema Reference / `HcsWaitForOperationResult`（"free with LocalFree"）。
- `computecore.dll`/`vmcompute.dll`/`cmclient.dll` 导出表（PE 解析）。
- Check Point《Playing in the (Windows) Sandbox》的完整 Sandbox JSON。
