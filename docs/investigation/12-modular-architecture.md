# 12 · 模块化架构（ABP） / Modular Architecture (ABP)

> 记录 `WindowsSandboxManager` 向 **Volo.Abp.Core 模块化单体**（一模块一程序集、接口化、多引擎）迁移后的架构。
> Documents the `WindowsSandboxManager` architecture after migrating to a **Volo.Abp.Core modular
> monolith** (one module per assembly, interface-first, multi-engine).

## 1. 目标与分层 / Goal and layering

**中文**：把原单体（`WindowsSandboxManager.{Domain,Application,Infrastructure,Cli,App}`）拆分为
**一模块一程序集**：每个程序集恰好包含一个 `AbpModule` 派生类，公共契约 `public`、实现 `internal`。
程序集按职责分层，用 `.slnx` 的**解决方案文件夹**编号（仅组织用途，真正的约束是模块/项目依赖图无环）：

**English**: The original monolith (`WindowsSandboxManager.{Domain,Application,Infrastructure,Cli,App}`)
is split into **one module per assembly**: each assembly contains exactly one `AbpModule`-derived class,
with public contracts and `internal` implementations. Assemblies are tiered by responsibility, numbered
through **solution folders** in `.slnx` (organisational only; the real invariant is an acyclic
module/project dependency graph):

| 层 Tier | 目录 Directory | 职责 Responsibility |
|---|---|---|
| 核心 Core | `/src/00-Core/` | 领域、引擎契约、应用编排、引擎选择 / domain, engine contract, application orchestration, engine selection |
| HCS 能力 HCS capability | `/src/10-Hcs/` | 原生互操作、布局、JSON、存储、检查点、进程、网络 / native interop, layout, JSON, storage, checkpoint, process, network |
| 引擎 Engines | `/src/20-Engines/` | 具体引擎实现 / concrete engine implementations |
| 连接 Connections | `/src/30-Connections/` | gRPC 客户端、`.wsb` 序列化、RDP 连接器 / gRPC client, `.wsb` serialization, RDP connectors |
| 主机 Host | `/src/40-Host/` | 持久化、环境、提权、策略、本地化 / persistence, environment, elevation, policy, localization |
| 表示层 Presentation | `/src/50-App/` | CLI 与 WinUI 宿主（已交付）/ CLI and WinUI hosts (delivered) |
| 测试 Tests | `/tests/90-Tests/` | 测试装配 `Wsm.Tests` / test composition `Wsm.Tests` |

> 旧工程 `/src/WindowsSandboxManager.*` 与 `/tests/WindowsSandboxManager.Tests` 在迁移期曾保留（冻结），
> 已在最终阶段 **P8b（`92c2a08`）整体删除**；迁移期间新栈保持**纯新增**，旧栈保持全绿。
> The legacy projects `/src/WindowsSandboxManager.*` and `/tests/WindowsSandboxManager.Tests` were kept
> (frozen) during the migration and were **removed entirely in the final phase P8b (`92c2a08`)**; during
> the migration the new stack stayed **purely additive** and the old stack stayed green.

## 2. 程序集清单 / Assembly inventory

**中文**：下表是**最终** 32 个程序集的完整清单（P8b、第三个引擎与可选择的 RDP 连接器落地后）。P5 引入的 4 个
**预留空壳**（容器 JSON / 进程 / 网络 / 容器引擎）现均已实现为 **核心**，因此全部为已实现。

**English**: The table lists the complete **final** inventory of 32 assemblies (after P8b, the third
engine and the selectable RDP connector landed). The four **reserved shells** introduced in P5
(container JSON, process, network, container engine) are now implemented as **Core**, so all are
implemented.

| 程序集 Assembly | 模块 Module | 公共契约 / 职责 Contract / responsibility | 状态 Status |
|---|---|---|---|
| `Wsm.Domain` | `SandboxDomainModule` | 领域模型与不变量（`SandboxInstance`、`SandboxRecipe`、`SandboxCheckpoint`） / domain model & invariants | 核心 Core |
| `Wsm.Engines.Abstractions` | `SandboxEngineAbstractionsModule` | `ISandboxEngine`、`ISandboxEngineRegistry`、`SandboxEngineCapabilities`、`SandboxEngineKind` | 核心 Core |
| `Wsm.Application` | `SandboxApplicationModule` | `ISandboxManager`（用例编排）/ `ISandboxManager` (use-case orchestration) | 核心 Core |
| `Wsm.Application.EngineSelection` | `EngineSelectionModule` | `ISandboxEngineSelector`（默认 HCS）/ `ISandboxEngineSelector` (default HCS) | 核心 Core |
| `Wsm.Hcs.Interop` | `HcsInteropModule` | `IHcsClient`（`computecore.dll` P/Invoke）/ `IHcsClient` | 核心 Core |
| `Wsm.Hcs.Layout` | `HcsLayoutModule` | `IHcsHostLayout`（主机布局 / 基础层）/ `IHcsHostLayout` | 核心 Core |
| `Wsm.Hcs.Json.Sandbox` | `SandboxVmJsonModule` | `ISandboxVmJsonBuilder`（Sandbox/VM HCS JSON）/ `ISandboxVmJsonBuilder` | 核心 Core |
| `Wsm.Hcs.Json.Container` | `ContainerJsonModule` | `IContainerJsonBuilder`（容器 HCS JSON / schema 2.1）/ `IContainerJsonBuilder` | 核心 Core |
| `Wsm.Hcs.Storage` | `HcsStorageModule` | `ISandboxStorageProvisioner`（用户数据盘）/ `ISandboxStorageProvisioner` | 核心 Core |
| `Wsm.Hcs.Checkpoint` | `HcsCheckpointModule` | `ICheckpointService`（保存/枚举/恢复）/ `ICheckpointService` | 核心 Core |
| `Wsm.Hcs.Process` | `HcsProcessModule` | `IComputeProcessExecutor`（`HcsCreateProcess`/`HcsWaitForProcessExit`）/ process execution | 核心 Core |
| `Wsm.Hcs.Network` | `HcsNetworkModule` | `IHnsNetworkService`（HNS `HNSCall` endpoint）/ HNS endpoints | 核心 Core |
| `Wsm.Engine.Hcs` | `HcsEngineModule` | `HcsSandboxEngine : ISandboxEngine`（默认）/ default engine | 核心 Core |
| `Wsm.Engine.WindowsSandbox` | `WindowsSandboxEngineModule` | `WindowsSandboxEngine : ISandboxEngine`（gRPC）/ gRPC engine | 核心 Core |
| `Wsm.Engine.WindowsContainer` | `WindowsContainerEngineModule` | `WindowsContainerEngine : ISandboxEngine`（HCS 进程隔离容器，复用 Sandbox 镜像层）/ process-isolated container | 核心 Core |
| `Wsm.SandboxServer.Client` | `SandboxServerClientModule` | `ISandboxServerClient`（命名管道 gRPC）/ named-pipe gRPC client | 核心 Core |
| `Wsm.Recipe.Serialization` | `WsbRecipeSerializationModule` | `ISandboxRecipeSerializer`（`.wsb` XML）/ `.wsb` XML codec | 核心 Core |
| `Wsm.Connection.RdpBase` | `RdpBaseModule` | `IRdpBaseConnectorFactory`、`IRdpBaseConnector` | 核心 Core |
| `Wsm.Connection.RdpHost` | `RdpHostModule` | `IRdpSessionHost`/`IRdpConnectorHost`/`IRdpSandboxControl`/`IRdpConnectorPreference`、`SelectingRdpSessionHost`（连接器契约与选择）/ connector contract & selection | 核心 Core |
| `Wsm.Connection.RdpHost.Ax` | （无模块 / no module） | `RdpClientControl`（`mstscax` 互操作 + WinForms 宿主）/ shared ActiveX host | 核心 Core |
| `Wsm.Connection.RdpHost.Wpf` | `WpfRdpHostModule` | `WpfRdpConnectorHost`（默认 WPF 连接器）/ default WPF connector | 核心 Core |
| `Wsm.Connection.RdpHost.WinUi` | `WinUiRdpHostModule` | `WinUiRdpConnectorHost`（启动 WinUI3 连接器进程）/ launches the WinUI3 connector | 核心 Core |
| `Wsm.Connection.Session` | `SessionLaunchModule` | `ISessionLauncher` | 核心 Core |
| `Wsm.Connection.Legacy` | `LegacyLauncherModule` | `ILegacySandboxLauncher` | 核心 Core |
| `Wsm.Host.Environment` | `HostEnvironmentModule` | `ISandboxEnvironment`（特性探测/管道名）/ `ISandboxEnvironment` | 核心 Core |
| `Wsm.Host.Elevation` | `ElevationModule` | `IElevationService`（提权检测/重启）/ `IElevationService` | 核心 Core |
| `Wsm.Host.Policy` | `MultiInstancePolicyModule` | `IMultiInstancePolicy` | 核心 Core |
| `Wsm.Persistence` | `SandboxPersistenceModule` | `ISandboxInstanceRepository`（JSON + `.wsb`）/ repository | 核心 Core |
| `Wsm.Localization` | `WsmLocalizationModule` | `WindowsSandboxManagerResource`（ABP JSON 本地化）/ ABP JSON localization | 核心 Core |
| `Wsm.Cli` | `CliModule` | 控制台命令（启动模块）/ console commands | 已交付 Delivered（P7a） |
| `Wsm.Ui.WinUI` | `WinUiModule` | WinUI3 宿主 + MVVM + `.resw`/`x:Uid` / WinUI3 host | 已交付 Delivered（P7b） |
| `Wsm.Tests` | `TestModule` | 测试装配 / test composition | 核心 Core |

### 2a. 修订（2026-10）：第 29、30 个程序集 / Amendment: the 29th and 30th assemblies

**中文**：上表是 ABP 迁移（P1–P8）的**历史终态**。2026-10 的连接改造新增两个连接套件：
`Wsm.Connection.RemoteApp`（`RemoteAppModule`：remoteapptool 风格的 `mstsc` + `.rdp` + 命名管道桥，
备选）与 `Wsm.Connection.RailClient`（`RailClientModule`：**WDAG 式 RAIL 客户端**——独立客户端进程经
RDPBASE 承载 `mstscax`，远程应用窗口为自由浮动的顶层窗口；RAIL 的默认呈现）。因此程序集总数为 **30**；
`ISessionLauncher`（`Wsm.Connection.Session`）按模式路由到 RAIL 客户端、窗口连接器或备选套件。

**English**: The table above is the **historical end state** of the ABP migration (P1–P8). The
2026-10 connection rework added two connection kits: `Wsm.Connection.RemoteApp` (`RemoteAppModule`:
the remoteapptool-style `mstsc` + `.rdp` + named-pipe bridge, the alternative) and
`Wsm.Connection.RailClient` (`RailClientModule`: the **WDAG-style RAIL client** — a dedicated client
process hosting `mstscax` over RDPBASE with free-floating application windows; the default RAIL
presentation). The inventory therefore holds **30** assemblies; `ISessionLauncher`
(`Wsm.Connection.Session`) routes by mode to the RAIL client, the windowed connector or the
alternative kit.

## 3. ABP 约定 / ABP conventions

**中文**：模块装配完全依赖 ABP 的**约定式 DI**，不写手写 `DependencyInjection.cs` 扩展。

**English**: Module wiring relies entirely on ABP's **conventional DI**; there is no hand-written
`DependencyInjection.cs` extension.

| 约定 Convention | 中文 | English |
|---|---|---|
| 模块 Module | 每个程序集一个 `AbpModule` 派生类，`[DependsOn(...)]` 声明模块依赖 | one `AbpModule` per assembly; `[DependsOn(...)]` declares module dependencies |
| 生命周期 Lifecycle | 实现 `ITransientDependency` / `IScopedDependency` / `ISingletonDependency` 即自动注册 | implementing a marker interface auto-registers the service |
| 暴露 Expose | 实现类显式 `[ExposeServices(typeof(IFoo))]`，实现为 `internal` | implementation is `internal` and explicitly exposes its interface via `[ExposeServices]` |
| 引擎 Engine | 引擎必须 `ISingletonDependency`（持有原生句柄/连接）/ engines MUST be `ISingletonDependency` | engines hold native handles/connections |
| 键控 Keyed | **不使用** keyed service；多引擎查找走 `ISandboxEngineRegistry.Find(kind)` | **no** keyed services; multi-engine lookup goes through the registry |
| 引导 Bootstrap | `AbpApplicationFactory.CreateAsync<TStartupModule>(options => options.UseAutofac())`；**`app.ServiceProvider` 才是 `IServiceProvider`**（`app.Services` 是 `IServiceCollection`） | `AbpApplicationFactory` boot; **`app.ServiceProvider` is the `IServiceProvider`** |
| 退出 Shutdown | `using var app = ...` + `finally { await app.ShutdownAsync(); }`（`IAbpApplicationWithInternalServiceProvider` 仅 `IDisposable`） | dispose + `ShutdownAsync`; the app handle is only `IDisposable` |

**中文**：模块类只做装配（`ConfigureServices` / options / 钩子），不写业务逻辑。多实现（引擎）不注册为
键控服务；`SandboxEngineRegistry` 在**重复 `Kind`** 时快速失败，选择器**显式按 `SandboxEngineKind`
数值排序**，不依赖 DI 枚举顺序。

**English**: Module classes only assemble (`ConfigureServices` / options / hooks) and hold no business
logic. Multiple implementations (engines) are not keyed; `SandboxEngineRegistry` fails fast on a
duplicate `Kind`, and the selector orders engines **explicitly by `SandboxEngineKind`**, never by DI
enumeration order.

## 4. 引擎抽象 / Engine abstraction

**中文**：统一契约 `ISandboxEngine`（`Wsm.Engines.Abstractions`）由 `Name`、`Kind`、`Capabilities`、
`IsAvailable` 与生命周期方法组成；可选能力（映射目录、执行进程、IP、检查点）带**默认抛异常**实现，
引擎只覆盖其支持的部分。`ISandboxEngineRegistry` 提供 `Engines` 与 `Find(kind)`；
`ISandboxEngineSelector.Select(request)` 的规则为：仅考虑可用引擎 → 按 recipe 能力匹配 → 按
`SandboxEngineKind` 升序（HCS 优先）→ 无可用引擎时抛 `SandboxNotSupportedException`（含提权提示）。

**English**: The unified `ISandboxEngine` contract (`Wsm.Engines.Abstractions`) exposes `Name`, `Kind`,
`Capabilities`, `IsAvailable` and lifecycle methods; optional capabilities (folder mapping, process
execution, IPs, checkpoints) carry **default-throwing** implementations so an engine overrides only what
it supports. `ISandboxEngineRegistry` exposes `Engines` and `Find(kind)`; `ISandboxEngineSelector.Select`
rules: consider only available engines → match recipe-required capabilities → ascending
`SandboxEngineKind` (HCS first) → throw `SandboxNotSupportedException` (with an elevation hint) when none
is available.

| 引擎 Engine | `SandboxEngineKind` | 状态 Status | 说明 Notes |
|---|---|---|---|
| HCS（默认 / default） | `HcsSandbox = 0` | 核心 Core | VM 型 DisposableVM，需提权；JSON/布局/存储/检查点模块支撑 / requires elevation |
| Windows Sandbox | `WindowsSandbox = 1` | 核心 Core | 非提权 gRPC 控制面（命名管道）/ non-elevated gRPC over named pipe |
| Windows Container | `WindowsContainer = 2` | 核心 Core | **HCS 进程隔离容器**（MXC `processcontainer` 对应的轻量隔离层）：复用 Windows Sandbox 最小镜像层（只读 `Storage.Layers`）+ 每实例 scratch，需提权；容器 JSON / 进程执行 / HNS 能力模块支撑 / HCS process-isolated container, requires elevation |

> 选择行为与现状一致：提权时 HCS，未提权时 HCS 不可用 → 回退 Windows Sandbox；进程隔离容器按
> `SandboxEngineKind` 升序排在 HCS 之后，仅在显式首选时被选中。
> Selection matches existing behaviour: HCS when elevated, otherwise fall back to Windows Sandbox; the
> process-isolated container is ordered after HCS and is selected only when explicitly preferred.

## 5. 本地化策略 / Localization strategy

**中文**：本地化分两路。**非 UI 文案**（领域/应用/CLI 消息）由 ABP 承载：`Wsm.Localization` 程序集
注册 `WindowsSandboxManagerResource`，JSON 资源以**嵌入式虚拟文件**提供，路径
`/Localization/WindowsSandboxManager/{culture}.json`，语言为 `en` 与 `zh-Hans`；消费方通过
`IStringLocalizer<WindowsSandboxManagerResource>` 读取。**UI 文案**（P7b 已交付）由 WinUI `.resw` + `x:Uid`
承载（`Resources/en-US/Resources.resw`、`Resources/zh-Hans/Resources.resw`），运行期切换语言后需重建
窗口/页面（WinUI 无自动重载）。

**English**: Localization is two-tracked. **Non-UI text** (domain/application/CLI messages) is carried by
ABP: the `Wsm.Localization` assembly registers `WindowsSandboxManagerResource` with **embedded virtual
JSON** files at `/Localization/WindowsSandboxManager/{culture}.json` for `en` and `zh-Hans`; consumers
read through `IStringLocalizer<WindowsSandboxManagerResource>`. **UI text** (delivered in P7b) is carried
by WinUI `.resw` + `x:Uid` (`Resources/en-US/Resources.resw`, `Resources/zh-Hans/Resources.resw`); after a
runtime culture switch the window/page must be rebuilt (WinUI has no automatic reload).

| 键 Key | 用途 Purpose |
|---|---|
| `EngineUnavailable` | 无可用引擎（含提权提示）/ no engine available (elevation hint) |
| `MultipleInstances` | 单实例引擎拒绝并发实例 / single-instance engine rejects a concurrent instance |
| `CheckpointNotSupported` | 引擎不支持检查点 / engine does not support checkpoints |
| `SandboxNotFound` | 未找到沙箱 / sandbox not found |
| `ElevationRequired` | 需要管理员权限 / administrator privileges required |

> **注意 / Note**：`AddEmbedded<WsmLocalizationModule>("Wsm.Localization")` 需**显式**传入根命名空间；
> 省略时 ABP 10.2.0 不推断出期望的虚拟路径（资源会落到 `/Wsm/Localization/...`）。
> `AddEmbedded<WsmLocalizationModule>("Wsm.Localization")` passes the root namespace **explicitly**;
> omitting it does not infer the expected virtual path under ABP 10.2.0.

## 6. 命名空间规则 / Namespace rule

**中文**：**命名空间根 == 程序集名**（允许根下子命名空间，如 `*.Internal` / `*.Mapping`）。
全部 32 个程序集均使用 `Wsm.*` 命名空间。迁移期间 `Wsm.Domain` 曾暂时保留
`WindowsSandboxManager.Domain*`（过渡债），**已在 P8b（`92c2a08`）重命名并消除**；仓库内
`WindowsSandboxManager.*` 命名空间**全部消除**（守卫 grep 为空）。

**English**: **Namespace root == assembly name** (sub-namespaces such as `*.Internal` / `*.Mapping` are
allowed). All 32 assemblies now use `Wsm.*` namespaces. During the migration `Wsm.Domain` temporarily kept
`WindowsSandboxManager.Domain*` (accepted transition debt); it was **renamed and eliminated in P8b
(`92c2a08`)**, and every `WindowsSandboxManager.*` namespace is now **gone** (the guard grep is empty).

## 7. 阶段与门禁 / Phases and gates

**中文**：迁移按阶段推进，每阶段结束由 **Oracle 门禁**评审层边界/依赖方向/接口纯度，并产出非阻塞
发现（F-items）供后续回收。迁移期间旧栈保持不变、新栈纯新增；**P8b 已删除旧栈并完成命名空间归一**。

**English**: The migration proceeds in phases; each phase ends with an **Oracle gate** reviewing layer
boundaries, dependency direction and interface purity, producing non-blocking findings (F-items) for
later recovery. During the migration the old stack was untouched and the new stack purely additive;
**P8b deleted the old stack and completed the namespace normalisation**.

| 阶段 Phase | 交付 Deliverable | 门禁 Gate |
|---|---|---|
| P1 Foundation | ABP 最小包 + 版本对齐 + 核心三程序集（Domain / Engines.Abstractions / Application）+ 引导测试 / ABP packages + 3 core assemblies + bootstrap test | Gate 1 |
| P2 HCS capability | `Wsm.Hcs.{Interop,Layout,Json.Sandbox,Storage,Checkpoint}` | Gate 2 |
| P3 Infra ports | `Wsm.Host.*`、`Wsm.Persistence`、`Wsm.SandboxServer.Client`、`Wsm.Recipe.Serialization`、`Wsm.Connection.*` | Gate 3 |
| P4 Engines + Selection | `Wsm.Engine.{Hcs,WindowsSandbox}`、`Wsm.Application.EngineSelection`、`SandboxManager` 实现 / engine implementations + selector + manager | Gate 4 |
| P5 Reserved stubs | `Wsm.Engine.WindowsContainer`、`Wsm.Hcs.{Json.Container,Process,Network}` | Gate 5 |
| P6 Localization (+ presentation start) | `Wsm.Localization`（ABP JSON）+ 文档 / localization + docs | Gate 6 + final |
| P7 CLI + WinUI | `Wsm.Cli`、`Wsm.Ui.WinUI`（`.resw`/`x:Uid`、引擎/语言切换）/ CLI + WinUI | final |
| P8 收尾 Cleanup | 删除旧栈、命名空间重命名（消除 `WindowsSandboxManager.*`）/ delete old stack, rename namespaces | final |

> P5 引入的 4 个**预留空壳**（`Wsm.Engine.WindowsContainer`、`Wsm.Hcs.{Json.Container,Process,Network}`）
> 已在后续「第三个引擎（进程隔离容器）」落地时**全部实现**；§2 的最终状态已如实标注为**核心**。
>
> The four **reserved shells** introduced in P5 (`Wsm.Engine.WindowsContainer` and
> `Wsm.Hcs.{Json.Container,Process,Network}`) were **fully implemented** when the third engine
> (process-isolated container) landed; §2 now reflects their true **Core** status.

## 8. P6 / P7 / P8 收尾 / Finalization (localization · presentation · cleanup)

**中文**：迁移的收尾三段及其提交链如下。

**English**: The three finalization segments and their commit chain follow.

| 段 Segment | 范围 Scope | 提交 Commit |
|---|---|---|
| 基线 Baseline | P1 foundation + P2 HCS capability + P3a host/persistence | `e174887` |
| P3 | infra-port 模块程序集 + gate-2 清理 / infra-port modules + gate-2 cleanups | `5c5d9fd` → `fa4e502` |
| P4 | 引擎 + 选择器 + `SandboxManager` / engines + selector + manager | `09abb76` |
| P5 | 预留空壳 + 引擎/管理器对齐 / reserved stubs + parity | `267be86` |
| **P6** | `Wsm.Localization`（ABP JSON `en`/`zh-Hans`）+ 双语模块化文档 / localization + docs | `dc2e2e6` |
| **P7** | P7a `ISandboxManager` 扩展 + ABP 承载 `Wsm.Cli`；P7b WinUI3 管理界面（`.resw`/`x:Uid`、引擎/语言切换） / CLI + WinUI | `06d4abb` → `b01311d` |
| **P8** | P8a 旧测试对等移植；**P8b 删除旧栈 + 命名空间根归一** / test parity; delete legacy + rename namespaces | `2bef8ad` → `92c2a08` |

**提交链 / Commit chain**：`e174887` → `5c5d9fd` → `fa4e502` → `09abb76` → `267be86` → `dc2e2e6`
→ `06d4abb` → `b01311d` → `92c2a08`（HEAD）。

**最终守卫 / Final guards**（P8b，全部通过 / all green）：
- **命名空间根 == 程序集名**：全部 32 个程序集满足；`WindowsSandboxManager.*` 命名空间守卫 grep 为空。
- **旧栈删除**：`src/WindowsSandboxManager.*` 与 `tests/WindowsSandboxManager.Tests` 不复存在；
  旧 `Infrastructure/Hcs`、手写 DI 扩展、`Domain.Abstractions`（`Ports.cs`、
  `SandboxBackendCapabilities.cs`）全部删除。
- **无悬空引用**：`ISandboxBackend`、Domain 副本 `ISandboxRecipeSerializer` 等旧端口零引用。
- **全绿**：`dotnet build WindowsSandboxManager.slnx` 0 错误；`Wsm.Tests` 49 个测试通过。

**English**: Namespace roots equal assembly names for all 32 assemblies and the `WindowsSandboxManager.*`
namespaces are gone; the legacy projects, `Infrastructure/Hcs`, hand-written DI extensions and
`Domain.Abstractions` ports were deleted; no dangling references to the old ports remain; the build is
clean and all 49 tests pass.

## 证据 / Evidence

- 解决方案：`WindowsSandboxManager.slnx`（**32 个程序集**；`/src/00-Core|10-Hcs|20-Engines|30-Connections|40-Host|50-App/`、`/tests/90-Tests/`）。
- 引擎契约：`src/00-Core/Wsm.Engines.Abstractions/{ISandboxEngine,ISandboxEngineRegistry,Internal/SandboxEngineRegistry}.cs`。
- 选择器：`src/00-Core/Wsm.Application.EngineSelection/Internal/EngineSelector.cs`。
- 引擎实现：`src/20-Engines/Wsm.Engine.Hcs/`、`Wsm.Engine.WindowsSandbox/`、`Wsm.Engine.WindowsContainer/WindowsContainerEngine.cs`。
- 本地化：`src/40-Host/Wsm.Localization/{WsmLocalizationModule,WindowsSandboxManagerResource}.cs` 与
  `Localization/WindowsSandboxManager/{en,zh-Hans}.json`。
- 应用编排：`src/00-Core/Wsm.Application/Internal/SandboxManager.cs`。
- 测试：`tests/90-Tests/Wsm.Tests/`（`Bootstrap/`、`Engines/`、`Hcs/`、`Localization/`、`Composition/`、`Application/`）。
- 计划与过程：`C:\Users\User\.opencode\plan\hcs-abp-modularization-plan.md`、`.slim/deepwork/abp-modularization.md`。
- 相关模块：[11 项目映射](11-project-mapping.md)、[04 HCS](04-hcs.md)、[07 存储与检查点](07-storage-and-checkpoints.md)。

---

## 9. 最终验收 / Final sign-off（门禁 7）

**判决 / Verdict**：`VERDICT = APPROVE_WITH_NONBLOCKING_FINDINGS` —— **ABP 模块化改造（P1–P8）正式签署通过**。
计划 §14 全部 11 项均已满足（见门禁 7 复核表）。

**两点解释性裁定 / Two interpretation rulings**：
1. **`Wsm.Cli` 的 `AssemblyName = wsm` 予以保留**。§14「命名空间根 == 程序集名」针对的是命名空间卫生
   （命名空间 `Wsm.Cli` 与项目名一致、无遗留根），而非二进制文件命名；`wsm.exe` 是有意的 CLI UX 决策，
   与 README/帮助文本一致，无技术风险（无 `InternalsVisibleTo`、无类型转发、无附属资源耦合）。
2. **`Wsm.Tests` 含 6 个测试模块予以接受**。「一模块一程序集」约束的是**生产**模块图；
   测试程序集使用多个组合根（`TestModule`/`InfraPortsTestModule`/`EnginesTestModule`/`HcsTestModule`/
   `RecipeTestModule`/`LocalizationTestModule`）以隔离地启动不同依赖图，是标准 ABP 测试实践。

**遗留 backlog（非阻塞，按优先级）**：
1. 引擎偏好端到端打通：`ISandboxManager.StartAsync` 增加可选 `SandboxEngineKind?`，并接通 WinUI 选择。
2. 引入就地 `UpdateAsync`，消除 UI「保存」= 删除+重建 导致的实例 ID 变更与检查点孤儿。
3. 恢复的检查点归入所属实例（或在文档 07/11 中写明孤儿 VM 语义）。
4. ~~可选：`WindowsContainerEngine.Capabilities` 改为诚实全 false 或加预留标记~~（**已处理**：第三个引擎已实现，
   能力矩阵已按其真实支持更新）；其余保留：`wsm layers` 由 `IHcsHostLayout` 重新提供；
   `WindowsSandboxEngine.IsAvailable => _environment.IsSupported`。
5. 文档微调：P8c 入提交链、§2 记 6 个测试模块、§6 补 `AssemblyName=wsm` 括注；`AppLocalizer` 改 internal（**已处理**）。
6. **进程隔离容器的后续工作**：scratch 层卷（`\\?\Volume{GUID}\`）自动供给、容器网络的 HNS endpoint attach。

**English**: The ABP modularization refactor (P1–P8) is signed off; all plan §14 items are met.
Both interpretation notes are ruled acceptable (`Wsm.Cli` keeps `AssemblyName=wsm`; `Wsm.Tests` holds 6
test composition roots). Deferred items are listed as a non-blocking backlog; the `AppLocalizer`
visibility fix is already applied.
