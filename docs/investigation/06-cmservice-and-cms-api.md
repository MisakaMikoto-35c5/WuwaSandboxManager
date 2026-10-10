# 06 · CmService 与 `Cms*` 客户端 API / CmService and the `Cms*` Client API

## 1. CmService 职责 / CmService responsibilities

**中文**：`CmService`（Container Manager Service，`CmService.dll`）是 Windows 容器/沙箱的**管理层**：
准备基础镜像与层、挂载卷、生成 HCS JSON（`Container::Manager::Hcs::Details::GenerateCreateComputeSystemJson`），
并经 RPC 调用 `vmcompute!HcsRpc_CreateSystem`。它还负责检查点、合子、持久化等高级操作。
**English**: `CmService` (Container Manager Service, `CmService.dll`) is the **management layer** for
Windows containers/Sandboxes: it prepares base images and layers, mounts volumes, builds the HCS JSON
(`Container::Manager::Hcs::Details::GenerateCreateComputeSystemJson`) and calls
`vmcompute!HcsRpc_CreateSystem` over RPC. It also drives checkpoints, zygotes and persistence.

## 2. `cmimageworker.exe`

**中文**：镜像层工作进程，接收 `expandpbl` / `deploy` / `clean` 等参数，负责层的展开与部署
（注册表 hive 导出/导入：`ORCreateHive`/`ORSaveHive` 等）。
**English**: The image-layer worker, invoked with `expandpbl` / `deploy` / `clean`, handles layer
expansion/deployment (registry hive export/import via `ORCreateHive`/`ORSaveHive`).

## 3. `Cms*` 客户端 API / The `Cms*` client API

**中文**：`cmclient.dll` 导出 `Cms*` 函数，是应用（如 `WindowsSandbox.exe`、WDAG）管理容器的公开面。
**English**: `cmclient.dll` exports the `Cms*` functions — the surface applications (e.g.
`WindowsSandbox.exe`, WDAG) use to manage containers.

| 分类 Category | 函数 Functions |
|---|---|
| 容器 Container | `CmsCreateContainer`, `CmsOpenContainer`, `CmsCloseContainer`, `CmsInitiateShutdownContainer`, `CmsGetContainerId`, `CmsGetContainerType`, `CmsGetContainerOwner`, `CmsGetContainerFriendlyName`, `CmsHasContainerStarted`, `CmsGetContainerSecurityInformation` |
| 存储 Storage | `CmsCreateStorage`, `CmsOpenStorage`, `CmsOpenContainerStorage`, `CmsCloseStorage`, `CmsDeleteStorage`, `CmsGetStorageId`, `CmsResizeStorage`, `CmsQueryInformationStorage` |
| 映射 Mapping | `CmsMapFolderToContainer`, `CmsMapFolderToContainerEx`, `CmsMapFileToContainer`, `CmsMapStorageToContainer`, `CmsMapVirtualDiskToContainer`, `CmsMapNamedPipeToContainer` |
| 持久化 Persistence | `CmsCreatePersistedStorage`, `CmsOpenPersistedStorage`, `CmsClosePersistedStorage`, `CmsDeletePersistedStorage`, `CmsGetPersistedStorageId` |
| 层 Layer | `CmsCreateLayer`, `CmsOpenLayer`, `CmsCloseLayer` |
| 进程 Process | `CmsCreateProcessInContainer`, `CmsOpenProcessInContainer`, `CmsGetProcessId`, `CmsGetProcessExitCode`, `CmsCloseProcess` |
| 网络 Network | `CmsAddHostNetworkToContainer`, `CmsRemoveHostNetworkFromContainer`, `CmsGetNetworkId`, `CmsQueryInformationNetwork` |
| 通知/诊断 Notification/Diag | `CmsRegisterForContainerNotifications`, `CmsQueryContainerMemoryInfo`, `CmsDiagEnumerateContainers`, `CmsGetContainerTelemetryId` |
| 服务 Servicing | `CmsServicingCreateLayer`, `CmsServicingAdjustLayer`, `CmsServicingCreateTransaction`, `CmsServicingCommitTransaction`, … |

**中文**：`WindowsSandbox.exe` 正是通过 `api-ms-win-containers-cmclient-l1-*` 引入这些函数。
**English**: `WindowsSandbox.exe` imports these through `api-ms-win-containers-cmclient-l1-*`.

## 4. 注册表布局 / Registry layout

**中文**：`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Containers\CmService`：
- `\Layers\<id>`：层注册（`Path`/`ParentId`/`StorageId`/`LayerMetadata`…）。
- `\Images`：基础镜像索引（本机为空）。
另：`HKLM\SOFTWARE\Microsoft\Windows Sandbox\{Capabilities,Policy,Persisted}`。
**English**: `...\Containers\CmService`: `\Layers\<id>` (layer registration), `\Images` (base image
index, empty here). Also `HKLM\SOFTWARE\Microsoft\Windows Sandbox\{Capabilities,Policy,Persisted}`.

## 5. 到 vmcompute 的 RPC / RPC to vmcompute

**中文**：`CmService` → `vmcompute!HcsRpc_CreateSystem` → `ComputeService::Management::Details::ConstructVmWorker`
→ `CreateSiloForIsolatedWorkerProcess` → `ConvertJobObjectToContainer` → `container!WcCreateContainer`。
**English**: `CmService` → `vmcompute!HcsRpc_CreateSystem` → `ConstructVmWorker` →
`CreateSiloForIsolatedWorkerProcess` → `ConvertJobObjectToContainer` → `container!WcCreateContainer`.

## 6. 与 `wsb`/gRPC 的关系 / Relation to `wsb` / gRPC

**中文**：`WindowsSandboxServer`（gRPC）内部使用 `ManagedWindowsVM`（`SandboxUDK`）→ 由 `CmService`/HCS
创建容器。因此 gRPC 面与 `Cms*` 面是**同一底层**的两个抽象层级。
**English**: `WindowsSandboxServer` (gRPC) uses `ManagedWindowsVM` (`SandboxUDK`) internally, which
creates the container through `CmService`/HCS. So the gRPC surface and the `Cms*` surface are two
abstraction levels over the **same foundation**.

## 证据 / Evidence

- `cmclient.dll` 导出表（PE 解析，104 个导出，含上表函数）。
- `WindowsSandbox.exe` 导入 `api-ms-win-containers-cmclient-l1-1-0 … l1-5-2`。
- `CmService.dll` 字符串：`GenerateCreateComputeSystemJson`、`BaseImages`、`Snapshots`、`CreateZygote`、`PersistedStorages`。
- Check Point 文章的调用栈与 HCS JSON。
- 相关模块：[04 HCS](04-hcs.md)、[05 容器层](05-container-layers.md)、[07 存储与检查点](07-storage-and-checkpoints.md)。
