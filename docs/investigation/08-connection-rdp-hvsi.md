# 08 · 连接：RDP over HvSI / NamedPipe / Connection

## 1. 概述 / Overview

**中文**：Windows Sandbox 的会话是 **RDP**，但传输不是普通 TCP，而是 **Hyper-V Socket（HvSI）** 或
**命名管道**。客户端把 RDP 流接到 `mstscax`（Remote Desktop ActiveX）上呈现。默认走
`\\.\pipe\<VMId>`，无需 guest 网络。
**English**: A Windows Sandbox session is **RDP**, but the transport is not ordinary TCP — it is a
**Hyper-V Socket (HvSI)** or a **named pipe**. The client feeds the RDP stream into `mstscax` (the
Remote Desktop ActiveX control). By default it uses `\\.\pipe\<VMId>`, requiring no guest networking.

## 2. 传输方式 / Transports

| `RdpTransport` | 中文 | English |
|---|---|---|
| `NamedPipe`(0, 默认) | 连接 `\\.\pipe\<VMId>`，无需网络 | Connect to `\\.\pipe\<VMId>`, no networking |
| `Local`(1) | 本地回环 | Loopback / local |
| `TCP`(2) | guest IP 的 3389（需 `<Networking>`） | Guest IP:3389 (requires `<Networking>`) |

## 3. `RDPBASE.dll` 接口 / The `RDPBASE.dll` interfaces

**中文**：`RDPBASE.dll`（Remote Desktop Base）提供「把 RDP 接到任意流」的能力。关键 CLSID/IID：
**English**: `RDPBASE.dll` provides the ability to connect RDP to an arbitrary stream. Key CLSID/IID:

| 名称 Name | GUID |
|---|---|
| `CLSID_RDPRuntimeSTAContext` | `FB332AE7-0055-4208-92B7-20410CA8382B` |
| `CLSID_RDPENCNamedPipeDirectConnector` | `FB332AE7-0088-4208-92B7-20410CA8382B` |
| `IID_IRDPENCPlatformContext` | `FB332AE7-000E-4208-92B7-20410CA8382B` |
| `IID_IRDPENCNamedPipeDirectConnector` | `4ACF942D-EADC-45BF-8EA8-793FE3CE31E8` |
| `IID_IRDPENCNamedPipeDirectConnectorCallbacks` | `D923EFE9-0A6D-4344-92B3-164229DB8D2D` |

**中文**：流程：`RDPBASE_CreateInstance` 创建 `IRDPENCPlatformContext` → `InitializeInstance`；
再创建 `IRDPENCNamedPipeDirectConnector` → `InitializeInstance(callback)` → `StartConnect(pipeName)`；
回调 `OnConnectionCompleted(netStream)` 返回 `IRDPENCNetStream`（可交给 `mstscax`）。
**English**: Flow: `RDPBASE_CreateInstance` creates `IRDPENCPlatformContext` → `InitializeInstance`;
then create `IRDPENCNamedPipeDirectConnector` → `InitializeInstance(callback)` → `StartConnect(pipeName)`;
the `OnConnectionCompleted(netStream)` callback returns an `IRDPENCNetStream` (feed it to `mstscax`).

## 4. `mstscax` 连接 / `mstscax` connection

**中文**：`IMsRdpClient10.AdvancedSettings.ConnectWithEndpoint = netStream` 后 `Connect()`；用户名/密码
来自 `RdpClientConfig`；`EnableCredSspSupport`/`NegotiateSecurityLayer` 视安全策略设置。
**English**: Set `IMsRdpClient10.AdvancedSettings.ConnectWithEndpoint = netStream` then `Connect()`;
credentials come from `RdpClientConfig`; `EnableCredSspSupport`/`NegotiateSecurityLayer` depend on the
security policy.

## 5. RAIL / RemoteApp

**中文**：`RailModeEnabled=true` 时设置 `RemoteProgram3.RemoteProgramMode = true`，以**独立应用窗口**
（RAIL）呈现——这正是 WDAG「远程应用」的机制。全桌面则相反。
**English**: With `RailModeEnabled=true`, set `RemoteProgram3.RemoteProgramMode = true` to present
**individual application windows** (RAIL) — the same mechanism as WDAG's "remote app". Full desktop
is the opposite.

## 6. `WindowsSandboxRemoteSession.exe`

**中文**：官方 RDP 客户端，`--id <sandboxId>` 连接指定实例（全桌面或 RAIL，取决于该实例创建时的
`RailMode`）。它内部用 `RDPBASE` + `MSTSCLib`（`IMsRdpClient10`）。
**English**: The official RDP client; `--id <sandboxId>` connects to a specific instance (full desktop
or RAIL, depending on the instance's creation-time `RailMode`). Internally it uses `RDPBASE` +
`MSTSCLib` (`IMsRdpClient10`).

## 7. `RdpClientConfig`

**中文**：`StartSandbox`/`GetRdpClientConfig` 返回的 XML（`XmlSerializer`），含
`WindowTitle`/`RdpTransport`/`Fullscreen`/`RailModeEnabled`/`IpAddress`/`VMId`/`SandboxId`/
`AudioInputEnabled`/`VideoInputEnabled`/`PrinterRedirection`/`ClipboardRedirection`/
`SmartCardRedirection`/`Username`/`Password`。
**English**: The XML returned by `StartSandbox`/`GetRdpClientConfig` (`XmlSerializer`) with the fields
listed above.

## 8. HvSI 与 WDAG / HvSI and WDAG

**中文**：WDAG 使用 `HvsiRdpClient`（`hvsirdpclient.exe`）通过 Hyper-V Socket 传输 RDP；Windows Sandbox
的 NamedPipe 传输是其现代等价物（见 [03 WDAG](03-wdag.md)）。
**English**: WDAG used `HvsiRdpClient` (`hvsirdpclient.exe`) over a Hyper-V Socket; Windows Sandbox's
named-pipe transport is its modern equivalent (see [03 WDAG](03-wdag.md)).

## 9. RemoteApp (RAIL) 经由 mstsc / RemoteApp (RAIL) through `mstsc`

**中文**：**备选套件** `Wsm.Connection.RemoteApp`（remoteapptool 风格的 .rdp 交付）由**系统自带的远程桌面
客户端** `mstsc.exe` + 生成的 `.rdp` 文件呈现 RAIL 会话（默认 RAIL 呈现见 §10 的 WDAG 式客户端）。
`.rdp` 的字段组合遵循
[remoteapptool](https://github.com/kimmknight/remoteapptool) 把应用开放给 RDP 的做法：
`remoteapplicationmode:i:1`、`remoteapplicationprogram:s:<程序路径>`、
`alternate shell:s:rdpinit.exe`、`disableremoteappcapscheck:i:1`、
`username:s:<用户>` + `password 51:b:<DPAPI 十六进制>`、标准 RDP 安全
（`authentication level:i:0`、`enablecredsspsupport:i:0`、`negotiate security layer:i:1`）。

**English**: The **alternative kit** `Wsm.Connection.RemoteApp` (remoteapptool-style `.rdp` delivery)
presents RAIL through the **stock Remote Desktop client** (`mstsc.exe`) driven by a generated `.rdp`
document (the default RAIL presentation is the WDAG-style client of §10). The document follows how
[remoteapptool](https://github.com/kimmknight/remoteapptool) exposes an application to RDP:
`remoteapplicationmode:i:1`, `remoteapplicationprogram:s:<path>`, `alternate shell:s:rdpinit.exe`,
`disableremoteappcapscheck:i:1`, `username` + `password 51:b:<DPAPI hex>` and standard RDP security.

### 9.1 mstsc 与命名管道 / `mstsc` and the named pipe

**中文**：`mstsc.exe` **不能**直连命名管道（只有 `/v:<主机>` 与 `.rdp` 的 `full address`，均走 TCP）。
WDAG 当年的 `hvsirdpclient.exe` 与 Windows Sandbox 的 `WindowsSandboxRemoteSession.exe` 都是
**自建 `mstscax` 宿主**（分别走 HvSI 与 RDPBASE 命名管道），并非 `mstsc.exe`。但沙箱命名管道
`\\.\pipe\<VMId>` 内传送的是**标准 RDP 字节流**（TPKT/X.224）：Devolutions IronRDP 的
`ironrdp-client` 明确把该管道当普通 byte stream 直连（`--sandbox-id` / `--sandbox-pipe`，
标准 RDP 安全、无 TLS/CredSSP）。因此 `Wsm.Connection.RemoteApp` 架一条
**loopback TCP &#8596; 命名管道** 的透明字节桥（`LoopbackSessionBridge`）：`mstsc` 连
`127.0.0.1:<临时端口>`，桥把字节原样转发进管道。有 guest TCP 地址（HCS 显式开启 TCP）时同样
经过桥，以便统一观测会话生命周期。

**English**: `mstsc.exe` cannot dial a named pipe (only `/v:<host>` and `full address`, both TCP).
WDAG's `hvsirdpclient.exe` and the Sandbox's `WindowsSandboxRemoteSession.exe` were **custom
`mstscax` hosts** (HvSI and the RDPBASE named pipe), not `mstsc.exe`. However the sandbox pipe
carries a **standard RDP byte stream** (TPKT/X.224): Devolutions' IronRDP client connects that pipe
as a plain byte stream (`--sandbox-id` / `--sandbox-pipe`, standard RDP security, no TLS/CredSSP).
`Wsm.Connection.RemoteApp` therefore runs a transparent **loopback TCP &#8596; pipe** bridge
(`LoopbackSessionBridge`); `mstsc` dials `127.0.0.1:<ephemeral>` and the bridge forwards bytes
verbatim. TCP-reachable guests are bridged the same way so session lifetime stays observable.

### 9.2 guest 侧开放（remoteapptool 的服务端步骤）/ Guest-side enablement

**中文**：Windows 客户机 SKU（含 WDAG guest）对 RemoteApp 存在允许列表限制；连接前由应用层经引擎的
in-guest 命令通道写入（幂等、尽力而为，引擎不能执行 guest 命令时跳过）：

- `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Terminal Server\TSAppAllowList` →
  `fDisabledAllowList` = `1`；
- `HKLM\SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services` →
  `fAllowUnlistedRemotePrograms` = `1`。

**English**: Client SKUs (the WDAG guest included) restrict RemoteApp programs through an allow
list. Before a RAIL connect the application layer writes two idempotent registry values through the
engine's in-guest command channel (best effort; engines without guest exec are skipped), mirroring
remoteapptool's server-side setup.

### 9.3 本机实测 / Live findings (2026-10-15, HCS + named pipe)

- **桥接 + mstsc 全桌面连接成功**：渲染出 guest 桌面（`LoopbackSessionBridge` 字节转发、
  `password 51:b:` 凭据与标准 RDP 安全端到端可行）。
- **RAIL（`remoteapplicationmode:i:1`）到达「正在启动你的应用 / 正在配置远程会话」**；
  实测中（含 §9.2 的 guest 允许列表已开放、凭据/桥接可用）应用窗口仍未出现，停在该阶段——
  guest 侧 RAIL 会话/ `rdpinit` 的接受条件是后续真机定位项。实现、路由、`.rdp`、桥接、
  凭据与会话跟踪均已实测连通；RAIL 窗口的端到端出现尚待复验。
- **未签名 `.rdp` 首次打开有发布者安全警告**；勾选“不再询问”后按地址记录在
  `HKCU\Software\Microsoft\Terminal Server Client\LocalDevices\<地址>`（桥接固定为 `127.0.0.1`，
  仅首次出现）。
- **命名管道全宿主仲裁单会话**（与窗口连接器时代相同）：需要并发 RAIL 的沙箱应使用 TCP 传输。
- `mstsc.exe` 收到 `.rdp` 后会另起真正的客户端进程并立即退出；模块按**进程快照**跟踪新出现的
  `mstsc` 进程判定会话，并把它们绑定到 kill-on-close 作业对象（杀死 `wsm connect` 子进程即终止
  客户端，不留孤儿）。

### 9.4 备选套件 / Alternative kit

**中文**：§9.1–9.3 描述的 mstsc 套件（`Wsm.Connection.RemoteApp`）保留为 **remoteapptool 风格备选**：
生成 `.rdp` 交付给系统 `mstsc`，并提供 GUI 快捷方式写入器。

**English**: The `mstsc` kit described above (`Wsm.Connection.RemoteApp`) remains as the
**remoteapptool-style alternative**; the default RAIL presentation is the WDAG-style client below.

## 10. RemoteApp 客户端（WDAG 式）/ The WDAG-style RemoteApp client

**中文**：默认的 RAIL 呈现是 `Wsm.Connection.RailClient` —— 对齐 WDAG / Windows Sandbox 官方客户端的做法：

- **官方客户端剖析（本机取证）**：`C:\Windows\System32\WindowsSandboxClient.exe`（原生，源自
  `windows\containers\disposablevm\client\remotesession.cpp`/`remotesessiondialog.cpp`）是 **MFC 对话框
  宿主 mstscax**（含 `AXWIN Frame Window`/`SetWindowRgn` 等）并用 `RDPBASE` 命名管道；包内的
  `WindowsSandboxRemoteSession.dll`（托管，`SandboxRdp.RemoteSession.*`）用 **`AtlAxWin`（ATL ActiveX 容器）
  + `AtlAxGetControl`** 在 WinUI 页面里承载 mstscax，且只调用 `set_RemoteProgramMode`，RAIL 状态完全由
  **事件**（`OnRemoteProgramResult`/`OnRemoteProgramDisplayed`/`OnRemoteWindowDisplayed`）驱动；
  其遥测名（`RdpSessionRemoteProgramDisplayed`、`RdpSessionRemoteWindowDisplayed`、
  `RdpSessionInternalDialogDisplayed/Dismissed`）说明：**连接期显示内部状态对话框，应用窗口显示后收起**。
- **本仓库实现**：`Wsm.Connection.RailClient`（`IRailClientSessionHost`）在**独立客户端进程**的 STA 线程上，
  经 `RDPBASE`（或 TCP）承载 `mstscax`，启用 `RemoteProgram3.RemoteProgramMode`，登录完成后显式
  `ServerStartProgram`；**远程应用窗口由控件创建为顶层自由窗口**（可拖动、层叠、最小化，不受宿主窗口约束），
  宿主仅是一个"启动中"状态框，应用首窗显示（或会话开始后探测到新顶层窗口）即以 **空窗口区域**
  （`SetWindowRgn`，与官方客户端同手法）隐藏；断开时恢复可见并显示 `GetErrorDescription` 的官方原因；
  事件与状态写入 `rdp-events.log`；客户端绑定 kill-on-close 作业对象（结束 `wsm connect` 子进程即终止客户端）。

**English**: The default RAIL presentation is `Wsm.Connection.RailClient`, mirroring WDAG / the Windows
Sandbox client: a dedicated client process hosts `mstscax` over the RDPBASE named pipe (or TCP) on an STA
thread with `RemoteProgram3.RemoteProgramMode`; the requested program (`RemoteApplicationProgram` /
`Name` / `Args`) and the session shell (`SecuredSettings3.StartProgram = rdpinit.exe`, i.e.
`alternate shell:s:rdpinit.exe`) are set exactly like a published-application `.rdp`; the remote
application windows are **free-floating top-level windows** created by the control
(drag/stack/minimize like local apps), while the host is only a "starting" status dialog that is hidden
with an **empty window region** (`SetWindowRgn`) once the program is displayed — the technique the
platform client itself uses.

**Live results (2026-10-15, HCS)** — over **TCP** (`ContainerMode=0`) both this client and stock
`mstsc` connect, log on, keep a **stable** session, and the guest's RdpCoreTS log shows every RAIL
channel (`rail`, `rail_wi`, `rail_ri`, RemoteApp aux/graphics redirection, HiDef frame buffer)
established; over the **named pipe** (`ContainerMode=1`) requesting the `rdpinit.exe` shell kills the
session ~5s in, and without it the session is logged off ~30s later by rdpinit's empty-session
timeout. **In every variant the guest never starts a session shell or the requested application**
(1s-resolution guest process watch: no `rdpinit`/`rdpshell`/`notepad`/`explorer`), so no RAIL window
ever appears — with `mstsc`, with our client, with pre-connect programs, with post-logon
`ServerStartProgram`, and with a published `TSAppAllowList\Applications` entry. The remaining gate is
on the guest side (platform-orchestrated RAIL bootstrap); see the Chinese block above for the full
matrix. The X-ray of the shipped binaries (`.rdp` contract, consent dialog, `LocalDevices` trust) and
the next diagnostic steps (guest ETW of the RAIL providers) are documented above.

**本机实测（2026-10-15，HCS）/ Live findings**：

**TCP 传输（`ContainerMode=0`，全新沙箱 + Default Switch DHCP）**

- 修复 HNS 网络选择（优先 ICS/Default Switch，见下）后，TCP 沙箱启动成功：guest 获得
  `172.21.x.x`（DHCP），宿主到 3389 可达。
- `Wsm.Connection.RailClient` 与 `mstsc.exe`（remoteapptool 风格 `.rdp`）**都能**连接、登录
  （LSM: "Session logon succeeded" + "Shell start notification received"），会话**稳定**
  （观察 >70 秒不中断，无 5s/30s 掉线）。
- guest 的 RdpCoreTS 日志显示 **RAIL 通道全部建立**：`rail`、`rail_wi`、`rail_ri`、
  `Microsoft::Windows::RDS::RemoteAppAuxRedirection`、`RemoteAppGraphicsRedirection`、
  `Frame_Buffer::Control` 等。
- mstsc 会显示 "RemoteApp 安全警告"（未知发布者）对话框；信任后（`HKCU\Software\Microsoft\
  Terminal Server Client\LocalDevices\<地址> = 0x4c`）会话继续稳定。
- **但 guest 从启动会话 shell 到拉起应用这一段没有发生**：1 秒粒度的 guest 进程观察
  （`for /l` + `tasklist >> C:\watch.log`）中 `rdpinit.exe`/`rdpshell.exe`/`notepad.exe`/
  `explorer.exe` 从未出现；客户端 `OnRemoteProgramResult` 从未触发。

**命名管道（`ContainerMode=1`）**

- 请求 `alternate shell: rdpinit.exe` 时：会话在**登录前 ~5 秒**被断开（guest 侧
  reason `0x8007006D` = `ERROR_BROKEN_PIPE`）。
- 不请求 alternate shell 时：登录成功、shell start notification，**~30 秒后**断开——
  来自 rdpinit 的空会话超时（其字符串 "No previous successful rail app launches during
  timeout period. Will logoff"，对应 `RemoteAppEmptySessionDisconnectTimeout`）。
- 同样没有任何应用进程。

**对照矩阵（全部失败在同一处 → guest 侧门槛）**

对 TCP 与命名管道分别测试：无 alternate shell / 预连接 `remoteapplicationprogram` /
登录后 `ServerStartProgram` / 两者兼有 / 把 notepad 发布进
`TSAppAllowList\Applications\Notepad` 并以 `||Notepad` 请求 / 清除
`RemoteAppSkipFirstShellInit` —— 会话与登录均正常，**应用进程始终不出现**。

**guest 侧画像（离线 hive + 字符串取证）**

- 镜像自带：`TSAppAllowList\fDisabledAllowList=1`、`LicensingType=5`、
  `Terminal Server\ContainerMode`、`RemoteAppSkipFirstShellInit=1`、
  `RemoteAppEmptySessionDisconnectTimeout(*) = 0xf4240`、`RailShowallNotifyIcons=1`。
- `rdpinit.exe`（签名有效）是完整的 "RemoteApp Logon Application"（含 rail tray/图标、
  `IsRailAllowed`、"RAIL is enabled on this Server/Client SKU"、"RAIL on this Client SKU is
  not allowed"、`E_RAIL_SVC_HANDSHAKE_TIMEOUT` 等字符串），`rdpshell.exe` 为 RAIL shell 宿主。

**结论 / Conclusion**

- **客户端侧已就绪**：连接、登录、RAIL 通道、事件、状态框收起/恢复、TCP 稳定会话、
  以及（管道）会话回收；`.rdp`/mstscax 契约与 remoteapptool 一致。
- **未闭环点**：guest 侧"启动 RAIL shell / 拉起被请求应用"的路径对普通 RDP 客户端
  （mstsc 与 mstscax 一致）不响应，疑似由平台的容器编排（WDAG/Windows Sandbox 客户端
  配置向 guest 不传任何 RAIL 应用信息）驱动。后续定位手段：guest 内 ETW 追踪
  （`Microsoft-Windows-RemoteDesktopServices-RdpCoreTS` 与 rdpinit 的 RAIL provider），
  或对比官方 `WindowsSandboxClient.exe` 在平台托管沙箱中的会话。

**顺带修复（本轮）/ Incidental fixes**

- **HNS 网络选择**：`GetOrCreateNatNetworkIdAsync` 现在**优先 ICS 网络**（Hyper-V
  Default Switch，自带 ICS DHCP/DNS），其次才用普通 NAT。修复前本机存在一个无 DHCP 的
  `wsm-nat`（fallback 网络）排在前面，guest 只能拿到 APIPA (169.254.x)，**所有 TCP 传输的
  沙箱启动都会失败**（"no guest IPv4 address could be discovered"）。
- **CLI `--local`**：强制使用进程内引擎，忽略驻留的 WsmAgent 服务。同机多会话或诊断时
  必需（本机另有一个旧版 agent 服务在 `\\.\pipe\wsm-agent` 上，曾使 `wsm exec` 走了它的
  旧代码路径而看不到 guest 输出/退出码）。


### 10.1 已完成的一并改造 / Alongside changes

- GUI 启动列表（全局 + 每沙箱）与 `wsm connect --remoteapp --program <p> [--program-args <a>] [--quiet]`
  均与呈现方式无关，随新的 RAIL 客户端继续适用（快捷方式仍指向 `wsm connect`）。
- `ConnectionEventLog` 提升到 `Wsm.Connection.RdpBase`，两个连接套件共用 `rdp-events.log`。

## 证据 / Evidence

- `WindowsSandboxRemoteSession` 反编译：`NamedPipeConnector`、`pipeName = \\.\pipe\{config.VMId}`、`ConnectWithEndpoint`、`RemoteProgram3.RemoteProgramMode`。
- `smourier/WinformsSandbox`、`M2Team/NanaBox` 的 `RDPBASE`/`IRDPENCNamedPipeDirectConnector` 用法与 GUID。
- `WindowsSandboxClient.exe` 字符串：`RDPBASE_CreateInstance`、`RailMode`、`HiDefRemoteAppContainerGUID`。
- 官方客户端（本机系统二进制取证）：`C:\Windows\System32\WindowsSandboxClient.exe`（MFC + mstscax：
  `AXWIN Frame Window`、`SetWindowRgn`、`remotesession.cpp`）；包内 `WindowsSandboxRemoteSession.dll`
  （`SandboxRdp.RemoteSession`：`AtlAxWin`/`AtlAxGetControl`、`set_RemoteProgramMode`、
  `OnRemoteProgramResult`/`OnRemoteProgramDisplayed`/`OnRemoteWindowDisplayed`；WinUI `RemoteSessionPage`）。
- remoteapptool：`RDPFileLib/RDPFileLib.vb`（`.rdp` 字段）、`RemoteAppCreateClientConnection.vb`
  （`remoteapplicationmode:i:1`、`remoteapplicationprogram:s:||<名称>`、`alternate shell:s:rdpinit.exe`）、
  `RemoteAppHostOptions.vb`（`TSAppAllowList\fDisabledAllowList`、`fAllowUnlistedRemotePrograms`）。
- Devolutions IronRDP：`crates/ironrdp-client/src/config.rs`（`NamedPipe` 连接器，"byte stream carrying
  TPKT/X.224"）、`crates/ironrdp-agent`（`--sandbox-pipe`、标准 RDP 安全、无加密）、
  `crates/ironrdp-activex`（RAIL 窗口由客户端按 `TS_RAIL_ORDER_WINDOWCREATE` 创建为顶层窗口）。
- 实现：`src/30-Connections/Wsm.Connection.RailClient/`（`RailClientForm`、`WinFormsRailClientSessionHost`）、
  `src/30-Connections/Wsm.Connection.RemoteApp/`（`RemoteAppRdpDocument`、`LoopbackSessionBridge`、
  `MstscRemoteAppSessionHost`、`MstscClientTracker`、`ProcessJob`、`ShellLinkShortcutWriter`）。
- 相关模块：[01 Windows Sandbox](01-windows-sandbox.md)、[03 WDAG](03-wdag.md)、[09 网络与 vSMB](09-networking-and-vsmb.md)。
