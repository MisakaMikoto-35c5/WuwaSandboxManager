# 09 · 网络（HNS）与 vSMB/HvSocket / Networking and vSMB/HvSocket

## 1. 网络：HNS / Networking: HNS

**中文**：容器/沙箱的网络由 **HNS**（Host Compute Network）提供：Endpoint / Network / Policy。
Sandbox 的 `<Networking>Enable</Networking>` 对应 UDK `VMOptions.NetworkingMode = Nat`，即接到
Hyper-V 默认交换机（NAT）。在裸 HCS 中，网络通过 `Devices.NetworkAdapters["0"].EndpointId` 引用
一个 HNS endpoint。
**English**: Container/Sandbox networking is provided by **HNS** (Host Compute Network):
endpoint/network/policy. `<Networking>Enable</Networking>` maps to UDK `VMOptions.NetworkingMode =
Nat` (attached to the Hyper-V default switch). In raw HCS, networking is expressed via
`Devices.NetworkAdapters["0"].EndpointId` referencing an HNS endpoint.

| Item | 中文 | English |
|---|---|---|
| `VMNetworkingMode.Nat` | 默认交换机 NAT | default-switch NAT |
| `NetworkAdapters["0"].EndpointId` | HNS endpoint | HNS endpoint |
| `GetIpAddress` | 返回 guest 接口 IP | returns guest interface IPs |
| `NetworkAdapters["0"].MacAddress` | 可选 MAC | optional MAC |

## 2. vSMB / vSMB

**中文**：**Virtual SMB** 让 guest 访问宿主目录（含只读基础层），由宿主 `vmusrv`（在 `vmwp.exe` 内）
提供，走 VMBus。Sandbox 用它对 guest 暴露 OS 层。
**English**: **Virtual SMB** lets the guest access host directories (including the read-only base
layer), served by the host `vmusrv` (inside `vmwp.exe`) over VMBus. Sandbox uses it to expose the OS
layer to the guest.

| Item | 中文 | English |
|---|---|---|
| `VirtualSmb.Shares[]` | 共享（`Name`,`Path`,`Options`） | Shares (`Name`,`Path`,`Options`) |
| `Shares[0]` | `os` → 基础层 `Files`（只读） | `os` → base layer `Files` (read-only) |
| `Options.ReadOnly` | 只读 | read-only |
| `Options.ReparseBaseLayer` | 解析层 reparse | resolve layer reparse points |
| `Options.NoLocks`/`PseudoOplocks`/`PseudoDirnotify` | SMB 语义优化 | SMB semantic optimizations |
| `DirectFileMappingInMB` | 直接文件映射上限（如 2048） | direct file mapping limit |

**中文**：宿主创建共享通过向存储提供者设备 `CreateFileW(\??\STORVSP\VSMB\??\<path>)` + IOCTL `0x240328`；
guest 侧路径形如 `\\.\vmsmb\VSMB-{guid}\os\...`。
**English**: The host creates shares via `CreateFileW(\??\STORVSP\VSMB\??\<path>)` + IOCTL `0x240328`;
guest-side paths look like `\\.\vmsmb\VSMB-{guid}\os\...`.

## 3. HvSocket / Hyper-V Socket

**中文**：**Hyper-V Socket** 是主机↔guest 的内核级套接字，用于 RDP、LSM/NLM 等服务。Sandbox 的
`Devices.HvSocket.HvSocketConfig.ServiceTable` 列出服务 GUID 与安全描述符。
**English**: **Hyper-V Socket** is a host↔guest kernel-level socket used by RDP, LSM/NLM services,
etc. Sandbox lists service GUIDs and security descriptors in
`Devices.HvSocket.HvSocketConfig.ServiceTable`.

| 服务 Service | GUID | 说明 Description |
|---|---|---|
| LSM | `F58797F6-C9F3-4D63-9BD4-E52AC020E586` | License/Logon 服务 / license-logon service |
| NLM | `A715AC94-B745-4889-9A0F-772D85A3CFA4` | Network Location / network-location service |

**中文**：默认安全描述符 `D:P(A;;FA;;;SY)`（仅 SYSTEM 全权）。`HyperVSocketServiceConfiguration` 由
`ManagedWindowsVM`/`VMOptions` 添加（LSM/NLM）。
**English**: Default security descriptor `D:P(A;;FA;;;SY)` (SYSTEM full control).
`HyperVSocketServiceConfiguration` entries (LSM/NLM) are added by `ManagedWindowsVM`/`VMOptions`.

## 4. EnhancedModeVideo（RDP 端点）/ EnhancedModeVideo (RDP endpoint)

**中文**：`Devices.EnhancedModeVideo.ConnectionOptions = { AccessSids, NamedPipe }` 提供 RDP 端点；
`NamedPipe` 即 `\\.\pipe\<VMId>`，与 [08 连接](08-connection-rdp-hvsi.md) 一致。
**English**: `Devices.EnhancedModeVideo.ConnectionOptions = { AccessSids, NamedPipe }` provides the RDP
endpoint; `NamedPipe` is `\\.\pipe\<VMId>`, matching [08 Connection](08-connection-rdp-hvsi.md).

## 5. 网络与 HCS 的关系 / Networking and HCS

**中文**：HCS 不创建网络资源（客户端/HNS 负责）。Sandbox 由 `CmService`/`ManagedWindowsVM` 创建
HNS endpoint；裸 HCS 需自行用 HNS API 创建 endpoint 再填入 `NetworkAdapters`。
**English**: HCS does not create network resources (the client/HNS does). Sandbox's endpoint is
created by `CmService`/`ManagedWindowsVM`; raw HCS requires creating an HNS endpoint yourself and
filling it into `NetworkAdapters`.

## 证据 / Evidence

- IL 反编译 `WindowsSandboxServer.Constants`（LSM/NLM GUID）、`GetVMOptionsFromRecipe`（`NetworkingMode = Nat`、HvSocket 服务）。
- Check Point 文章的 vSMB share 与 `\??\STORVSP\VSMB\??\...` + IOCTL `0x240328`、`\\.\vmsmb\VSMB-{guid}\os\...`。
- HCS schema `VirtualSmb`/`NetworkAdapter`/`HvSocket`/`EnhancedModeVideo`（见 [10](10-hcs-schema-and-native-api.md)）。
- 相关模块：[02 镜像](02-sandbox-image.md)、[05 容器层](05-container-layers.md)、[08 连接](08-connection-rdp-hvsi.md)。
