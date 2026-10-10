# 02 · Sandbox 系统镜像 / Sandbox System Image

## 1. 核心结论 / Key conclusion

**中文**：Windows Sandbox 的「系统镜像」**不是一块可启动的 VHD**，而是一个 **wcifs 容器层目录**
（文件 + 注册表 hive + 特化状态）。HCS 只是**按路径引用**它；真正把层拼装成 guest 可见的 `C:` 的
是容器运行时（`container.dll` + `wcifs`），由 HCS JSON 的 `RunInSilo` 触发。
**English**: The Windows Sandbox "system image" is **not a bootable VHD** but a **wcifs container
layer directory** (files + registry hives + specialization state). HCS merely **references it by
path**; the container runtime (`container.dll` + `wcifs`) assembles the layer into the guest-visible
`C:`, triggered by `RunInSilo` in the HCS JSON.

## 2. 磁盘布局 / On-disk layout

`C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>\`：

| Item | 中文 | English |
|---|---|---|
| `Files\` | 完整 Windows 目录树（`Windows`/`Program Files`/`Users`/`EFI`…），顶层多为 reparse point | Full Windows tree; top-level entries are mostly reparse points |
| `Hives\*_BASE` | `SYSTEM_BASE`/`SOFTWARE_BASE`/`SAM_BASE`/`SECURITY_BASE`/`DEFAULTUSER_BASE`，**符号链接**指向 `Files\Windows\System32\Config\{SYSTEM,SOFTWARE,…}` | Symlinks to the OS hives under `Files\Windows\System32\Config\` |
| `Specialization\` | `Windows\{SYSTEM,SOFTWARE,…}`（hive）+ `config.json` | Post-sysprep hives + `config.json` metadata |
| `Bindings\config.json` | `{"Version":1}` | Filesystem namespace bindings |
| `dotnet.bcf` | 二进制配置 | .NET layer config |

**中文**：本机 `Files` 顶层（实测）：`EFI`、`inetpub`、`PerfLogs`、`Program Files`、
`Program Files (x86)`、`ProgramData`、`Recovery`、`sources`、`Users`、`Windows`、`License.txt`。
**English**: Observed top-level entries of `Files`: `EFI`, `inetpub`, `PerfLogs`, `Program Files`,
`Program Files (x86)`, `ProgramData`, `Recovery`, `sources`, `Users`, `Windows`, `License.txt`.

## 3. `Files` 与 reparse point / `Files` and reparse points

**中文**：层链通过 **reparse point** 表达「未修改目录指向父层」。vSMB 共享必须设置
`ReparseBaseLayer: true` 才能正确解析。
**English**: The layer chain expresses "unchanged directories point at the parent layer" via
**reparse points**. The vSMB share must set `ReparseBaseLayer: true` to resolve them.

## 4. `Hives`：注册表基础 hive / Registry base hives

**中文**：`Hives\SYSTEM_BASE` 是**符号链接**，实测目标为
`...\Layers\<id>\Files\Windows\System32\Config\SYSTEM`；`SOFTWARE_BASE` 同理。也就是说层的注册表
hive 就是 OS 自带的 hive，`Hives\` 目录是给容器运行时（`hivestack`）用的入口。
**English**: `Hives\SYSTEM_BASE` is a **symlink** to
`...\Layers\<id>\Files\Windows\System32\Config\SYSTEM` (same for `SOFTWARE_BASE`). The layer's
registry hives are the OS's own hives; the `Hives\` folder is the entry point for the container
runtime (`hivestack`).

## 5. `Specialization`：特化状态 / Specialization state

**中文**：`Specialization\config.json`（实测）：
```json
{ "Id":"e01ff4dc-…", "Languages":["en-US","zh-CN"], "SpeechLanguages":["zh-Hans-CN"],
  "HotPatchingEnabled":true, "DotNetIsConfigured":true, "EdgeIsInstalled":true,
  "BootFeatureConfigurationsHash":"d9153715…", "EdgeVersion":"151.0.4129.86", "EdgeUpdateVersion":"1.3.275.13" }
```
并含特化后的 `SYSTEM`(≈9.9MB)/`SOFTWARE`(≈69MB) hive 及事务日志。
**English**: `Specialization\config.json` records what has been specialized (languages, .NET, Edge,
hot-patching, boot-feature hash), alongside the specialized `SYSTEM` (~9.9MB) / `SOFTWARE` (~69MB)
hives and their transaction logs.

## 6. 注册表层注册 / Layer registration

`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService\Layers\<id>`：

| Value | 中文 | English |
|---|---|---|
| `Path` | 层目录（可指向自定义位置） | Layer directory (can point anywhere) |
| `ParentType` / `ParentId` | 父层链（每次 Windows 更新新增一层） | Parent chain (a new layer per update) |
| `OSVersion` | 层对应 OS 版本 | OS version of the layer |
| `StorageId` | 关联的 `ContainerStorages\<id>`（可写 scratch） | Associated writable scratch |
| `SequenceNumber` | 序号（越大越新） | Sequence (higher = newer) |
| `LayerMetadata` | 分层元数据（见下） | Layering metadata (below) |

`LayerMetadata`（实测）：
```json
{ "LayeringMode":"Wcifs",
  "WcifsConfiguration":{"LayerDescriptors":[{"LayerId":"6af57529-…","Path":"%SYSTEMDRIVE%"}]},
  "OverlayDirectories":[{"RelativePath":"Windows\\Servicing\\Packages"},
                        {"RelativePath":"Windows\\WinSxS"}],
  "CreateZygote":true, "SnapshotActions":1, "FirstBootActions":1,
  "SysprepActions":1, "SpecializationFlags":31, "MapEdge":true, "EncryptedFileSystemSupport":true }
```

## 7. `BaseImages` / Base image directory

**中文**：`...\Containers\BaseImages\` 在本机**为空**；不存在独立的 OS VHDX。`CmService.dll` 字符串含
`BaseImages`/`BaseImageIndex`/`DeploymentManager_CreateOrOpenBaseImage`，说明「基础镜像」在概念上存在，
但当前构建里实际使用的是 `Layers\` 层链（由 vSMB 提供）。
**English**: `...\Containers\BaseImages\` is **empty** on this machine and there is no standalone OS
VHDX. `CmService.dll` contains `BaseImages`/`BaseImageIndex`/`DeploymentManager_CreateOrOpenBaseImage`,
so the concept exists, but the current build effectively uses the `Layers\` chain (served over vSMB).

## 8. HCS 如何消费镜像 / How HCS consumes the image

**中文**：
1. **vSMB 只读**：`VirtualMachine.Devices.VirtualSmb.Shares[0] = { Name:"os", Path:<Layer>\Files, ReadOnly:true, ReparseBaseLayer:true }`。
2. **Silo 基路径**：`RunInSilo.SiloBaseOsPath = <Layer>\Files`；`RunInSilo.FileSystemLayers[0] = { Id:"8264f677-40b0-4ca5-bf9a-944ac2da8087", Path:"C:\\", PathType:"AbsolutePath" }`。
3. **引导**：`Chipset.Uefi.BootThis = { DeviceType:"VmbFs", DevicePath:"\\EFI\\Microsoft\\Boot\\bootmgfw.efi" }`。
4. **wcifs 拼装**：`RunInSilo` → `vmcompute` → `container!WcCreateContainer` → `wc_storage!WciSetupFilter` → `FilterSendMessage(\WcifsPort)`。
**English**: (1) read-only vSMB share of the layer; (2) `RunInSilo` base-OS path and host layer; (3)
UEFI VmbFs boot from the vSMB-provided OS; (4) `RunInSilo` makes `vmcompute` invoke the container
runtime which configures the `wcifs` filter via `\WcifsPort`.

## 9. 自定义镜像位置 / Custom image location

**中文**：**允许（仅 HCS 层）**。把 `VirtualSmb.Shares[].Path` / `RunInSilo.SiloBaseOsPath` /
`Layer.Path` 指向任意绝对路径即可；原生支持 `vmcompute!CreateBaseImageVHD`、`CreateLayer`、
`ImportLayer`、`MountVhdForSetup`、`GetLayerMountPath`。但 Sandbox 自带栈（`CmService`/`VMOptions`）
硬编码 `ProgramData` 且不暴露该字段。
**English**: **Yes, at the HCS layer only.** Point `VirtualSmb.Shares[].Path`,
`RunInSilo.SiloBaseOsPath` or `Layer.Path` at any absolute path; native helpers include
`vmcompute!CreateBaseImageVHD`, `CreateLayer`, `ImportLayer`, `MountVhdForSetup`, `GetLayerMountPath`.
The Sandbox stack itself hardcodes `ProgramData` and does not expose this.

## 证据 / Evidence

- 目录/文件/大小/符号链接：`Get-ChildItem`、`fsutil reparsepoint query`（`SYSTEM_BASE` → `Files\Windows\System32\Config\SYSTEM`）。
- 注册表：`HKLM\...\Containers\CmService\Layers\<id>` 的 `Path/ParentId/StorageId/LayerMetadata`。
- `Specialization\config.json` 内容。
- `CmService.dll` 字符串：`BaseImages`、`Hives`、`hivestack`、`ReparseBaseLayer`、`\WcifsPort`、`CreateZygote`。
- HCS 字段：`VirtualSmb`/`RunInSilo`/`Chipset.Uefi`（见 [10 HCS Schema](10-hcs-schema-and-native-api.md)）。
- 相关模块：[05 容器层](05-container-layers.md)、[09 网络与 vSMB](09-networking-and-vsmb.md)。
