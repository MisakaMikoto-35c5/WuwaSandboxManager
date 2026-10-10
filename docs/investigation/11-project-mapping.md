# 11 · 本项目映射与能力矩阵 / Project Mapping and Capability Matrix

> 对应代码仓库 `WindowsSandboxManager`（.NET 10，**ABP 模块化 · 一模块一程序集**）。
> This documents the `WindowsSandboxManager` repository (.NET 10, **ABP modular, one module per assembly**).

> **迁移说明 / Migration note**：早期实现是一个 DDD 分层单体
> `WindowsSandboxManager.{Domain,Application,Infrastructure,Cli,App}`（6 个 csproj），以手写 DI 扩展与
> `ISandboxBackend` 端口为中心。P8b 提交（`92c2a08`）已**整体删除旧栈**并把命名空间根归一为
> `Wsm.*`。下表描述**最终**模块布局，并注明对应旧代码曾经所在之处。
>
> The early implementation was a DDD-layered monolith
> `WindowsSandboxManager.{Domain,Application,Infrastructure,Cli,App}` (6 csproj) built around hand-written DI
> extensions and the `ISandboxBackend` port. Commit P8b (`92c2a08`) **deleted the entire legacy stack** and
> normalised the namespace roots to `Wsm.*`. The tables below describe the **final** module layout and note
> where the corresponding legacy code used to live.

## 1. 分层映射 / Layer mapping

| 文档概念 Concept | 最终模块 Final module | 旧位置（已删除） Legacy location (deleted) |
|---|---|---|
| 领域模型 / Domain | `Wsm.Domain`（`SandboxInstance`、`SandboxRecipe`、`SandboxStorageOptions`、`SandboxCheckpoint`、值对象、领域事件） | `src/WindowsSandboxManager.Domain/` |
| 用例 / Use cases | `Wsm.Application`（`ISandboxManager` / `SandboxManager`） | `Application/SandboxApplicationService` |
| 引擎选择 / Engine selection | `Wsm.Application.EngineSelection`（`ISandboxEngineSelector`） | `Application/Backends/DefaultSandboxBackendSelector` |
| 引擎契约 / Engine contract | `Wsm.Engines.Abstractions`（`ISandboxEngine`、`ISandboxEngineRegistry`、`SandboxEngineKind`、`SandboxEngineCapabilities`） | `Domain/Abstractions/ISandboxBackend` + `SandboxBackendCapabilities` |
| HCS 互操作 / HCS interop | `Wsm.Hcs.Interop`（`IHcsClient`） | `Infrastructure/Hcs/HcsService`、`HcsNative`、`HcsOperation` |
| 主机布局 / Host layout | `Wsm.Hcs.Layout`（`IHcsHostLayout`） | `Infrastructure/Hcs/HcsHostLayout` |
| Sandbox JSON | `Wsm.Hcs.Json.Sandbox`（`ISandboxVmJsonBuilder`） | `Infrastructure/Hcs/HcsSandboxJson` |
| 容器 JSON / Container JSON | `Wsm.Hcs.Json.Container`（`IContainerJsonBuilder`，**预留**） | （新拆 / new, reserved） |
| 存储 / Storage | `Wsm.Hcs.Storage`（`ISandboxStorageProvisioner`） | `Infrastructure/Hcs/HcsStorageManager` |
| 检查点 / Checkpoint | `Wsm.Hcs.Checkpoint`（`ICheckpointService`） | `Infrastructure/Hcs/HcsCheckpointManager` |
| in-guest 进程 / process | `Wsm.Hcs.Process`（`IComputeProcessExecutor`，**预留**） | （新拆 / new, reserved） |
| 网络 / Network | `Wsm.Hcs.Network`（`IHnsNetworkService`，**预留**） | （新拆 / new, reserved） |
| HCS 引擎 / HCS engine | `Wsm.Engine.Hcs`（`HcsSandboxEngine : ISandboxEngine`，默认） | `Infrastructure/Hcs/HcsSandboxBackend` |
| gRPC 引擎 / gRPC engine | `Wsm.Engine.WindowsSandbox`（`WindowsSandboxEngine : ISandboxEngine`） | `Infrastructure/Grpc/WindowsSandboxServerBackend` |
| 容器引擎 / Container engine | `Wsm.Engine.WindowsContainer`（**预留**，`IsAvailable=false`） | （新拆 / new, reserved） |
| gRPC 客户端 / gRPC client | `Wsm.SandboxServer.Client`（`ISandboxServerClient`，`sandbox_core.proto` + 命名管道通道） | `Infrastructure/Grpc/*` |
| 序列化 / Serialization | `Wsm.Recipe.Serialization`（`ISandboxRecipeSerializer`，`.wsb` XML） | `Infrastructure/Serialization/WsbRecipeXmlSerializer` |
| RDPBASE 连接器 / connector | `Wsm.Connection.RdpBase`（`IRdpBaseConnectorFactory`、`IRdpBaseConnector`） | `Infrastructure/Connectors/RdpBaseNamedPipeConnector` |
| 会话启动 / session launch | `Wsm.Connection.Session`（`ISessionLauncher`） | `Infrastructure/Connectors/WindowsSandboxSessionConnector` |
| 传统启动器 / legacy launcher | `Wsm.Connection.Legacy`（`ILegacySandboxLauncher`） | （新拆 / new） |
| 持久化 / Persistence | `Wsm.Persistence`（`ISandboxInstanceRepository`，JSON + `.wsb`） | `Infrastructure/Repositories/*` |
| 环境探测 / Environment | `Wsm.Host.Environment`（`ISandboxEnvironment`） | `Infrastructure/Environment/WindowsSandboxEnvironment` |
| 提权 / Elevation | `Wsm.Host.Elevation`（`IElevationService`） | `Infrastructure/Elevation/*` |
| 多实例策略 / Multi-instance policy | `Wsm.Host.Policy`（`IMultiInstancePolicy`） | `Infrastructure/Environment/SandboxMultiInstancePolicy` |
| 本地化 / Localization | `Wsm.Localization`（`WindowsSandboxManagerResource`，ABP JSON `en`/`zh-Hans`） | （新拆 / new） |
| 表现层 / Presentation | `Wsm.Cli`（`wsm`，`CliModule`）、`Wsm.Ui.WinUI`（WinUI3，`WinUiModule`，`.resw`/`x:Uid`） | `Cli/*`、`App/*` |
| 测试 / Tests | `Wsm.Tests`（`TestModule`，49 个测试） | `tests/WindowsSandboxManager.Tests` |

> 旧 `Infrastructure/DependencyInjection.cs` 与 `Application/DependencyInjection.cs`（手写 DI 扩展）
> 已被 **ABP 约定式 DI**（`ITransientDependency`/`ISingletonDependency` + `[ExposeServices]`）取代，
> 随旧栈一并删除。
>
> The legacy `Infrastructure/DependencyInjection.cs` and `Application/DependencyInjection.cs` (hand-written
> DI extensions) were replaced by **ABP conventional DI** (`ITransientDependency`/`ISingletonDependency` +
> `[ExposeServices]`) and deleted with the old stack.

## 2. 引擎能力矩阵 / Engine capability matrix

| 能力 Capability | HCS（默认） HCS (default) | Windows Sandbox（gRPC） | Windows Container（预留） Reserved |
|---|---|---|---|
| `SandboxEngineKind` | `HcsSandbox = 0` | `WindowsSandbox = 1` | `WindowsContainer = 2` |
| `IsAvailable` | 提权时 / when elevated | 恒真 / always | 恒假 / always `false` |
| 多实例 / multiple instances | ✅（无许可门槛 / no license gate） | ⚠️ 受 `CO_E_APPSINGLEUSE` | 计划 / planned ✅ |
| CPU 核数 / CPU cores | ✅ `Processor.Count` | ✖ | 计划 / planned ✅ |
| 自定义用户数据位置 / custom storage location | ✅ `Scsi.Attachments[].Path` | ✖（仅大小 / size only） | 计划 / planned ✅ |
| 自定义基础镜像层 / custom base image | ✅ `VirtualSmb`/`RunInSilo` | ✖ | 计划 / planned ✅ |
| 检查点 / checkpoints | ✅ `HcsSaveComputeSystem` | ✖ | 计划 / planned ✅ |
| 运行期热映射目录 / runtime folder mapping | ✖（创建时 / create-time） | ✅ `MapFolder` | 计划 / planned ✅ |
| in-guest 进程执行 / process execution | ✖ | ✅ `CreateProcess` | 计划 / planned ✅ |
| 获取 IP / get IP | ✖（HNS） | ✅ `GetIpAddress` | 计划 / planned ✅ |
| 提权要求 / elevation | 需要 / required | 不需要 / not required | 需要 / required |

> 选择器 `ISandboxEngineSelector` **仅考虑 `IsAvailable` 引擎**，按 recipe 能力匹配后按
> `SandboxEngineKind` 升序（HCS 优先）选择。Windows Container 的“计划 ✅”表示其预留空壳声明的
> 目标能力（见 `WindowsContainerEngine.Capabilities`），但 `IsAvailable=false` 使其**永不被选中**。
>
> The selector considers only `IsAvailable` engines, matches recipe capabilities, then orders by ascending
> `SandboxEngineKind` (HCS first). Windows Container's "planned ✅" reflects the target matrix declared by
> its reserved shell; `IsAvailable == false` keeps it from ever being selected.

## 3. HCS JSON 字段映射 / HCS JSON field mapping

| 概念 Concept | 字段 Field | 代码 Code |
|---|---|---|
| 基础镜像层 / base layer | `VirtualSmb.Shares[0].Path`；`RunInSilo.SiloBaseOsPath` | `IHcsHostLayout.ResolveBaseLayerPath()` → `ISandboxVmJsonBuilder` |
| 用户数据盘 / user-data disk | `Scsi.primary.Attachments["0"].Path` | `ISandboxStorageProvisioner.Provision` |
| guest 状态 / guest state | `GuestState.GuestStateFilePath` | `ISandboxStorageProvisioner.Provision` |
| CPU / memory | `ComputeTopology.Processor.Count` / `Memory.SizeInMB` | `SandboxRecipe.Cpu` / `SandboxRecipe.Memory` |
| vGPU | `EnhancedModeVideo.ConnectionOptions` | `SandboxRecipe.VirtualGpu` |
| 网络 / networking | `NetworkAdapters["0"].EndpointId` | `HcsSandboxDefinition.NetworkEndpointId` |
| 目录映射 / folders | `VirtualSmb.Shares[]` | `SandboxRecipe.MappedFolders` |
| 检查点保存 / checkpoint save | `HcsSaveComputeSystem` + `SaveOptions` | `ICheckpointService.Save` |
| 检查点恢复 / checkpoint restore | `RestoreState.SaveStateFilePath`/`TemplateSystemId` | `ICheckpointService.Restore` |
| HvSocket 服务 / services | `HvSocket.HvSocketConfig.ServiceTable` | `ISandboxVmJsonBuilder`（LSM/NLM） |

## 4. 存储与检查点映射 / Storage & checkpoint mapping

| 项 Item | 位置 Location | 代码 Code |
|---|---|---|
| 层注册 / layer registry | `HKLM\...\Containers\CmService\Layers\<id>` | `IHcsHostLayout.ListLayers` |
| 用户数据 / user data | `ContainerStorages\<id>\sandbox.vhdx` | `SandboxStorageOptions.StoragePath` |
| guest 状态 / guest state | `ContainerStorages\<id>\sandbox.vmgs` | `ISandboxStorageProvisioner` |
| 检查点 / checkpoint | `Snapshots\<id>\SnapshotSavedState.vmrs` | `ICheckpointService` |

## 5. 关键约束 / Key constraints

**中文**：
- **HCS 需要提权**（`0x8037011B`）；引擎在提权时自动选 HCS，否则回退 Windows Sandbox gRPC
  （`ISandboxEngine.IsAvailable`）。
- 裸 HCS **冷启动**需要已存在基础层与用户数据盘；`ISandboxStorageProvisioner` 从既有
  `ContainerStorages` 播种。模板（zygote）可选，只影响启动速度。
- 运行期热映射与进程执行由 Windows Sandbox gRPC 引擎承担。
- 旧的 `wsm hcs` / `wsm layers` 诊断命令依赖已删除的 `Infrastructure/Hcs`，**未迁移**。

**English**:
- **HCS requires elevation** (`0x8037011B`); the engine auto-selects HCS when elevated, otherwise falls
  back to the Windows Sandbox gRPC engine (`ISandboxEngine.IsAvailable`).
- Raw-HCS **cold start** needs an existing base layer and user-data disk; `ISandboxStorageProvisioner`
  seeds from an existing `ContainerStorages`. The template (zygote) is optional and only affects speed.
- Runtime folder mapping and process execution are handled by the Windows Sandbox gRPC engine.
- The legacy `wsm hcs` / `wsm layers` diagnostic commands depended on the deleted `Infrastructure/Hcs`
  and were **not migrated**.

## 6. 常用命令 / Common commands

```powershell
wsm env                 # 引擎/提权/能力 / engine, elevation, capabilities
wsm elevate             # 提权重启以启用 HCS / relaunch elevated
wsm create --cpu 8 --memory 8192 --storage-path E:\sbx --networking enable
wsm start <id> ; wsm stop <id>
wsm checkpoint save <id> snap1 --template
wsm checkpoint list <id> ; wsm checkpoint restore <id> <checkpointId>
wsm help                # 显示帮助 / show help
```

## 证据 / Evidence

- 解决方案 `WindowsSandboxManager.slnx`：**28 个程序集**，分层文件夹
  `/src/00-Core/`、`/src/10-Hcs/`、`/src/20-Engines/`、`/src/30-Connections/`、`/src/40-Host/`、
  `/src/50-App/`、`/tests/90-Tests/`。
- 旧栈（`src/WindowsSandboxManager.*` 与 `tests/WindowsSandboxManager.Tests`）已由提交 `92c2a08`
  整体删除；命名空间根归一为 `Wsm.*`，`WindowsSandboxManager.*` 命名空间全部消除。
- 单元测试 `tests/90-Tests/Wsm.Tests/`（49 个测试：bootstrap、引擎/选择、HCS JSON、序列化、领域、本地化、组合）。
- 相关模块：[02 镜像](02-sandbox-image.md)、[04 HCS](04-hcs.md)、[07 存储与检查点](07-storage-and-checkpoints.md)、
  [10 Schema](10-hcs-schema-and-native-api.md)、[12 模块化架构](12-modular-architecture.md)。
