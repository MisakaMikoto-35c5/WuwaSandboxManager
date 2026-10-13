# 07 · 存储、检查点与合子 / Storage, Checkpoints and Zygote

## 1. 存储布局 / Storage layout

**中文**：`C:\ProgramData\Microsoft\Windows\Containers\ContainerStorages\<id>\`：
`sandbox.vhdx`（可写/用户数据盘）+ `sandbox.vmgs`（guest 状态）+ `Bindings\config.json`。
`<id>` 即层的 `StorageId`（或沙箱自己的存储 id）。
**English**: `ContainerStorages\<id>\` holds `sandbox.vhdx` (writable/user-data disk) +
`sandbox.vmgs` (guest state) + `Bindings\config.json`. `<id>` is a layer's `StorageId` (or the
sandbox's own storage id).

**中文**：实测三份存储：`0f0cd630-…`（vhdx 4MB，空）、`b8c0e347-…`（88MB）、
`18be0d40-…`（917MB，等于层 `e01ff4dc` 的 `StorageId`）。
**English**: Three storages observed: `0f0cd630-…` (4MB, empty), `b8c0e347-…` (88MB) and
`18be0d40-…` (917MB, equal to layer `e01ff4dc`'s `StorageId`).

## 1b. 存储链：层存储 → 基础镜像 → 沙箱 / Storage chain: layer storage → base image → sandbox

**中文**：`ContainerStorages` 内的三层结构（实测 `PersistedStorages` + VHDX 父定位器）：
**层存储**（`ParentType=2`，父为层 id，`Bindings\config.json` 仅 `{"Version":1}`；即上面的 917MB
`18be0d40`）→ **基础镜像**（`ParentType=1`，父为层存储，带 VM 的 `Bindings` 配置；即 88MB /
本机 178MB 的 `f8299c70`，已完成后宾首次启动/特化）→ **沙箱盘**（4MB，父为基础镜像）。
平台**从不**让沙箱直接挂在层存储上；它先物化基础镜像，再从基础镜像克隆每个沙箱。WHY：特化
（shell / 开始菜单 / XAML-AppX 包注册）写在基础镜像里，直接挂层存储会启动**未特化**的来宾
——会话能起、但 `explorer.exe` 挂起、开始菜单打不开。`Wsm.Hcs.Storage` 的 provisioner 因此
优先克隆**donor 的直接父盘**（基础镜像），仅在没有基础镜像时回退到链根。
**English**: Three levels inside `ContainerStorages` (observed via `PersistedStorages` + the VHDX
parent locator): **layer storage** (`ParentType=2`, parent = layer id, `Bindings\config.json` is
just `{"Version":1}`; the 917MB `18be0d40`) → **base image** (`ParentType=1`, parent = layer
storage, carries the VM `Bindings` config; the 88MB / local 178MB `f8299c70`, with the guest's
first-boot/specialization completed) → **sandbox disk** (4MB, parent = base image). The platform
**never** boots a sandbox straight on a layer storage; it first materializes a base image and clones
every sandbox from it. WHY: the specialization (shell / Start menu / XAML-AppX package registration)
lives in the base image, so chaining to the layer storage boots an **unspecialized** guest — the
session comes up, but `explorer.exe` hangs and the Start menu never opens. The `Wsm.Hcs.Storage`
provisioner therefore prefers a donor's **immediate parent** (the base image), falling back to the
chain root only when no base image exists.

## 2. 用户数据盘 / User-data disk

**中文**：`sandbox.vhdx` 是动态 VHDX（4MB 起），承载容器写入与用户数据；`sandbox.vmgs` 是持久 guest
状态（4MB）。HCS 通过 `Devices.Scsi.primary.Attachments["0"].Path` 挂载 vhdx，通过
`GuestState.GuestStateFilePath` 关联 vmgs。
**English**: `sandbox.vhdx` is a dynamic VHDX (from 4MB) holding container writes and user data;
`sandbox.vmgs` is the persistent guest state (4MB). HCS mounts the vhdx via
`Devices.Scsi.primary.Attachments["0"].Path` and associates the vmgs via
`GuestState.GuestStateFilePath`.

## 3. 自定义存储位置 / Custom storage location

**中文**：**大小可配**（UDK `VMOptions.MaxStorageSizeMB`；`.wsb`/gRPC 不暴露）。
**位置仅 HCS 直连可改**：`Scsi.Attachments[].Path`，或 `CmsCreateStorage` / `CmsMapVirtualDiskToContainer`。
`SandboxCommon.StorageConfiguration{StoragePath,DiskSizeInMB,DiskEncryption}` 存在但服务器**不映射**它，
`VMStorage` 仅暴露 `Id`。
**English**: **Size** is configurable (UDK `VMOptions.MaxStorageSizeMB`; not exposed via `.wsb`/gRPC).
**Location is HCS-only**: `Scsi.Attachments[].Path`, or `CmsCreateStorage` /
`CmsMapVirtualDiskToContainer`. `SandboxCommon.StorageConfiguration` exists but the server does **not**
map it; `VMStorage` only exposes `Id`.

## 4. 检查点 / Checkpoints

**中文**：`C:\ProgramData\Microsoft\Windows\Containers\Snapshots\<id>\SnapshotSavedState.vmrs`（实测约
371MB = VM 运行时状态）。由 `HcsSaveComputeSystem` + `SaveOptions` 产生。
**English**: `Snapshots\<id>\SnapshotSavedState.vmrs` (observed ~371MB = saved VM runtime state),
produced by `HcsSaveComputeSystem` + `SaveOptions`.

| SaveType | 中文 | English |
|---|---|---|
| `ToFile` | 保存内存+设备状态（可恢复的全状态检查点） | Saves memory + device state (resumable full-state checkpoint) |
| `AsTemplate` | 只保存设备状态，系统保持可被克隆 | Saves device state only; system stays cloneable |

`SaveOptions`：`{ "SaveType":"ToFile"|"AsTemplate", "SaveStateFilePath":"<path>" }`。
`CmService.dll` 字符串：`DeploymentManager_CreateOrOpenSnapshot`、`ResourceOperation_MaterializeSnapshot`、
`SnapshotOperation`、`SnapshotFolder`、`DisableSnapshot`、`OptimizeSnapshotQuiesceTimeoutMilliseconds`、
`PrepareForSnapshotTimeoutMilliseconds`。

## 5. 恢复 / Restore

**中文**：`VirtualMachine.RestoreState`：`{ "SaveStateFilePath":…, "TemplateSystemId":… }`。
用 `SaveStateFilePath` 恢复全状态；用 `TemplateSystemId` 从模板克隆。
**English**: `VirtualMachine.RestoreState = { "SaveStateFilePath":…, "TemplateSystemId":… }`. Use
`SaveStateFilePath` to resume a full state, or `TemplateSystemId` to clone from a template.

## 6. 合子（Zygote）/ Template

**中文**：`CreateZygote`/`CmZygote`/`DeploymentManager_CreateZygote`/`ResourceOperation_MaterializeZygote`/
`CloneTemplate`/`GetCloneTemplate`/`RuntimeTemplateId`/`IsRuntimeTemplate` —— 一个**预启动的模板 VM**，
克隆由此 fork，使沙箱**秒开**；`RebootSupport` 用 `AsTemplate` 走这条路径（`new ManagedWindowsVM(options, _vm.Storage)`）。
**English**: A **pre-booted template VM** (zygote) that clones fork from, enabling **instant** sandbox
startup; `RebootSupport` uses the `AsTemplate` path (`new ManagedWindowsVM(options, _vm.Storage)`).

## 7. 持久化 / Persistence

**中文**：`PersistenceSupport` 对应 `PersistedStorages` / `Container Manager Persisted Storage` 与
`CmsCreatePersistedStorage` 等，使沙箱内容在会话间保留。
**English**: `PersistenceSupport` maps to `PersistedStorages` / `Container Manager Persisted Storage`
and `CmsCreatePersistedStorage`, keeping sandbox content across sessions.

## 8. 生命周期与存储 / Lifecycle and storage

**中文**：`SandboxVM.Reboot()` 复用同一 `_vm.Storage` 重新构造 `ManagedWindowsVM`（快速重启），
说明存储与 VM 生命周期解耦。
**English**: `SandboxVM.Reboot()` reuses the same `_vm.Storage` to construct a new `ManagedWindowsVM`
(fast reboot), showing storage is decoupled from the VM lifecycle.

## 证据 / Evidence

- 目录与文件大小：`ContainerStorages\*\sandbox.vhdx|vmgs`、`Snapshots\*\SnapshotSavedState.vmrs`。
- 注册表 `Layers\<id>.StorageId` 与 `ContainerStorages` 对应关系。
- IL 反编译 `SandboxCommon.Config.StorageConfiguration`、`SandboxServer.VM.SandboxVM`（Reboot/Storage）。
- `CmService.dll` 字符串（Snapshot/Zygote/Persisted）。
- HCS schema `SaveOptions`/`RestoreState`/`GuestState`（见 [10](10-hcs-schema-and-native-api.md)）。
- 相关模块：[02 镜像](02-sandbox-image.md)、[04 HCS](04-hcs.md)、[11 项目映射](11-project-mapping.md)。
