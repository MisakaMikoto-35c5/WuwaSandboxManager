# 11 · 本项目映射与能力矩阵 / Project Mapping and Capability Matrix

> 对应代码仓库 `WindowsSandboxManager`（.NET 10，DDD 分层）。
> This documents the `WindowsSandboxManager` repository (.NET 10, DDD layering).

## 1. 分层映射 / Layer mapping

| 文档概念 Concept | 本项目实现 Implementation |
|---|---|
| 领域模型 / Domain | `WindowsSandboxManager.Domain`（`SandboxInstance`、`SandboxRecipe`、`SandboxStorageOptions`、`SandboxCheckpoint`、端口） |
| 用例 / Use cases | `WindowsSandboxManager.Application`（`SandboxApplicationService`、后端选择器、checkpoint 用例） |
| gRPC 控制面 / gRPC control plane | `Infrastructure/Grpc`（`sandbox_core.proto`、命名管道通道、`WindowsSandboxServerBackend`） |
| HCS 控制面 / HCS control plane | `Infrastructure/Hcs`（`HcsService`、`HcsOperation`、`HcsSandboxJson`、`HcsHostLayout`、`HcsStorageManager`、`HcsCheckpointManager`、`HcsSandboxBackend`） |
| 连接 / Connection | `Infrastructure/Connectors`（`WindowsSandboxSessionConnector`、`RdpBaseNamedPipeConnector`） |
| 序列化 / Serialization | `Infrastructure/Serialization`（`WsbRecipeXmlSerializer`，`WsbRecipe`/`.wsb`） |
| 环境/提权 / Environment & elevation | `Infrastructure/Environment`、`Infrastructure/Elevation` |
| 表现层 / Presentation | `WindowsSandboxManager.App`（WinUI3）、`WindowsSandboxManager.Cli`（`wsm`） |

## 2. 后端能力矩阵 / Backend capability matrix

| 能力 Capability | HCS 后端 HCS backend | gRPC 后端 gRPC backend |
|---|---|---|
| 多实例 / multiple instances | ✅（无许可门槛 / no license gate） | ⚠️ 受 `CO_E_APPSINGLEUSE` |
| CPU 核数 / CPU cores | ✅ `Processor.Count` | ✖ |
| 自定义用户数据位置 / custom storage location | ✅ `Scsi.Attachments[].Path` | ✖（仅大小 / size only） |
| 自定义基础镜像层 / custom base image | ✅ `VirtualSmb`/`RunInSilo` | ✖ |
| 检查点 / checkpoints | ✅ `HcsSaveComputeSystem` | ✖ |
| 运行期热映射目录 / runtime folder mapping | ✖（创建时 / create-time） | ✅ `MapFolder` |
| in-guest 进程执行 / process execution | ✖ | ✅ `CreateProcess` |
| 获取 IP / get IP | ✖（HNS） | ✅ `GetIpAddress` |
| 提权要求 / elevation | 需要 / required | 不需要 / not required |

## 3. HCS JSON 字段映射 / HCS JSON field mapping

| 概念 Concept | 字段 Field | 代码 Code |
|---|---|---|
| 基础镜像层 / base layer | `VirtualSmb.Shares[0].Path`；`RunInSilo.SiloBaseOsPath` | `HcsHostLayout.ResolveBaseLayerPath()` → `HcsSandboxJson.Build` |
| 用户数据盘 / user-data disk | `Scsi.primary.Attachments["0"].Path` | `HcsStorageManager.Provision` |
| guest 状态 / guest state | `GuestState.GuestStateFilePath` | `HcsStorageManager.Provision` |
| CPU / memory | `ComputeTopology.Processor.Count` / `Memory.SizeInMB` | `SandboxRecipe.Cpu` / `SandboxRecipe.Memory` |
| vGPU | `EnhancedModeVideo.ConnectionOptions` | `SandboxRecipe.VirtualGpu` |
| 网络 / networking | `NetworkAdapters["0"].EndpointId` | `HcsSandboxDefinition.NetworkEndpointId` |
| 目录映射 / folders | `VirtualSmb.Shares[]` | `SandboxRecipe.MappedFolders` |
| 检查点保存 / checkpoint save | `HcsSaveComputeSystem` + `SaveOptions` | `HcsCheckpointManager.Save` |
| 检查点恢复 / checkpoint restore | `RestoreState.SaveStateFilePath`/`TemplateSystemId` | `HcsCheckpointManager.Restore` |
| HvSocket 服务 / services | `HvSocket.HvSocketConfig.ServiceTable` | `HcsSandboxJson`（LSM/NLM） |

## 4. 存储与检查点映射 / Storage & checkpoint mapping

| 项 Item | 位置 Location | 代码 Code |
|---|---|---|
| 层注册 / layer registry | `HKLM\...\Containers\CmService\Layers\<id>` | `HcsHostLayout.ListLayers` |
| 用户数据 / user data | `ContainerStorages\<id>\sandbox.vhdx` | `SandboxStorageOptions.StoragePath` |
| guest 状态 / guest state | `ContainerStorages\<id>\sandbox.vmgs` | `HcsStorageManager` |
| 检查点 / checkpoint | `Snapshots\<id>\SnapshotSavedState.vmrs` | `HcsCheckpointManager` |

## 5. 关键约束 / Key constraints

**中文**：
- **HCS 需要提权**（`0x8037011B`）；引擎在提权时自动选 HCS，否则回退 gRPC（`ISandboxBackend.IsAvailable`）。
- 裸 HCS **冷启动**需要已存在基础层与用户数据盘；`HcsStorageManager` 从既有 `ContainerStorages` 播种。
  模板（zygote）可选，只影响启动速度。
- 运行期热映射与进程执行由 gRPC 后端承担。
**English**:
- **HCS requires elevation** (`0x8037011B`); the engine auto-selects HCS when elevated, otherwise falls
  back to gRPC (`ISandboxBackend.IsAvailable`).
- Raw-HCS **cold start** needs an existing base layer and user-data disk; `HcsStorageManager` seeds
  from an existing `ContainerStorages`. The template (zygote) is optional and only affects speed.
- Runtime folder mapping and process execution are handled by the gRPC backend.

## 6. 常用命令 / Common commands

```powershell
wsm env                 # 后端/提权/能力 / backend, elevation, capabilities
wsm layers              # 层/存储/快照布局 + 基础镜像 / layout + base image
wsm elevate             # 提权重启以启用 HCS / relaunch elevated
wsm create --cpu 8 --memory 8192 --storage-path E:\sbx --networking enable
wsm start <id> ; wsm stop <id>
wsm checkpoint save <id> snap1 --template
wsm checkpoint list <id> ; wsm checkpoint restore <id> <checkpointId>
wsm hcs                 # 列出 HCS compute system（需提权）/ list HCS systems (elevated)
```

## 证据 / Evidence

- 仓库源码 `src/WindowsSandboxManager.{Domain,Application,Infrastructure,Cli,App}`。
- 单元测试 `tests/WindowsSandboxManager.Tests`（含 HCS JSON、后端选择、序列化）。
- 相关模块：[02 镜像](02-sandbox-image.md)、[04 HCS](04-hcs.md)、[07 存储与检查点](07-storage-and-checkpoints.md)、[10 Schema](10-hcs-schema-and-native-api.md)。
