# 04 · Host Compute Service（HCS）/ 主机计算服务

## 1. 概述 / Overview

**中文**：HCS 是 Windows 上创建与控制 **VM 和容器**的底层 API。一个「compute system」代表一个
VM 或容器；所有操作都针对 compute system 的句柄。compute system 是**临时的**：停止后句柄失效，
需要重新创建。
**English**: HCS is the low-level Windows API for creating and controlling **VMs and containers**. A
"compute system" represents a VM or container; all operations act on its handle. Compute systems are
**ephemeral**: once stopped, handles are invalid and the system must be recreated.

## 2. 组件 / Components

| Item | 中文 | English |
|---|---|---|
| `vmcompute.exe` | HCS 服务（编排所有 compute system） | HCS service (orchestrates all compute systems) |
| `vmcompute.dll` | 服务端实现 + 层/镜像工具 | Server implementation + layer/image helpers |
| `computecore.dll` | 客户端库（operation 模型） | Client library (operation model) |
| `HcsService` / `vmwp.exe` | 每个 compute system 的工作进程 | Per-system worker process |
| HNS (`hns`) | 网络（Endpoint/Network/Policy） | Networking (endpoint/network/policy) |

## 3. 生命周期 / Lifecycle

**中文**：客户端负责在调用 HCS 前准备好资源（VHD、网络、HvSocket）；HCS **不会**代为创建资源。
典型流程：准备 JSON → `HcsCreateComputeSystem` → `HcsStartComputeSystem` → 交互
（`HcsCreateProcess`）→ `HcsShutdownComputeSystem`/`HcsTerminateComputeSystem`。
**English**: The client must prepare resources (VHD, networking, HvSocket) before calling HCS; HCS
**does not** create them. Typical flow: build JSON → `HcsCreateComputeSystem` →
`HcsStartComputeSystem` → interact (`HcsCreateProcess`) → `HcsShutdownComputeSystem` /
`HcsTerminateComputeSystem`.

## 4. 操作模型 / Operation model

**中文**：`computecore.dll` 使用**异步操作**：`HcsCreateOperation` 创建 `HCS_OPERATION`；
长任务返回 `HCS_E_OPERATION_PENDING (0xC0370103)`，随后用 `HcsWaitForOperationResult` 等待并取回
结果文档（**必须用 `LocalFree` 释放**）；完成后 `HcsCloseOperation`。
**English**: `computecore.dll` uses **asynchronous operations**: `HcsCreateOperation` creates an
`HCS_OPERATION`; long calls return `HCS_E_OPERATION_PENDING (0xC0370103)`, then
`HcsWaitForOperationResult` waits and returns the result document (**must be freed with
`LocalFree`**); finally `HcsCloseOperation`.

## 5. Schema 版本 / Schema versions

**中文**：HCS 配置是 JSON，`SchemaVersion { Major, Minor }`。Windows Sandbox 使用 2.x（示例为 2.1）。
较新字段随 Minor 增加（如 `Attachment.SupportCompressedVolumes` 2.3、`SupportEncryptedFiles` 2.6、
`Processor.Count` 2.4）。
**English**: HCS configuration is JSON with `SchemaVersion { Major, Minor }`. Windows Sandbox uses
2.x (observed 2.1). Newer fields appear with higher minors (e.g. `Attachment.SupportCompressedVolumes`
2.3, `SupportEncryptedFiles` 2.6, `Processor.Count` 2.4).

## 6. 提权 / Elevation

**中文**：直接调用 HCS 需要 **Administrators 或 Hyper-V Administrators**；否则返回
`0x8037011B`。Windows Sandbox 之所以能非提权使用，是因为 `CmService` 服务以 SYSTEM 代劳。
**English**: Calling HCS directly requires **Administrators or Hyper-V Administrators**; otherwise it
returns `0x8037011B`. Windows Sandbox works unelevated because the `CmService` service performs HCS
work as SYSTEM.

## 7. API 表面 / API surface

| 来源 Source | 函数 Functions |
|---|---|
| `computecore.dll` | `HcsCreateOperation`, `HcsWaitForOperationResult`, `HcsCloseOperation`, `HcsCreateComputeSystem`, `HcsOpenComputeSystem`, `HcsCloseComputeSystem`, `HcsStartComputeSystem`, `HcsShutDownComputeSystem`, `HcsTerminateComputeSystem`, `HcsSaveComputeSystem`, `HcsGetComputeSystemProperties`, `HcsEnumerateComputeSystems`, `HcsCreateProcess`, `HcsCreateEmptyGuestStateFile`, `HcsCreateEmptyRuntimeStateFile`, `HcsGrantVmAccess` |
| `vmcompute.dll` | `CreateBaseImageVHD`, `CreateBaseImageVHDWithFolders`, `CreateDifferencingVHD`, `CreateLayer`, `CreateSandboxLayer`, `ImportLayer`, `ExportLayer`, `MountVhdForSetup`, `GetLayerMountPath`, `PrepareLayer`, `ActivateLayer` |

> 完整签名见 [10 HCS Schema 与原生 API](10-hcs-schema-and-native-api.md)。

## 8. 与 Sandbox/CmService 的关系 / Relation to Sandbox & CmService

**中文**：`CmService` 生成 HCS JSON 并经 RPC（`vmcompute!HcsRpc_CreateSystem`）调用 HCS；
`vmcompute` 再调用容器运行时（`container.dll`）做 wcifs 拼装。HCS 的 `RunInSilo` 是触发点。
**English**: `CmService` builds the HCS JSON and calls HCS via RPC
(`vmcompute!HcsRpc_CreateSystem`); `vmcompute` then invokes the container runtime (`container.dll`)
to perform the wcifs assembly. `RunInSilo` is the trigger.

## 9. 常见 HRESULT / Common HRESULTs

| HRESULT | 含义 Meaning |
|---|---|
| `0xC0370103` | `HCS_E_OPERATION_PENDING`（异步进行中 / async pending） |
| `0xC0370104` | `HCS_E_OPERATION_TIMEOUT` |
| `0x80370102` | Hyper-V 未安装 / hypervisor not available |
| `0x8037010F` | compute system 已存在 / already exists |
| `0x80370110` | 未找到 / not found |
| `0x80370117` | 已终止 / terminated |
| `0x8037011B` | 需要提权 / requires elevation |
| `0x800401F6` | `CO_E_APPSINGLEUSE`（Sandbox 单实例策略 / Sandbox single-instance policy） |

## 证据 / Evidence

- `computecore.dll`/`vmcompute.dll` 导出表（PE 解析）。
- 本机非提权实测 `HcsEnumerateComputeSystems` → `0x8037011B`（"只有管理员或 Hyper-V 管理员…"）。
- `hcsshim` 的 `internal/computecore/*`（`HcsSaveComputeSystem`、`HcsWaitForOperationResult`、`HcsCreateOperation` 等签名）。
- Microsoft Learn：HCS Overview / API Reference / JSON Schema Reference / `HcsWaitForOperationResult`。
- 相关模块：[05 容器层](05-container-layers.md)、[10 HCS Schema](10-hcs-schema-and-native-api.md)、[07 存储与检查点](07-storage-and-checkpoints.md)。
