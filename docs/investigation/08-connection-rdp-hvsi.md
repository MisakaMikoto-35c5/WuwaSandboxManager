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

## 证据 / Evidence

- `WindowsSandboxRemoteSession` 反编译：`NamedPipeConnector`、`pipeName = \\.\pipe\{config.VMId}`、`ConnectWithEndpoint`、`RemoteProgram3.RemoteProgramMode`。
- `smourier/WinformsSandbox`、`M2Team/NanaBox` 的 `RDPBASE`/`IRDPENCNamedPipeDirectConnector` 用法与 GUID。
- `WindowsSandboxClient.exe` 字符串：`RDPBASE_CreateInstance`、`RailMode`、`HiDefRemoteAppContainerGUID`。
- 相关模块：[01 Windows Sandbox](01-windows-sandbox.md)、[03 WDAG](03-wdag.md)、[09 网络与 vSMB](09-networking-and-vsmb.md)。
