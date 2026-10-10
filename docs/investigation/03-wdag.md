# 03 · Windows Defender Application Guard（Madrid）/ WDAG

## 1. 概述 / Overview

**中文**：Windows Defender Application Guard（WDAG，内部代号 **Madrid**）是 Windows 10/11 的
硬件隔离方案：用 Hyper-V 容器隔离不受信任的浏览（Edge）与 Office 文档。它已被微软**弃用**，
但其技术栈被 Windows Sandbox 继承。
**English**: Windows Defender Application Guard (WDAG, codename **Madrid**) is a hardware-isolation
feature of Windows 10/11 that isolates untrusted browsing (Edge) and Office documents inside a
Hyper-V container. It has been **deprecated**, but Windows Sandbox inherited its technology stack.

## 2. 架构 / Architecture

**中文**：启用后，后台预启动一个隐藏的 Hyper-V 容器（体现在 `vmmem`/`vmwp` 内存增长，最大约 1.3GB）。
由 **Container Manager Service** 管理。启动流程：Browser Broker → WDAG Manager → Container Manager
Service → 在容器内启动应用。客户端是一个特殊的 RDP 客户端 **`HvsiRdpClient`**，把容器内应用以
**远程应用（RAIL）** 形式呈现到主机。
**English**: Once enabled, a hidden Hyper-V container is pre-launched in the background (visible as
`vmmem`/`vmwp` memory growth, up to ~1.3GB), managed by the **Container Manager Service**. Launch
flow: Browser Broker → WDAG Manager → Container Manager Service → start the app inside the container.
The client is a special RDP client, **`HvsiRdpClient`**, presenting the in-container app to the host
as a **remote app (RAIL)**.

## 3. 组件 / Components

| Item | 中文 | English |
|---|---|---|
| `madrid.dll` | WDAG 引擎（HCS JSON 中 `Owner` 即 "Madrid"） | WDAG engine (`Owner` in the HCS JSON is "Madrid") |
| `hvsirdpclient.exe` | HvSI RDP 客户端 | HvSI RDP client |
| `hvsicontainerservice.dll` | 容器服务 | Container service |
| `cmclient.dll` | `Cms*` 容器管理客户端 API（与 Sandbox 共用） | `Cms*` container-management client API (shared with Sandbox) |
| `HvsiManagementApi.dll` / `HvsiSettingsProvider.dll` | 策略/设置 | Policy/settings |
| `vmcompute.dll` | HCS | HCS |

## 4. 多实例 / Multiple instances

**中文**：WDAG 支持**同时**运行多个隔离实例（例如 Edge 与 Office），这正是「底层 HCS 本就支持
多实例」的直接证据。
**English**: WDAG could run **multiple** isolated instances **concurrently** (e.g. Edge and Office),
direct evidence that the underlying HCS always supported multiple instances.

## 5. RAIL / HvsiRdpClient

**中文**：WDAG 用 **RDP over HvSI（Hyper-V Socket）** 传输，并以 RAIL 模式呈现单个应用窗口——
这与 Windows Sandbox 的 `RailMode` + `RDPBASE` 命名管道连接是**同一机制**。
**English**: WDAG transported **RDP over HvSI (Hyper-V Socket)** and presented individual app
windows in RAIL mode — the **same mechanism** as Windows Sandbox's `RailMode` + `RDPBASE` named-pipe
connection.

## 6. 与 Windows Sandbox 的关系 / Relation to Windows Sandbox

**中文**：两者共用：Hyper-V 容器（DisposableVM）、`CmService` + HCS、`cmclient.dll`（`Cms*`）、
RDP-over-HvSI/RAIL、镜像层 + vSMB。差异：WDAG 面向应用级隔离（策略驱动、预启动容器、RAIL 为主）；
Sandbox 面向整桌面（`.wsb` 配置、全桌面或 RAIL、用户可交互）。
**English**: They share the Hyper-V container (DisposableVM), `CmService` + HCS, `cmclient.dll`
(`Cms*`), RDP-over-HvSI/RAIL and the image-layer + vSMB model. Difference: WDAG targets
application-level isolation (policy-driven, pre-launched container, RAIL-first); Sandbox targets a
full desktop (`.wsb` config, full desktop or RAIL, user-interactive).

## 7. 弃用 / Deprecation

**中文**：微软已宣布弃用 WDAG，转向更集成的安全方案。但 WDAG 的组件（`madrid.dll`、
`hvsirdpclient.exe`、`hvsicontainerservice.dll`、`cmclient.dll`）在本机 `System32` 中仍然存在，
并被 Windows Sandbox 复用。
**English**: Microsoft has deprecated WDAG in favour of more integrated security solutions. Its
components (`madrid.dll`, `hvsirdpclient.exe`, `hvsicontainerservice.dll`, `cmclient.dll`) still
exist in `System32` on this machine and are reused by Windows Sandbox.

## 证据 / Evidence

- `System32` 中同时存在 `madrid.dll`、`hvsirdpclient.exe`、`hvsicontainerservice.dll`、`HvsiManagementApi.dll`、`cmclient.dll`。
- Check Point 研究给出的 HCS JSON 中 `"Owner": "Madrid"`。
- MIRU.CH 描述 WDAG 使用 `HvsiRdpClient` 以远程应用形式呈现容器内应用。
- 相关模块：[01 Windows Sandbox](01-windows-sandbox.md)、[06 CmService/Cms](06-cmservice-and-cms-api.md)、[08 连接](08-connection-rdp-hvsi.md)。
