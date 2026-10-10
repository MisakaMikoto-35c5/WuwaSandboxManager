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
| 表示层 Presentation | `/src/50-App/` | CLI 与 WinUI 宿主（P7）/ CLI and WinUI hosts (P7) |
| 测试 Tests | `/tests/90-Tests/` | 测试装配 `Wsm.Tests` / test composition `Wsm.Tests` |

> 旧工程 `/src/WindowsSandboxManager.*` 与 `/tests/WindowsSandboxManager.Tests` 在迁移期保留（冻结），
> 由最终阶段 P8 删除；期间新栈保持**纯新增**，旧栈保持全绿。
> The legacy projects `/src/WindowsSandboxManager.*` and `/tests/WindowsSandboxManager.Tests` are kept
> (frozen) during the migration and removed in the final phase P8; the new stack stays **purely additive**
> and the old stack stays green in the meantime.

## 2. 程序集清单 / Assembly inventory

**中文**：下表是当前（P6 本地化阶段后）新栈的程序集。**核心**=已实现；**预留**=可编译空壳，
`IsAvailable=false` / 成员抛 `SandboxNotSupportedException`；**计划**=P7 交付。

**English**: The table lists the new-stack assemblies as of the current state (after the P6 localization
work). **Core** = implemented; **Reserved** = compilable shell, `IsAvailable=false` / members throw
`SandboxNotSupportedException`; **Planned** = delivered in P7.

| 程序集 Assembly | 模块 Module | 公共契约 / 职责 Contract / responsibility | 状态 Status |
|---|---|---|---|
| `Wsm.Domain` | `SandboxDomainModule` | 领域模型与不变量（`SandboxInstance`、`SandboxRecipe`、`SandboxCheckpoint`） / domain model & invariants | 核心 Core |
| `Wsm.Engines.Abstractions` | `SandboxEngineAbstractionsModule` | `ISandboxEngine`、`ISandboxEngineRegistry`、`SandboxEngineCapabilities`、`SandboxEngineKind` | 核心 Core |
| `Wsm.Application` | `SandboxApplicationModule` | `ISandboxManager`（用例编排）/ `ISandboxManager` (use-case orchestration) | 核心 Core |
| `Wsm.Application.EngineSelection` | `EngineSelectionModule` | `ISandboxEngineSelector`（默认 HCS）/ `ISandboxEngineSelector` (default HCS) | 核心 Core |
| `Wsm.Hcs.Interop` | `HcsInteropModule` | `IHcsClient`（`computecore.dll` P/Invoke）/ `IHcsClient` | 核心 Core |
| `Wsm.Hcs.Layout` | `HcsLayoutModule` | `IHcsHostLayout`（主机布局 / 基础层）/ `IHcsHostLayout` | 核心 Core |
| `Wsm.Hcs.Json.Sandbox` | `SandboxVmJsonModule` | `ISandboxVmJsonBuilder`（Sandbox/VM HCS JSON）/ `ISandboxVmJsonBuilder` | 核心 Core |
| `Wsm.Hcs.Json.Container` | `ContainerJsonModule` | `IContainerJsonBuilder`（容器 HCS JSON）/ `IContainerJsonBuilder` | 预留 Reserved |
| `Wsm.Hcs.Storage` | `HcsStorageModule` | `ISandboxStorageProvisioner`（用户数据盘）/ `ISandboxStorageProvisioner` | 核心 Core |
| `Wsm.Hcs.Checkpoint` | `HcsCheckpointModule` | `ICheckpointService`（保存/枚举/恢复）/ `ICheckpointService` | 核心 Core |
| `Wsm.Hcs.Process` | `HcsProcessModule` | `IComputeProcessExecutor`（in-guest 执行）/ `IComputeProcessExecutor` | 预留 Reserved |
| `Wsm.Hcs.Network` | `HcsNetworkModule` | `IHnsNetworkService`（HNS endpoint）/ `IHnsNetworkService` | 预留 Reserved |
| `Wsm.Engine.Hcs` | `HcsEngineModule` | `HcsSandboxEngine : ISandboxEngine`（默认）/ default engine | 核心 Core |
| `Wsm.Engine.WindowsSandbox` | `WindowsSandboxEngineModule` | `WindowsSandboxEngine : ISandboxEngine`（gRPC）/ gRPC engine | 核心 Core |
| `Wsm.Engine.WindowsContainer` | `WindowsContainerEngineModule` | `WindowsContainerEngine : ISandboxEngine` | 预留 Reserved |
| `Wsm.SandboxServer.Client` | `SandboxServerClientModule` | `ISandboxServerClient`（命名管道 gRPC）/ named-pipe gRPC client | 核心 Core |
| `Wsm.Recipe.Serialization` | `WsbRecipeSerializationModule` | `ISandboxRecipeSerializer`（`.wsb` XML）/ `.wsb` XML codec | 核心 Core |
| `Wsm.Connection.RdpBase` | `RdpBaseModule` | `IRdpBaseConnectorFactory`、`IRdpBaseConnector` | 核心 Core |
| `Wsm.Connection.Session` | `SessionLaunchModule` | `ISessionLauncher` | 核心 Core |
| `Wsm.Connection.Legacy` | `LegacyLauncherModule` | `ILegacySandboxLauncher` | 核心 Core |
| `Wsm.Host.Environment` | `HostEnvironmentModule` | `ISandboxEnvironment`（特性探测/管道名）/ `ISandboxEnvironment` | 核心 Core |
| `Wsm.Host.Elevation` | `ElevationModule` | `IElevationService`（提权检测/重启）/ `IElevationService` | 核心 Core |
| `Wsm.Host.Policy` | `MultiInstancePolicyModule` | `IMultiInstancePolicy` | 核心 Core |
| `Wsm.Persistence` | `SandboxPersistenceModule` | `ISandboxInstanceRepository`（JSON + `.wsb`）/ repository | 核心 Core |
| `Wsm.Localization` | `WsmLocalizationModule` | `WindowsSandboxManagerResource`（ABP JSON 本地化）/ ABP JSON localization | 核心 Core |
| `Wsm.Cli` | `CliModule` | 控制台命令（启动模块）/ console commands | 计划 Planned |
| `Wsm.Ui.WinUI` | `WinUiModule` | WinUI3 宿主 + MVVM + `.resw`/`x:Uid` / WinUI3 host | 计划 Planned |
| `Wsm.Tests` | `TestModule` | 测试装配 / test composition | 核心 Core |

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
| Windows Container | `WindowsContainer = 2` | 预留 Reserved | 轻量隔离，`IsAvailable=false`；`IContainerJsonBuilder`/`IComputeProcessExecutor`/`IHnsNetworkService` 同为预留 / lightweight isolation, reserved |

> 选择行为与现状一致：提权时 HCS，未提权时 HCS 不可用 → 回退 Windows Sandbox。
> Selection matches existing behaviour: HCS when elevated, otherwise fall back to Windows Sandbox.

## 5. 本地化策略 / Localization strategy

**中文**：本地化分两路。**非 UI 文案**（领域/应用/CLI 消息）由 ABP 承载：`Wsm.Localization` 程序集
注册 `WindowsSandboxManagerResource`，JSON 资源以**嵌入式虚拟文件**提供，路径
`/Localization/WindowsSandboxManager/{culture}.json`，语言为 `en` 与 `zh-Hans`；消费方通过
`IStringLocalizer<WindowsSandboxManagerResource>` 读取。**UI 文案**（P7）由 WinUI `.resw` + `x:Uid`
承载（`Resources/en-US/Resources.resw`、`Resources/zh-Hans/Resources.resw`），运行期切换语言后需重建
窗口/页面（WinUI 无自动重载）。

**English**: Localization is two-tracked. **Non-UI text** (domain/application/CLI messages) is carried by
ABP: the `Wsm.Localization` assembly registers `WindowsSandboxManagerResource` with **embedded virtual
JSON** files at `/Localization/WindowsSandboxManager/{culture}.json` for `en` and `zh-Hans`; consumers
read through `IStringLocalizer<WindowsSandboxManagerResource>`. **UI text** (P7) is carried by WinUI
`.resw` + `x:Uid` (`Resources/en-US/Resources.resw`, `Resources/zh-Hans/Resources.resw`); after a runtime
culture switch the window/page must be rebuilt (WinUI has no automatic reload).

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
新程序集一律使用 `Wsm.*` 命名空间；`Wsm.Domain` 暂时保留 `WindowsSandboxManager.Domain*`
（过渡债），最终 P8 重命名并消除全部 `WindowsSandboxManager.*` 命名空间。

**English**: **Namespace root == assembly name** (sub-namespaces such as `*.Internal` / `*.Mapping` are
allowed). New assemblies use `Wsm.*` namespaces; `Wsm.Domain` temporarily keeps
`WindowsSandboxManager.Domain*` (accepted transition debt) until P8 renames it and eliminates every
`WindowsSandboxManager.*` namespace.

## 7. 阶段与门禁 / Phases and gates

**中文**：迁移按阶段推进，每阶段结束由 **Oracle 门禁**评审层边界/依赖方向/接口纯度，并产出非阻塞
发现（F-items）供后续回收。旧栈在 P8 前保持不变，新栈保持纯新增。

**English**: The migration proceeds in phases; each phase ends with an **Oracle gate** reviewing layer
boundaries, dependency direction and interface purity, producing non-blocking findings (F-items) for
later recovery. The old stack is unchanged until P8; the new stack stays purely additive.

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

## 证据 / Evidence

- 解决方案：`WindowsSandboxManager.slnx`（`/src/00-Core|10-Hcs|20-Engines|30-Connections|40-Host/`、`/tests/90-Tests/`）。
- 引擎契约：`src/00-Core/Wsm.Engines.Abstractions/{ISandboxEngine,ISandboxEngineRegistry,Internal/SandboxEngineRegistry}.cs`。
- 选择器：`src/00-Core/Wsm.Application.EngineSelection/Internal/EngineSelector.cs`。
- 引擎实现：`src/20-Engines/Wsm.Engine.Hcs/`、`Wsm.Engine.WindowsSandbox/`、`Wsm.Engine.WindowsContainer/WindowsContainerEngine.cs`。
- 本地化：`src/40-Host/Wsm.Localization/{WsmLocalizationModule,WindowsSandboxManagerResource}.cs` 与
  `Localization/WindowsSandboxManager/{en,zh-Hans}.json`。
- 应用编排：`src/00-Core/Wsm.Application/Internal/SandboxManager.cs`。
- 测试：`tests/90-Tests/Wsm.Tests/`（`Bootstrap/`、`Engines/`、`Hcs/`、`Localization/`、`Composition/`、`Application/`）。
- 计划与过程：`C:\Users\User\.opencode\plan\hcs-abp-modularization-plan.md`、`.slim/deepwork/abp-modularization.md`。
- 相关模块：[11 项目映射](11-project-mapping.md)、[04 HCS](04-hcs.md)、[07 存储与检查点](07-storage-and-checkpoints.md)。
