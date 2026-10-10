# 01 · Windows Sandbox 总览 / Windows Sandbox Overview

## 1. 定义与定位 / Definition and positioning

**中文**：Windows Sandbox（WSB）是 Windows 10/11 专业版及以上提供的**一次性、隔离的桌面环境**，
用于安全地运行不受信任的应用。它在底层是一个 **Hyper-V 容器（内部类型 DisposableVM）**，
而不是普通虚拟机：没有独立内核镜像启动盘，操作系统通过容器层 + vSMB 提供。

**English**: Windows Sandbox (WSB) is a **disposable, isolated desktop environment** available on
Windows 10/11 Pro and above for safely running untrusted applications. Internally it is a **Hyper-V
container (type DisposableVM)**, not an ordinary VM: there is no standalone OS boot disk; the OS is
provided through a container layer plus vSMB.

## 2. 血统 / Lineage

**中文**：WSB 与已废弃的 **Windows Defender Application Guard（内部代号 Madrid）** 同源，共用
`cmclient.dll`（`Cms*` API）、`hvsirdpclient.exe`、`madrid.dll`、`vmcompute.dll` 等组件。WDAG 当年
能同时运行 Edge 与 Office 两个隔离实例，说明底层本就支持多实例。

**English**: WSB shares its lineage with the deprecated **Windows Defender Application Guard
(codename Madrid)**, reusing `cmclient.dll` (the `Cms*` API), `hvsirdpclient.exe`, `madrid.dll`,
`vmcompute.dll`, etc. WDAG could run Edge and Office in two isolated instances at once, showing the
underlying platform always supported multiple instances.

## 3. 进程与组件 / Processes and components

| Item | 中文 | English |
|---|---|---|
| `CmService` (`CmService.dll`) | 容器管理器服务：准备镜像层、挂载卷、生成 HCS JSON | Container Manager Service: prepares image layers, mounts volumes, builds the HCS JSON |
| `cmclient.dll` | `Cms*` 客户端 API（创建容器/存储/映射/进程） | `Cms*` client API (container/storage/mapping/process) |
| `cmimageworker.exe` | 镜像层展开/部署/清理工作进程 | Image-layer expand/deploy/clean worker |
| `vmcompute.exe` / `vmcompute.dll` | Host Compute Service，编排所有 compute system | Host Compute Service, orchestrates all compute systems |
| `computecore.dll` | HCS 客户端库（operation 模型） | HCS client library (operation model) |
| `vmwp.exe` | 每个容器的 VM worker（含 `vmusrv` vSMB 服务） | Per-container VM worker (hosts the `vmusrv` vSMB server) |
| `WindowsSandbox.exe` | 传统全桌面客户端（含单实例互斥 `WindowsSandboxMutex`） | Legacy full-desktop client (singleton mutex `WindowsSandboxMutex`) |
| `WindowsSandboxServer.exe` | 现代控制面服务器（托管 gRPC） | Modern control-plane server (hosts gRPC) |
| `WindowsSandboxRemoteSession.exe` | RDP 客户端，`--id <sandboxId>` 连接指定实例 | RDP client, `--id <sandboxId>` connects to a specific instance |
| `wsb.exe` / `wsb.dll` | CLI 与 WinUI 应用（共用 `SandboxCommon`） | CLI and WinUI app (share `SandboxCommon`) |
| `SandboxCommon.dll` | 共享库：`GrpcClient`、`WsbRecipe`、`RdpClientConfig` | Shared library: `GrpcClient`, `WsbRecipe`, `RdpClientConfig` |
| `SandboxGrpc.dll` | gRPC 契约（`sandboxserver.SandboxCore`） | gRPC contract (`sandboxserver.SandboxCore`) |
| `SandboxUDK.dll` | UDK 投影：`ManagedWindowsVM`/`VMOptions`/`VMStorage` | UDK projection: `ManagedWindowsVM`/`VMOptions`/`VMStorage` |

**中文**：启动一个沙箱后可见进程：`WindowsSandboxServer`、`ManagedWindowsVM`、`vmcompute`、`vmwp`。
**English**: After starting a sandbox you can observe `WindowsSandboxServer`, `ManagedWindowsVM`,
`vmcompute` and `vmwp`.

## 4. 控制面 A：传统 `.wsb` + `WindowsSandbox.exe` / Control plane A: legacy `.wsb`

**中文**：配置文件是 XML（根 `<Configuration>`，扩展名 `.wsb`）。`WindowsSandbox.exe <file.wsb>`
启动一个**全桌面**会话，并由互斥体保证**同一时刻仅一个实例**。适合「常规 Sandbox 方式连接」。
**English**: The configuration file is XML (root `<Configuration>`, extension `.wsb`).
`WindowsSandbox.exe <file.wsb>` starts a **full-desktop** session and a mutex enforces a **single
instance at a time**. This is the "classic" connection.

## 5. 控制面 B：`WindowsSandboxServer` gRPC / Control plane B: gRPC

**中文**：`WindowsSandboxServer.exe` 通过**命名管道** `\\.\pipe\wsandbox\<MD5(SID)>` 暴露 gRPC 服务
`sandboxserver.SandboxCore`。这是 `wsb.exe` 与新版 WinUI「Windows Sandbox」应用使用的接口，也是
`ManagedWindowsVM` 的上层封装。
**English**: `WindowsSandboxServer.exe` exposes the gRPC service `sandboxserver.SandboxCore` over the
**named pipe** `\\.\pipe\wsandbox\<MD5(SID)>`. This is the interface used by `wsb.exe` and the new
WinUI "Windows Sandbox" app, and the layer above `ManagedWindowsVM`.

| RPC | 中文 | English |
|---|---|---|
| `StartSandbox` | 以 recipe(JSON/XML) 启动，返回 `RdpClientConfig` | Start with a recipe, returns `RdpClientConfig` |
| `EnumerateSandboxVMs` | 列出运行中的沙箱 id | List running sandbox ids |
| `GetRdpClientConfig` | 获取某实例的 RDP 配置 | Get an instance's RDP config |
| `CreateProcess` | 在 guest 内执行进程（`RunAs: System/ExistingLogin`） | Execute a process in the guest |
| `MapFolder` | 运行期热映射目录 | Hot-map a folder at runtime |
| `ShutdownSandbox` | 停止实例 | Stop an instance |
| `GetIpAddress` | 获取 guest IP | Get the guest IP |
| `GetTerminateReason` | 等待/查询终止原因 | Wait/query termination reason |
| `WaitForReboot` | 等待重启（`RebootSupport`） | Wait for reboot (`RebootSupport`) |

**中文**：管道名 = `wsandbox` + `\` + `MD5(UTF8(SID字符串))` 转成的 GUID。
**English**: The pipe name = `wsandbox` + `\` + the GUID formed from `MD5(UTF8(SID string))`.

## 6. 配置模型 `.wsb` / Configuration model

| 元素 Element | 取值 Values | 说明 Description (中/EN) |
|---|---|---|
| `<vGPU>` | Enable/Disable/Default | 虚拟 GPU / virtualized GPU |
| `<Networking>` | Enable/Disable/Default | 网络（Hyper-V 默认交换机 NAT）/ networking |
| `<MappedFolders>` | HostFolder/SandboxFolder/ReadOnly | 目录映射 / folder mapping |
| `<LogonCommand>` | Command | 登录后执行 / post-logon command |
| `<AudioInput>` `<VideoInput>` | Enable/Disable/Default | 音视频输入 / audio & video input |
| `<ProtectedClient>` | Enable/Disable/Default | AppContainer 隔离 / AppContainer isolation |
| `<PrinterRedirection>` `<ClipboardRedirection>` `<SmartCardRedirection>` | Enable/Disable/Default | 设备重定向 / device redirection |
| `<MemoryInMB>` | 数值（<2048 提升为 2048） | 内存 / memory |

**中文**：内部类型 `WsbRecipe` 还含 `DisplayName`/`RdpTransport`/`RailMode`/`FullScreenMode`/
`RebootSupport`/`PersistenceSupport`/`AccountUser`/`AccountPassword`/`WorkingDirectory`/`TargetDisplay`。
**关键：`.wsb` 没有 CPU 核数配置**。
**English**: The internal type `WsbRecipe` also carries `DisplayName`/`RdpTransport`/`RailMode`/
`FullScreenMode`/`RebootSupport`/`PersistenceSupport`/`AccountUser`/`AccountPassword`/
`WorkingDirectory`/`TargetDisplay`. **Key point: `.wsb` has no CPU-core setting.**

## 7. 连接方式 / Connection

**中文**：默认 **RDP over Named Pipe**（`\\.\pipe\<VMId>`），也可 TCP。形态分两种：**全桌面**
（`RailModeEnabled=false`，类 Remote Desktop）与 **RemoteApp/RAIL**（`RemoteProgram3.RemoteProgramMode=true`，
独立应用窗口）。RAIL 标志在**创建时**写入 recipe，运行期不可切换。
**English**: Default is **RDP over a named pipe** (`\\.\pipe\<VMId>`), with a TCP option. Two
presentations exist: **full desktop** (`RailModeEnabled=false`, Remote-Desktop-like) and
**RemoteApp/RAIL** (`RemoteProgram3.RemoteProgramMode=true`, seamless windows). The RAIL flag is
baked in at **creation** time and cannot be switched live.

## 8. 生命周期与状态 / Lifecycle

**中文**：`Created → Starting → Running → (Rebooting) → Terminated`。终止原因
`VMTerminatedReason`：`None / UserInitiated / Crashed / UnknownFailure / UserInitiatedShutdown /
UserInitiatedShutdownForReboot`。
**English**: `Created → Starting → Running → (Rebooting) → Terminated`. Termination reasons:
`None / UserInitiated / Crashed / UnknownFailure / UserInitiatedShutdown /
UserInitiatedShutdownForReboot`.

## 9. 多实例策略 / Multi-instance policy

**中文**：官方默认**只允许一个**实例。`SandboxContracts.AreMultipleInstancesAllowed =
WVD-Enabled || 内部 rs_* 分支`；否则第二次 `StartSandbox` 返回 `CO_E_APPSINGLEUSE (0x800401F6)`。
本机实测：`WVD-Enabled=0`、`BuildBranch=ge_release` → 单实例。多实例的正道是 **HCS 直连**。
**English**: Officially **only one** instance is allowed. `AreMultipleInstancesAllowed = WVD-Enabled
|| internal rs_* branch`; otherwise the second `StartSandbox` returns `CO_E_APPSINGLEUSE
(0x800401F6)`. On this machine `WVD-Enabled=0`, `BuildBranch=ge_release` → single instance. The
proper way to run multiple is **direct HCS**.

## 10. 提权要求 / Elevation

**中文**：WSB 应用本身可**非提权**使用（由 `CmService` 服务以 SYSTEM 代劳 HCS）。但**直接调用
HCS 需要管理员 / Hyper-V 管理员**，否则返回 `0x8037011B`。
**English**: The WSB app itself runs **unelevated** (the `CmService` service performs HCS work as
SYSTEM). **Calling HCS directly requires administrator / Hyper-V Administrators**, otherwise it
returns `0x8037011B`.

## 证据 / Evidence

- 进程与二进制：`C:\Windows\System32\{WindowsSandbox,WindowsSandboxClient,vmcompute,computecore}.dll|exe`、`cmclient.dll`、`container.dll`、`CmService.dll`、`cmimageworker.exe`。
- MSIX 包：`C:\Program Files\WindowsApps\MicrosoftWindows.WindowsSandbox_0.8.107.0_x64__cw5n1h2txyewy`（含 `SandboxCommon.dll`、`SandboxGrpc.dll`、`SandboxUDK.dll`、`wsb.exe`、`WindowsSandboxServer.exe`、`WindowsSandboxRemoteSession.exe`）。
- IL 反编译：`SandboxCommon`、`SandboxGrpc`、`WindowsSandboxServer`、`wsb`、`WindowsSandboxRemoteSession`、`SandboxUDK`。
- 实测：`wsb start/list/ip/stop`、`wsm env/layers`、`hcsdiag`。
- 相关模块：[02 镜像](02-sandbox-image.md)、[03 WDAG](03-wdag.md)、[04 HCS](04-hcs.md)、[08 连接](08-connection-rdp-hvsi.md)。
