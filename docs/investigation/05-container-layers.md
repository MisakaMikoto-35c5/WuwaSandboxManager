# 05 · 容器层与 wcifs / Container Layers and wcifs

## 1. 概述 / Overview

**中文**：Windows 容器的文件系统不是单一镜像，而是**多层叠加**：只读基础层 + 只读更新层 + 每个容器
的可写 scratch。叠加由 **wcifs**（Windows Container Isolation File System，`wcifs.sys`）实现，
通过 `\WcifsPort` 与容器运行时通信。Windows Sandbox 的基础 OS 层就以此机制提供。
**English**: A Windows container's filesystem is not a single image but a **multi-layer overlay**:
read-only base layer + read-only update layers + a per-container writable scratch. The overlay is
implemented by **wcifs** (Windows Container Isolation File System, `wcifs.sys`), which communicates
with the container runtime via `\WcifsPort`. Windows Sandbox's base OS layer is provided this way.

## 2. 分层模式 / Layering mode

**中文**：`LayerMetadata.LayeringMode = "Wcifs"`。旧版还有 `UnionFS`。
**English**: `LayerMetadata.LayeringMode = "Wcifs"` (older builds also had `UnionFS`).

## 3. LayerDescriptors 与 OverlayDirectories / Layer descriptors and overlay directories

**中文**：
- `WcifsConfiguration.LayerDescriptors = [{ LayerId, Path:"%SYSTEMDRIVE%" }]`：把某个 LayerId 映射到
  guest 的 `%SYSTEMDRIVE%`（C:）。
- `OverlayDirectories`（如 `Windows\Servicing\Packages`、`Windows\WinSxS`）：这些目录以
  **overlay/写穿透**方式处理，而非完全虚拟化。
**English**:
- `WcifsConfiguration.LayerDescriptors = [{ LayerId, Path:"%SYSTEMDRIVE%" }]` maps a layer id to the
  guest's `%SYSTEMDRIVE%` (C:).
- `OverlayDirectories` (e.g. `Windows\Servicing\Packages`, `Windows\WinSxS`) are handled as
  **overlays / write-through** rather than fully virtualized.

## 4. 层链与 reparse point / Layer chain and reparse points

**中文**：每个层有 `ParentId`（父层）与 `SequenceNumber`。未修改的目录以 **reparse point** 表达，
指向父层，从而避免复制整个 OS。vSMB 共享需 `ReparseBaseLayer: true`。
**English**: Each layer has a `ParentId` (parent) and `SequenceNumber`. Unchanged directories are
expressed as **reparse points** into the parent, avoiding a full OS copy. The vSMB share needs
`ReparseBaseLayer: true`.

## 5. 注册表并集与 hivestack / Registry union and hivestack

**中文**：注册表也分层：层的 `Hives\*_BASE`（符号链接到 `Files\Windows\System32\Config\{SYSTEM,…}`）
+ `Specialization\Windows\{SYSTEM,SOFTWARE,…}`（特化后的 hive）。运行时由 `hivestack` 做并集，
容器拥有自己的可写 hive 副本。
**English**: The registry is layered too: the layer's `Hives\*_BASE` (symlinks to
`Files\Windows\System32\Config\{SYSTEM,…}`) plus `Specialization\Windows\{SYSTEM,SOFTWARE,…}` (the
specialized hives). At runtime `hivestack` unions them and the container gets its own writable copy.

## 6. ReparseBaseLayer 与 vSMB / ReparseBaseLayer and vSMB

**中文**：宿主 `vmusrv`（在 `vmwp.exe` 内）通过 vSMB 把层目录只读共享给 guest；`ReparseBaseLayer`
让 reparse point 在 guest 侧正确解析，`PseudoOplocks`/`PseudoDirnotify` 等优化 SMB 语义。
**English**: The host `vmusrv` (inside `vmwp.exe`) shares the layer directory read-only to the guest
over vSMB; `ReparseBaseLayer` lets reparse points resolve on the guest side, and `PseudoOplocks` /
`PseudoDirnotify` optimize SMB semantics.

## 7. 可写 scratch / Writable scratch

**中文**：每个容器的写入落到 `ContainerStorages\<id>\sandbox.vhdx`（动态 VHDX，4MB 起）——它是
**可写层/用户数据**，不是 OS 镜像。另有 `sandbox.vmgs`（guest 状态）。
**English**: Per-container writes go to `ContainerStorages\<id>\sandbox.vhdx` (dynamic VHDX, from 4MB)
— the **writable layer / user data**, not the OS image. There is also `sandbox.vmgs` (guest state).

## 8. 层工具 / Layer tooling

**中文**：`cmimageworker.exe` 负责层的展开/部署/清理；`vmcompute!CreateLayer`/`ImportLayer`/
`ExportLayer`/`PrepareLayer`/`ActivateLayer`/`GetLayerMountPath` 管理层生命周期；
`container.dll!WcCreateContainer` / `WcCreateDescriptionFromXml` 由容器运行时调用。
**English**: `cmimageworker.exe` expands/deploys/cleans layers; `vmcompute!CreateLayer`/`ImportLayer`/
`ExportLayer`/`PrepareLayer`/`ActivateLayer`/`GetLayerMountPath` manage the layer lifecycle;
`container.dll!WcCreateContainer` / `WcCreateDescriptionFromXml` are invoked by the container runtime.

## 9. 与 Sandbox 的对应 / Mapping to Sandbox

| HCS 字段 Field | 作用 Role (中/EN) |
|---|---|
| `Devices.VirtualSmb.Shares[0].Path` | 只读基础层 / read-only base layer |
| `RunInSilo.SiloBaseOsPath` | silo 基路径 / silo base OS path |
| `RunInSilo.FileSystemLayers[0].Id` | HOST_LAYER_ID `8264f677-…` / host layer id |
| `Devices.Scsi.Attachments["0"].Path` | 可写 scratch / writable scratch |

## 证据 / Evidence

- 注册表 `LayerMetadata`（`LayeringMode`、`WcifsConfiguration.LayerDescriptors`、`OverlayDirectories`）。
- 目录结构 `Layers\<id>\{Files,Hives,Specialization,Bindings,dotnet.bcf}` 与符号链接目标。
- `CmService.dll` 字符串：`hivestack`、`ReparseBaseLayer`、`\WcifsPort`。
- Check Point《Playing in the (Windows) Sandbox》给出的 `WcCreateContainer` → `WciSetupFilter` → `\WcifsPort` 调用栈。
- 相关模块：[02 镜像](02-sandbox-image.md)、[09 网络与 vSMB](09-networking-and-vsmb.md)。
