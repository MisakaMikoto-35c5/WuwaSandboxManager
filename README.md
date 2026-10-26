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
实现 `internal`；`.slnx` 用**解决方案文件夹**按职责分层（组织用途）。共 **32 个程序集**
（全部已实现；P5 的 4 个预留空壳已在第三个引擎落地时补齐）。命名空间根 **== 程序集名**（`Wsm.*`）；旧的
`WindowsSandboxManager.*` 命名空间已全部消除。

```
┌────────────────────────────────────────────────────────────────────────┐
│ 表示层 Presentation  /src/50-App/                                       │
│   Wsm.Cli (CliModule, `wsm`)   Wsm.Ui.WinUI (WinUiModule, .resw/x:Uid)  │
│   Wsm.Rdp.WinUI (WinUI3 连接器 / connector process)                     │
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
│                      Wsm.Connection.RdpHost (契约) / .Ax (mstscax) /    │
│                      .Wpf / .WinUi (可选择的连接器模块 / selectable)     │
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

## RDP 连接器（可选择的 WPF / WinUI 3 重写）

会话呈现由**可选择的连接器模块**承担，二者实现同一契约、互不影响：

| 模块 | 技术 | 进程模型 |
| --- | --- | --- |
| `Wsm.Connection.RdpHost.Wpf`（默认） | WPF XAML 窗口 + `WindowsFormsHost` 承载 `mstscax` | 在承载进程内（`wsm connect`） |
| `Wsm.Connection.RdpHost.WinUi` + `Wsm.Rdp.WinUI` | WinUI 3 窗口，`mstscax` 以子 HWND 承载 | 独立连接器进程（参考 Windows Sandbox 的 WinUI 应用） |

- 选择方式：`ui-settings.json` 的 `RdpConnector` 字段（GUI「设置」对话框可选），或 `WSM_RDP_CONNECTOR` 环境
  变量覆盖（`wpf` / `winui`）；缺省 WPF。`Wsm.Connection.RdpHost` 只保留契约与选择器
  （`IRdpSessionHost`/`IRdpConnectorHost`/`IRdpSandboxControl`），`Wsm.Connection.RdpHost.Ax` 共享
  `mstscax` 互操作与 WinForms 宿主控件。
- **快捷控制按钮**：发送 Ctrl+Alt+Del（`IMsRdpClientNonScriptable.SendKeys`）、重启、正常关机、强制关机、开机。
  生命周期均为 VM 级：重启 = 先 `StopAsync` 再 `StartAsync`；关机 / 强制关机 = `StopAsync`（引擎自身会从
  ACPI 优雅关机升级为 terminate）；开机 = `StartAsync`。操作经 `IRdpSandboxControl`（应用层）执行。
- **全屏模式**：工具栏切换（WPF 为无边框最大化 + F11/Esc；WinUI 3 用 `AppWindow` 的 `FullScreenPresenter`）。
- **窗口缩放**：RDP 表面随窗口自适应（控制填充 + `SmartSizing`，并在缩放后同步 `DesktopWidth/Height`），修复了
  之前改变窗口大小内容尺寸不变的问题。

> 注意：`mstscax` 是 ActiveX 控件，WinUI 3 **没有**原生的 ActiveX 承载能力；WinUI 3 连接器把
> `mstscax`（经 `Wsm.Connection.RdpHost.Ax` 的 WinForms 控件）作为子 HWND 挂到 WinUI 窗口上。这依赖
> 非官方支持的 HWND 承载（airspace：子窗口始终覆盖其所在区域），因此生命周期/确认对话框会先隐藏该子窗口。

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
wsm image import <ref> [path] 导入容器镜像的基础层，供「进程隔离」引擎使用
                              （path 可选；缺省落到 %LOCALAPPDATA%\WindowsSandboxManager\images\<slug>，
                              与 GUI 下载共用同一位置；默认 ref 为 mcr.microsoft.com/windows/servercore:ltsc2022）
wsm image session <path>      一条命令的桌面会话解锁：console 会话补丁 + 自动登录 +
                              wsm-shell（读侧作 Winlogon\Shell）（--user / --password / --no-mirror）
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

在图形界面里：**设置 → 进程隔离镜像**，从目录中选择镜像并点击「下载镜像」（需管理员）。
目录现含 4 项：`Windows Server 2025/2022 (full)`（约 5.5/5 GB）与 `Server Core 2025/2022`
（各约 1.5 GB），也可直接键入自定义 reference。下载有字节/速度/ETA 进度与取消按钮；
已安装的镜像显示「已安装」（可重新下载/删除），关闭对话框下载不中断。镜像默认下载到
`%LOCALAPPDATA%\WindowsSandboxManager\images\<slug>`——GUI 与 CLI 共用同一位置。
已有镜像可直接点「使用此镜像」而不必重新下载；点「准备桌面镜像」会写入交互登录和读侧
（先停止使用该镜像的容器）。镜像路径也保存进每个实例的 `ContainerImagePath`，所以 agent
已运行时，新实例仍使用你选择的镜像。下载完成后状态行显示「进程隔离：可用」，引擎下拉框随之可选。

命令行等价流程（`layerPath` 可省略，缺省同上默认根）：

```powershell
wsm elevate
wsm image import mcr.microsoft.com/windows/servercore:ltsc2022
$env:WSM_CONTAINER_BASE_IMAGE = "$env:LOCALAPPDATA\WindowsSandboxManager\images\mcr.microsoft.com-windows-servercore-ltsc2022"
$id = (wsm create --name ctr --engine container --memory 4096 --cpu 2 --networking enable)
wsm host $id                       # 启动并保持（另开一个提权窗口操作）
wsm exec $id cmd.exe /c ver        # 在容器内执行命令
wsm ip $id                         # 容器在 NAT 网络上的地址
wsm stop $id
```

> 也可用 `WindowsContainer:BaseImageLayerPath` 配置项（等价于该环境变量）。
> `wsm env` 会列出每个引擎的可用性。

### 容器的交互式会话（进程隔离）

Microsoft 的容器镜像把**交互式会话关掉了**：镜像当前控制集里的
`Session Manager\NumberOfInitialSessions = 1`（姊妹控制集仍是默认的 `2`），于是 `smss` 只创建
services 会话，`winlogon.exe` —— 以及 `LogonUI`、登录桌面、shell 链 —— **从不启动**。
把它改成 `2` 后重建容器，console 会话与登录链就会真的起来（已复现多次）：

```powershell
wsm elevate
# 对已导入的镜像打补丁（也可在导入时加尾部 --desktop）
wsm image desktop "$env:LOCALAPPDATA\WindowsSandboxManager\images\mcr.microsoft.com-windows-servercore-ltsc2022"

$env:WSM_CONTAINER_BASE_IMAGE = "$env:LOCALAPPDATA\WindowsSandboxManager\images\mcr.microsoft.com-windows-servercore-ltsc2022"
$id = (wsm create --name desk --engine container --memory 4096 --cpu 2 --networking enable)
wsm host $id
wsm exec $id cmd.exe /c qwinsta     # 应能看到  console  <id>  Conn
```

要的不只是 console 会话、而是**完整的桌面会话**时，用一条命令代替：`wsm image session <layerPath>
[--user <u>] [--password <p>] [--no-mirror]` —— 它在 console 补丁之外还把自动登录
（`AutoAdminLogon`/`DefaultPassword` 等）与 `Winlogon\Shell = C:\wsm\wsm-shell.cmd`（读侧以重启
循环常驻，作为会话的生命期）一起烘进镜像；引擎启动容器时会用 `net user` 把运行时密码设成与烘入值
一致（默认 `Administrator` / `Wsm!Container2025`），并写入 `AutoLogonCount=99`。
**注意：镜像准备已实测可用，但本机容器内桌面端到端尚未打通**（交互登录与首次重启仍受
宿主 HCS/会话问题阻塞，见
[docs/investigation/13-container-desktop-and-rdp.md](docs/investigation/13-container-desktop-and-rdp.md) §26）。

> **注意：这不能让你"远程进桌面"。** 进程隔离容器里 RDP 监听器被 `termsrv` 主动拒绝
> （`WTSIsServerContainer` → “TS in container and so Remote Connection is not allow”），
> 已逐一试过并全部失败的开关包括 `ContainerMode`、`AllowListenersInsideArgonContainer`
> （5 个位置 + 环境变量）、`EditionID=ContainerOSPlus`、RDP 策略项等。想看画面需要自建
> **会话内截屏/输入代理**，或改用 Hyper-V 隔离。完整证据见
> [docs/investigation/13-container-desktop-and-rdp.md](docs/investigation/13-container-desktop-and-rdp.md)。
>
> 后续实测（同文档 §10）把「外面能不能进去」测到底：console 会话**确实会建立**
> （winlogon + LogonUI + TSDDD 1024×768），但**外部进不去**——跨会话建进程被容器 silo
> 拒绝（即使以 SYSTEM 运行），自动登录虽被仲裁但没有完成，且会话在**开机后约 121 秒**
> 被注销。**真正能用的是容器自己会话的桌面**：可绘制、可截屏（同文档 §10.5 有一张
> 真实「注册表编辑器」的截图），工具见 `tools/container-desktop-agent/`。

容器桌面的引擎全局选项（默认关闭）：`WindowsContainer:Desktop`
（`Enabled` / `UserName` / `Password` / `Shell` / `AttemptTcpRdp`），也可用环境变量
`WSM_CONTAINER_DESKTOP=1` 打开。GUI 选择桌面时则把 `Desktop.InteractiveSession=true`
写进该实例清单；默认 `false`，不改变无头容器的启动和账号行为。Server Core 镜像没有
`explorer.exe`，所以默认 shell 是持久运行的 `C:\wsm\wsm-shell.cmd`，而不是会立即退出的 `cmd.exe`。
`AttemptTcpRdp` 是**实验性且已知无效**的 RDP 尝试，保留原因见上面的文档。

### 容器桌面通道（进程隔离；可替换的 provider，由清单选择）

进程隔离的容器没有 RDP，容器窗口也不能跨 silo 搬进你的会话（实测见同文档 §16/§17）。所以桌面是一条
**像素通道**，由 **provider** 实现，而**用哪一个由沙箱清单决定**：

* `frame-buffer`（默认）：容器内 agent 抓整屏写进共享缓冲，宿主用一个窗口显示（可缩放窗口 +
  letterbox 等比缩放与坐标逆变换；wheel/双击/剪贴板发送；**F10** 循环发起动态分辨率请求——agent 经
  `ChangeDisplaySettingsEx` 尝试、失败优雅拒绝，TSDDD 可能拒绝，实验性）；不需要改镜像。agent 生命期
  由**查看器锁**驱动（`viewers\<guid>.lock`，60 秒不续判 stale，无查看器 30 秒退出，900 秒兜底）。
* `window-mirror`（镜像协议 **v2**）：宿主通过 **HCS** 在容器会话里启动读侧；读侧**逐窗口**抓取写进
  共享内存，并对 shadow buffer 做 8×8 块差分，只发布**脏矩形**；宿主为**每个容器窗口开一个真实窗口**
  （`FormBorderStyle.Sizable`——真标题栏/最小化/最大化/关闭，出现在任务栏与 Alt-Tab），查看器只重绘脏区。
  **拖动宿主窗口就是真的移动容器里的窗口**（标题栏拖动 → `movewindow`；边框拖动 → `resize`；关闭按钮
  → `close`），输入为结构化命令 `key <vk> down|up` / `text`（SendInput + `KEYEVENTF_UNICODE`）/ `wheel` /
  双击。读侧还有 **`launch <exe> [args]`**：在它自己所在的会话里启动 GUI 程序——这是绕过跨会话 silo
  墙的机制（不需要跨会话建进程）。

共享缓冲有两种模式，由 `shm-mode.txt` 协商：宿主提权**且**查看器持有同一令牌时用 **pagefile**
（`.shm` 只是 4 KB 的 header stub，像素在 page-file section 里，宿主侧经句柄穿透
`SectionPassthrough` 直接映射，省掉文件这一跳）；否则回退 **file**。已知限制：由 SYSTEM 承载的 GUI
拉起的查看器（UAC 过滤后的交互用户令牌）拿不到穿透句柄，自动落回 file 模式。

清单里选（`SandboxRecipe.Desktop` → `SandboxDesktopPlan`）：

```jsonc
{ "desktop": { "kind": "WindowMirror",   // 或 "FrameBuffer" / "None"
               "interactiveSession": true, // 请求镜像内的交互登录；GUI 自动设置
               "provider": null,         // 也可直接写 provider id（优先于 kind）
               "presentToInteractiveSession": true } }
```

```powershell
wsm desktop <id> [--provider <id>] [--mode <Kind>] [--present|--no-present] [--input "keys F3"]
wsm desktop --list-providers                     # 本宿主注册了哪些 provider
wsm desktop --compute-system <id> --container-root \\?\Volume{GUID} --provider window-mirror
wsm image mirror <layerPath> [--shell cmd.exe] [--logon-user <u>] [--logon-password <p>]
wsm image session <layerPath> [--user <u>] [--password <p>] [--no-mirror]
wsm desktop-view --mode windows --frame <mirror.shm> [--offset-x N --offset-y N]
```

> `wsm image mirror` 把读侧与登录钩子（`Winlogon\Userinit`）烘进镜像，供"GUI 跑在交互会话"的容器使用；
> 默认路径不需要它——HCS 会把读侧直接启动到容器会话里。`wsm image session` 是更进一步的**一条命令
> 桌面会话解锁**（见上一节）。
>
> GUI 里：运行中的 WindowsContainer 实例的详情页有「Container desktop」区块，「桌面」/「窗口(RAIL)」
> 按钮分别以 `--mode frame|windows` 拉起查看器；创建对话框在引擎选 WindowsContainer 时也可直接选
> 桌面 kind 并持久化到清单。

实测（同文档 §20）：宿主上出现镜像窗口 `rect=(500,320 480x300)`；拖动 (+90,+60) 后被镜像的窗口变成
`rect=(170,140)`，读侧日志 `movewindow 0x15198C -> (170,140) ok=True`。**注意**：以上是宿主侧回归；
解锁链路（`wsm image session` + `launch`）的容器内端到端实测**尚未完成**，待验清单见同文档 §25.7。

### 桌面与 mstsc（用 VM 隔离）

**容器（进程隔离**或** Hyper-V 隔离）不支持图形界面与 RDP 交互会话**——这是 Microsoft 的设计
（见 [Lift and shift to containers](https://learn.microsoft.com/virtualization/windowscontainers/quick-start/lift-shift-to-containers)：
“Containers don't support GUI”、“An application using RDP can't be containerized as-is”；
即便是 “Desktop Experience” 的 `windows/server` 镜像，官方也明确 **没有 GUI**）。
实测：WS2022 容器内 `TermService` 能启动、`WinStations\RDP-Tcp` 配置存在，但**永远不监听 3389**，
因此 `mstsc` 无法连入容器。（容器内的**交互式会话本身是可以打开的**，见上一节；打不开的是
RDP 这条通道——`termsrv` 在容器里主动拒绝创建监听器。）

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
    `wsm image import mcr.microsoft.com/windows/servercore:ltsc2022`（path 可省略，缺省落到
    `%LOCALAPPDATA%\WindowsSandboxManager\images\<slug>`），再设 `WSM_CONTAINER_BASE_IMAGE`
    （或 `WindowsContainer:BaseImageLayerPath`）指向该层。
  - **图形桌面（宿主侧已实现；本机端到端未打通）**：进程隔离容器**没有 RDP 监听**
    （`termsrv` 在容器里主动拒绝创建监听器），因此**不能**用 `mstsc` 进入图形桌面。镜像准备、
    镜像选择、清单（`Kind` / `InteractiveSession` / `ContainerImagePath`）以及宿主侧
    frame-buffer / window-mirror provider 均已实测；但本机交互登录和首次容器重启仍受
    HCS/会话问题阻塞，**尚未验证容器内像素、输入与 RAIL 窗口**。详见
    [docs/investigation/13-container-desktop-and-rdp.md](docs/investigation/13-container-desktop-and-rdp.md) §26。
    需要成熟图形桌面请使用 Windows Sandbox/HCS 全 VM 引擎。
  - **pagefile 共享模式的限制**：`shm-mode.txt` 协商为 `pagefile` 时宿主侧靠句柄穿透直接映射像素
    section；由 SYSTEM 承载的 GUI 拉起的查看器（UAC 过滤后的交互用户令牌）穿透不了，自动回退
    `file` 模式（功能相同，多一次文件拷贝）。
- `HCS` 的 guest IP 由 HNS 维护，未从 HCS 属性直接暴露。
- `WindowsSandboxServer` gRPC 契约与 `RDPBASE`/`computecore`/`cmclient` 接口属**非公开**接口，可能随版本变化。
- `ISandboxEnvironment.PackageInstallPath` 在非提权下可能为 `null`。
- WinUI3 项目需 `x64`。
- **WinUI 3 RDP 连接器依赖非官方支持的 HWND 承载**：`mstscax` 子窗口始终覆盖其所在区域（airspace），
  因此工具栏以外的浮层（对话框）需先隐藏该子窗口。
- 连接器工具栏的生命周期按钮经进程内 `ISandboxManager` 执行；在代理服务模式下由连接器进程内引擎直接操作
  （HCS compute system 以名称定位，可跨进程停止/启动），尚未改为经代理 gRPC 转发。

---

## 许可

示例代码仅供学习与研究使用。所依赖的非公开接口归 Microsoft 所有。
