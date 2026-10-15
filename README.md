# WindowsSandboxManager

用 **.NET 10** 编写的 Windows Sandbox 管理工具集：WinUI3 GUI + CLI + 可复用的分层引擎。
支持**配置、启动、停止、连接**多个 Windows Sandbox 实例，提供
**RemoteApp（RAIL）** 与**常规全桌面**两种连接方式。

**控制面以 HCS（Host Compute Service）为主、`WindowsSandboxServer` gRPC 为回退。**
HCS 直连可提供：多实例、CPU 核数、自定义用户数据位置、自定义基础镜像层、检查点（checkpoint）。

> 详细技术调查见 [`docs/investigation.md`](docs/investigation.md)（单文件汇总）与
> [`docs/investigation/`](docs/investigation/README.md)（**中英双语、按模块拆分**的调查文档集）。
>
> See [`docs/investigation.md`](docs/investigation.md) (single-file summary) and
> [`docs/investigation/`](docs/investigation/README.md) (bilingual, module-by-module investigation).

---

## 三种引擎

引擎统一由 `ISandboxEngine`（`Wsm.Engines.Abstractions`）契约描述；选择器 `ISandboxEngineSelector`
**仅考虑 `IsAvailable == true` 的引擎**，按 recipe 所需能力匹配后，再按 `SandboxEngineKind` 升序
（HCS 优先）选择；无可用引擎时抛 `SandboxNotSupportedException`（含提权提示）。

| | **HCS（默认）** | **Windows Sandbox（gRPC）** | **Windows Container（进程隔离）** |
| --- | --- | --- | --- |
| `SandboxEngineKind` | `HcsSandbox = 0` | `WindowsSandbox = 1` | `WindowsContainer = 2` |
| 入口 | `computecore.dll`（operation-based） | `WindowsSandboxServer` gRPC over named pipe | HCS `Container` 计算系统（进程隔离，`Container` JSON） |
| `IsAvailable` | 提权时 `true` | 恒 `true` | 提权时 `true` |
| 提权 | **需要管理员 / Hyper-V 管理员** | 不需要 | 需要 |
| 多实例 | ✅ 无许可门槛 | ⚠️ 受许可策略限制（`CO_E_APPSINGLEUSE`） | ✅ 无许可门槛 |
| CPU 核数 | ✅ `Processor.Count` | ✖ | ✅ `Processor.Count` |
| 自定义用户数据位置 | ✅ `Scsi.Attachments[].Path` | ✖（仅大小） | ✅ 每实例 scratch 目录 |
| 基础镜像层 | ✅ `VirtualSmb.Shares` / `RunInSilo` | ✖ | ✅ 可配置只读镜像层（`WindowsContainer:BaseImageLayerPath`） |
| 检查点 | ✅ `HcsSaveComputeSystem` | ✖ | ✖ |
| 运行期热映射目录 | ✖（创建时映射） | ✅ `MapFolder` | ✖（创建时 `MappedDirectories`） |
| in-guest 进程执行 | ✖ | ✅ `CreateProcess` | ✅ `HcsCreateProcess` |
| 获取 IP | ✖（由 HNS 维护） | ✅ `GetIpAddress` | ✅ HNS（namespace + NAT endpoint） |

引擎在提权时**自动选用 HCS**（`HcsSandboxEngine.IsAvailable == _elevation.IsElevated`），否则回退
Windows Sandbox gRPC。第三个引擎 `WindowsContainerEngine` 是 **HCS 进程隔离容器（Argon）**：
它把配置的只读镜像层与每实例可写 scratch 层经 WCIFS 叠加成容器 `C:\`
（`CreateSandboxLayer`/`ActivateLayer`/`PrepareLayer`/`GetLayerMountPath`），并通过 HNS
**namespace** 接入 NAT 网络。镜像层默认取最新 Windows Sandbox 镜像层，但进程隔离要求镜像与宿主
build 一致（Windows Server 2022/2025 镜像例外），故通常需先 `wsm image import` 一个服务器镜像并设
`WSM_CONTAINER_BASE_IMAGE`。它同样需要提权（`IsAvailable == _elevation.IsElevated`），按
`SandboxEngineKind` 升序排在 HCS 之后。`wsm env` 会显示当前所选引擎与能力。

---

## 架构（ABP 模块化 · 一模块一程序集）

仓库为 **Volo.Abp.Core 模块化单体**：每个程序集恰好一个 `AbpModule` 派生类，公共契约 `public`、
实现 `internal`；`.slnx` 用**解决方案文件夹**按职责分层（组织用途）。共 **28 个程序集**
（全部已实现；P5 的 4 个预留空壳已在第三个引擎落地时补齐）。命名空间根 **== 程序集名**（`Wsm.*`）；旧的
`WindowsSandboxManager.*` 命名空间已全部消除。

```
┌────────────────────────────────────────────────────────────────────────┐
│ 表示层 Presentation  /src/50-App/                                       │
│   Wsm.Cli (CliModule, `wsm`)      Wsm.Ui.WinUI (WinUiModule, .resw/x:Uid)│
└──────────────┬────────────────────────────────┬────────────────────────┘
               │ ISandboxManager                │ IAppLocalizer / ResourceLoader
┌──────────────▼────────────────────────────────▼────────────────────────┐
│ 应用层 Application  /src/00-Core/                                       │
│   Wsm.Application (SandboxApplicationModule → ISandboxManager)         │
│   Wsm.Application.EngineSelection (EngineSelectionModule → 默认 HCS)    │
└──────────────┬──────────────────────────────────────────────────────────┘
               │ ISandboxEngine / ISandboxEngineRegistry
┌──────────────▼──────────────────────────────────────────────────────────┐
│ 引擎层 Engines  /src/20-Engines/                                        │
│   Wsm.Engine.Hcs (核心/默认)   Wsm.Engine.WindowsSandbox (核心/gRPC)     │
│   Wsm.Engine.WindowsContainer (进程隔离容器 / process-isolated)          │
└──────────────┬──────────────────────────────────────────────────────────┘
               │ 能力模块（一模块一程序集）
┌──────────────▼──────────────────────────────────────────────────────────┐
│ /src/10-Hcs/         Wsm.Hcs.Interop / Layout / Json.Sandbox /          │
│                      Json.Container / Storage / Checkpoint /            │
│                      Process / Network                                  │
│ /src/30-Connections/ Wsm.SandboxServer.Client / Wsm.Recipe.Serialization│
│                      Wsm.Connection.RdpBase / Session / Legacy          │
│ /src/40-Host/        Wsm.Persistence / Wsm.Host.Environment / Elevation │
│                      / Policy / Wsm.Localization                        │
└──────────────┬──────────────────────────────────────────────────────────┘
               │
┌──────────────▼──────────────────────────────────────────────────────────┐
│ 领域层 Domain  /src/00-Core/Wsm.Domain (SandboxDomainModule)            │
│   SandboxInstance(聚合根) · SandboxRecipe · StorageOptions ·            │
│   Checkpoint · 值对象 · 领域事件                                        │
└──────────────────────────────────────────────────────────────────────────┘
```

---

## 构建与运行

需要 **.NET 10 SDK**、Windows 11（已启用 Windows Sandbox 可选功能）。
HCS 后端需要以管理员运行。

```powershell
dotnet build WindowsSandboxManager.slnx
dotnet test  WindowsSandboxManager.slnx

# CLI
dotnet run --project src/50-App/Wsm.Cli -- env

# WinUI3 GUI（HCS 需要管理员，可右键“以管理员身份运行”）
dotnet run --project src/50-App/Wsm.Ui.WinUI
```

---

## CLI（`wsm`）用法

```text
wsm env                        显示所选引擎、能力、提权状态与多实例策略
wsm list / running             列出实例 / 运行中实例
wsm create [options]           创建实例定义
wsm show <id>                  查看实例
wsm start <id> [--remoteapp]   启动
wsm stop <id>                  停止
wsm connect <id> [--remoteapp] 连接 RDP 会话
wsm ip <id>                    获取 guest IP（Windows Sandbox gRPC 引擎）
wsm exec <id> <command>        在沙箱内执行命令（gRPC 引擎）
wsm share <id> <host> [sbx] [--rw]   运行期热映射目录（gRPC 引擎）
wsm checkpoint save <id> <name> [--template]  保存 HCS 检查点
wsm checkpoint list <id>                      列出检查点
wsm checkpoint restore <id> <checkpointId>    恢复检查点
wsm multi [--enable|--disable] 查看/切换多实例许可策略（gRPC 引擎用）
wsm image import <ref> <path> 导入容器镜像的基础层，供「进程隔离」引擎使用
                              （默认 mcr.microsoft.com/windows/servercore:ltsc2022）
wsm elevate                    提权重启（启用 HCS 引擎）
wsm help                       显示帮助

create 选项：
  --name <名称>
  --memory <MB>                默认 4096
  --cpu <核数>                 HCS 引擎
  --networking enable|disable|default
  --vgpu enable|disable|default
  --rail                       以 RemoteApp(RAIL) 启动
  --fullscreen / --protected
  --storage-path <目录>        自定义用户数据位置（HCS 引擎）
  --disk-size <MB>             用户数据盘大小
  --map "C:\host;C:\guest;rw"
  --logon "cmd.exe /c echo hi"
```

> 旧 CLI 的 `wsm hcs` / `wsm layers` 诊断命令依赖已在 P8b 删除的旧
> `Infrastructure/Hcs`，**未迁移**；层/存储布局现由 `Wsm.Hcs.Layout`、`Wsm.Hcs.Storage` 模块承担，
> 不再作为独立 CLI 命令暴露。

示例（HCS，需管理员）：

```powershell
wsm elevate
$id = (wsm create --name Demo --cpu 8 --memory 8192 --storage-path E:\sbx --networking enable)
wsm start $id
wsm checkpoint save $id snap1 --template
wsm checkpoint list $id
wsm stop $id
```

### 进程隔离（Windows 容器）快速上手

进程隔离要求镜像与宿主 build 一致；Windows 11 宿主可运行 **Windows Server 2022/2025** 镜像
（允许 build 不一致）。**VM 隔离（HCS / Windows Sandbox）始终可用**；仅当镜像满足兼容规则时，
进程隔离引擎才会被列出为可用（否则在下拉框里显示为不可用，选择器自动回退到 VM 引擎）。

在图形界面里：**设置 → 进程隔离镜像**，选择 `Windows Server 2022/2025` 镜像并点击「下载镜像」
（需管理员）。下载完成后立即生效：状态行显示「进程隔离：可用」，创建对话框的引擎下拉框随之可选。

命令行等价流程：

```powershell
wsm elevate
wsm image import mcr.microsoft.com/windows/servercore:ltsc2022 C:\wsm-images\servercore-ltsc2022
$env:WSM_CONTAINER_BASE_IMAGE = 'C:\wsm-images\servercore-ltsc2022'
$id = (wsm create --name ctr --engine container --memory 4096 --cpu 2 --networking enable)
wsm host $id                       # 启动并保持（另开一个提权窗口操作）
wsm exec $id cmd.exe /c ver        # 在容器内执行命令
wsm ip $id                         # 容器在 NAT 网络上的地址
wsm stop $id
```

> 也可用 `WindowsContainer:BaseImageLayerPath` 配置项（等价于该环境变量）。
> `wsm env` 会列出每个引擎的可用性。

### 桌面与 mstsc（用 VM 隔离）

**容器（进程隔离**或** Hyper-V 隔离）不支持图形界面与 RDP 交互会话**——这是 Microsoft 的设计
（见 [Lift and shift to containers](https://learn.microsoft.com/virtualization/windowscontainers/quick-start/lift-shift-to-containers)：
“Containers don't support GUI”、“An application using RDP can't be containerized as-is”；
即便是 “Desktop Experience” 的 `windows/server` 镜像，官方也明确 **没有 GUI**）。
实测：WS2022 容器内 `TermService` 能启动、`WinStations\RDP-Tcp` 配置存在，但**永远不监听 3389**，
因此 `mstsc` 无法连入容器。

**要进入桌面并用 `mstsc` 连接，必须用 VM/Hyper-V 隔离**（Windows Sandbox / HCS 引擎，不是容器）。
该引擎默认走命名管道（应用内 `RDPBASE` + `mstscax` 呈现桌面）；要改用标准 `mstsc`，选择
**TCP 传输**，guuest 会在 3389 上监听：

```powershell
wsm elevate
$id = (wsm create --name desk --networking enable --transport tcp --memory 4096 --cpu 2)
wsm host $id            # 启动并保持（另开一个提权窗口）
wsm ip $id              # 例如 172.21.83.79
mstsc /v:172.21.83.79   # 用户名 WDAGUtilityAccount；密码为确定性口令 Wsm!<SHA256(id) 前 10 字节>
```

- `--transport tcp` 需要 `--networking enable`（guest 通过 ICS “Default Switch” 获取 DHCP 地址）。
- 座席口令由引擎按实例 id 确定性派生（`Wsm!` + `SHA256(id)` 前 10 字节大写十六进制），
  `wsm start/host` 会打印 `user=…`。
- GUI 里在**实例设置 → RDP 传输**中选择 TCP 即可。

> 结论：**桌面 → VM 隔离（随时可用）；进程隔离 → 无头负载（更省资源）**。两者按同一 recipe 选择，
> 选择器会在进程隔离不可用（镜像不兼容）时自动回退到 VM 引擎。

---

## 三项能力（HCS 直连）

| 问题 | 结论 | 关键 HCS 字段 / API |
| --- | --- | --- |
| 自定义基础镜像层位置 | **允许**（HCS 层） | `VirtualSmb.Shares[0].Path`、`RunInSilo.SiloBaseOsPath`；`Layer.Path`；`vmcompute!CreateBaseImageVHD/CreateLayer/ImportLayer` |
| 自定义用户数据位置 | **允许**（位置仅 HCS） | `Scsi.primary.Attachments["0"].Path`；`CmsCreateStorage`/`CmsMapVirtualDiskToContainer` |
| 检查点 | **允许** | `HcsSaveComputeSystem` + `SaveOptions{ToFile\|AsTemplate}` + `RestoreState`；落盘 `Snapshots\<id>\SnapshotSavedState.vmrs` |

引擎实现（`/src/10-Hcs/` 能力模块）：

- 基础镜像层：`IHcsHostLayout.ResolveBaseLayerPath()`（`Wsm.Hcs.Layout`）从
  `HKLM\...\Containers\CmService\Layers` 解析最新可用层（本机实测解析到 `Layers\<id>\Files`）。
- 用户数据：`SandboxRecipe.Storage`（`SandboxStorageOptions`）→ `ISandboxStorageProvisioner.Provision()`
  （`Wsm.Hcs.Storage`）在自定义目录准备 `sandbox.vhdx`/`sandbox.vmgs`。
- 检查点：`ICheckpointService`（`Wsm.Hcs.Checkpoint`）保存/枚举/恢复；`SaveType=AsTemplate` 对应
  Windows Sandbox 内部用于快速重启的模板。

---

## 多实例

- **HCS 后端**：直接创建多个 compute system，**无许可门槛**（WDAG 当年即如此）。
- **gRPC 后端**：零售版受 `AreMultipleInstancesAllowed` 限制；可用 `wsm multi --enable`（管理员，机器级、可回滚）
  使服务器放行，或改用 HCS 后端。

引擎在应用层主动做单实例守卫，给出清晰错误，不产生“幽灵”实例。

---

## 已知限制

- HCS 需要提权；未提权时自动回退 gRPC。
- **裸 HCS 从零创建可启动的 Sandbox 属实验性**：需已存在基础层与用户数据盘
  （运行一次 Windows Sandbox 或由既有 `ContainerStorages` 播种即可）。
- HCS 后端不支持运行期热映射目录与 in-guest 进程执行（能力矩阵已如实声明）。
- **容器引擎（进程隔离，`WindowsContainer`）**：基于 HCS *Argon* 容器；`C:\`
  由只读镜像层 + 每实例可写 **scratch 层**经 WCIFS 叠加而成。引擎会
  `CreateSandboxLayer → ActivateLayer → PrepareLayer → GetLayerMountPath`
  取得 scratch 卷，并通过 HNS **namespace**（`Container.Networking.Namespace`）
  接入 NAT 网络。已实测：用 Windows Server 2022 镜像可正常 create/start/exec/取 IP/stop。
  - **镜像要求**：进程隔离要求镜像与宿主 **build 一致**；例外是 Windows 11 宿主可运行
    **Windows Server 2022/2025** 镜像（允许 build 不一致）。Windows Sandbox 自带的是
    **客户端** 镜像，功能更新后可能落后于宿主（本例宿主 26H2/26300、镜像 24H2/26100），
    此时 HCS 会以 `HCS_E_IMAGE_MISMATCH (0x80370101)` 拒绝。请先
    `wsm image import mcr.microsoft.com/windows/servercore:ltsc2022 <path>`，
    再设 `WSM_CONTAINER_BASE_IMAGE`（或 `WindowsContainer:BaseImageLayerPath`）指向该层。
  - **无图形桌面**：进程隔离容器（含 ServerCore）没有可交互的会话与 RDP 监听
    （`WinStations\RDP-Tcp` 缺失），因此**不能**用 `mstsc` 进入图形桌面；需要图形桌面请使用
    Windows Sandbox/HCS 全 VM 引擎。该容器适合无头工作负载（可用 `wsm exec` 执行命令）。
- `HCS` 的 guest IP 由 HNS 维护，未从 HCS 属性直接暴露。
- `WindowsSandboxServer` gRPC 契约与 `RDPBASE`/`computecore`/`cmclient` 接口属**非公开**接口，可能随版本变化。
- `ISandboxEnvironment.PackageInstallPath` 在非提权下可能为 `null`。
- WinUI3 项目需 `x64`。

---

## 许可

示例代码仅供学习与研究使用。所依赖的非公开接口归 Microsoft 所有。
