# Windows Sandbox 内部机制调查报告

> 本报告基于对本机 Windows 11（Build 26200 / `ge_release`）的实测与逆向分析，
> 覆盖公开文档、二进制字符串、IL 反编译（`SandboxCommon.dll`、`SandboxGrpc.dll`、
> `WindowsSandboxServer.dll`、`wsb.dll`、`WindowsSandboxRemoteSession.dll`）以及
> 运行期实验。

---

## 1. 结论速览

| 主题 | 结论 |
| --- | --- |
| 底层实现 | Windows Sandbox 是一个 **Hyper-V 容器（DisposableVM）**，由 `CmService` 经 HCS（`vmcompute`）创建，工作进程为 `vmwp`。 |
| 血统 | 与已废弃的 **Windows Defender Application Guard（内部代号 "Madrid"）** 同源：共用 `cmclient.dll`（`Cms*` API）、`hvsirdpclient.exe`、`madrid.dll`、`vmcompute.dll`。 |
| 现代控制面 | **`WindowsSandboxServer.exe` 暴露 gRPC 服务 `sandboxserver.SandboxCore`，通过命名管道 `\\.\pipe\wsandbox\<MD5(SID)>` 通信**。这是 `wsb.exe` 与新版 WinUI「Windows Sandbox」应用使用的接口。 |
| 传统控制面 | `WindowsSandbox.exe <file.wsb>`，单实例、全桌面。 |
| 配置模型 | `.wsb` XML（根 `<Configuration>`），对应内部类型 `WsbRecipe`。**CPU 核数不在 .wsb 中**。 |
| 连接方式 | 默认 **RDP over Named Pipe**（`\\.\pipe\<VMId>`），由 `RDPBASE.dll` 的 `IRDPENCNamedPipeDirectConnector` 提供传输。**RAIL / RemoteApp** 由 **WDAG 式独立客户端**（`Wsm.Connection.RailClient`：mstscax + RDPBASE，应用窗口为自由浮动的顶层窗口）呈现；**全桌面**经 `mstscax` 窗口连接器；remoteapptool 风格的 `mstsc` + `.rdp` 套件为备选。 |
| 多实例 | 官方**默认禁止**（`CO_E_APPSINGLEUSE`），由许可策略 `SandboxContracts.AreMultipleInstancesAllowed` 控制；AVD/WVD 主机或内部 `rs_*` 分支才允许多实例。WDAG 当年正是靠底层 HCS 同时跑 Edge 与 Office。 |
| GPU 直通 | `<vGPU>` → `VMOptions.VirtualGpuOptions.MirroringKind = AllGpus`。 |
| 网络 | `<Networking>` → `VMOptions.NetworkingMode = Nat`（Hyper-V 默认交换机）。 |
| 自定义基础镜像层位置 | **HCS 层允许**（`Layer.Path` / `VirtualSmb.Shares[].Path` / `RunInSilo.SiloBaseOsPath` + `vmcompute!CreateBaseImageVHD/CreateLayer/ImportLayer`）；但 Sandbox 自带栈（`CmService`/`VMOptions`）硬编码 `ProgramData` 且不暴露该字段。 |
| 自定义用户数据位置 | **大小可配**（`VMOptions.MaxStorageSizeMB`）；**位置**仅 HCS 直连可改（`Scsi.Attachments[].Path` / `CmsCreateStorage` / `CmsMapVirtualDiskToContainer`）。 |
| 检查点 | **HCS 支持**（`HcsSaveComputeSystem` + `SaveOptions{ToFile\|AsTemplate}` + `RestoreState`），落盘于 `Snapshots\<id>\SnapshotSavedState.vmrs`；Sandbox 内部已用于 `RebootSupport`。 |
| 提权要求 | **HCS 需要管理员 / Hyper-V 管理员**（否则 `0x8037011B`）；`CmService` 服务以 SYSTEM 代劳，所以 Sandbox 自身可非提权使用。 |

---

## 2. 组件与进程

| 组件 | 作用 |
| --- | --- |
| `CmService.dll` / `CmService` | Container Manager Service：准备 base layer、挂载卷、生成 HCS JSON（`CmService!Container::Manager::Hcs::Details::GenerateCreateComputeSystemJson`）。 |
| `cmclient.dll` | `Cms*` 客户端 API（`CmsCreateContainer` / `CmsCreateStorage` / `CmsMapFolderToContainer` / `CmsCreateProcessInContainer` / `CmsRegisterForContainerNotifications` / `CmsQueryContainerMemoryInfo` / `CmsInitiateShutdownContainer` …）。 |
| `vmcompute.dll` / `vmcompute.exe` | Host Compute Service（HCS），编排所有 compute system。 |
| `vmwp.exe` | 每个容器的 VM worker（含 `vmusrv` 的 vSMB 服务）。 |
| `WindowsSandbox.exe` | 传统全桌面客户端（也实现单实例互斥 `WindowsSandboxMutex`）。 |
| `WindowsSandboxServer.exe` | **现代控制面服务器**：托管 gRPC，负责创建/管理多个 VM。 |
| `WindowsSandboxRemoteSession.exe` | RDP 客户端，`--id <sandboxId>` 连接指定实例（全桌面或 RAIL）。 |
| `wsb.exe` | CLI（`start/list/exec/share/stop/connect/ip`），与 WinUI 应用共用 `SandboxCommon`。 |
| `SandboxCommon.dll` | 共享库：`GrpcClient`、`GrpcServer`、`WsbRecipe`、`RdpClientConfig`、`RecipeRepository`、组策略校验。 |
| `SandboxGrpc.dll` | gRPC 契约（`sandboxserver.SandboxCore` 服务与消息）。 |
| `SandboxUDK.dll` / `ManagedWindowsVM` | 创建并管理 Hyper-V 容器的原生/托管桥。 |
| `hvsirdpclient.exe` / `madrid.dll` / `HvsiManagementApi.dll` | WDAG（Madrid）遗留组件，说明二者同源。 |

进程可见性：启动一个沙箱后可见 `WindowsSandboxServer`、`ManagedWindowsVM`、`vmcompute`、`vmwp`。

---

## 3. 控制平面 A：传统 `.wsb` + `WindowsSandbox.exe`

- 配置文件为 XML，扩展名 `.wsb`，根元素 `<Configuration>`。
- 通过双击或 `WindowsSandbox.exe "C:\path\config.wsb"` 启动。
- 由 `WindowsSandbox.exe` 内部互斥体保证**同一时刻仅一个实例**。
- 适合「常规 Sandbox 方式连接（类 Remote Desktop 的全桌面）」。

### 配置项（`.wsb`）

| 元素 | 取值 | 说明 |
| --- | --- | --- |
| `<vGPU>` | Enable / Disable / Default | 虚拟 GPU |
| `<Networking>` | Enable / Disable / Default | 网络 |
| `<MappedFolders><MappedFolder>` | `HostFolder` / `SandboxFolder` / `ReadOnly` | 目录映射 |
| `<LogonCommand><Command>` | 命令 | 登录后执行 |
| `<AudioInput>` / `<VideoInput>` | Enable / Disable / Default | 音视频输入 |
| `<ProtectedClient>` | Enable / Disable / Default | AppContainer 隔离 |
| `<PrinterRedirection>` / `<ClipboardRedirection>` | Enable / Disable / Default | 打印机/剪贴板重定向 |
| `<MemoryInMB>` | 数值（<2048 会被提升到 2048） | 内存 |
| `<MemoryInMB>` 之外还有 | `DisplayName` / `RdpTransport` / `RailMode` / `FullScreenMode` / `SmartCardRedirection` / `RebootSupport` / `PersistenceSupport` / `AccountUser` / `AccountPassword` / `WorkingDirectory` / `TargetDisplay` | 内部 `WsbRecipe` 扩展项 |

> 关键点：**`.wsb` 没有 CPU 核数配置**。CPU 只能通过底层 HCS 的
> `VirtualMachine.ComputeTopology.Processor.Count` 控制。

---

## 4. 控制平面 B：`WindowsSandboxServer` gRPC（推荐）

这是 `wsb.exe` 与新版 WinUI 应用使用的接口，也是本项目采用的主后端。

### 4.1 传输

- 服务器：Kestrel **命名管道传输**，`ListenNamedPipe(GrpcServer.PipeName)`。
- 管道名：`PipeName = Path.Combine("wsandbox", UserSessionId)`，
  其中 `UserSessionId = new Guid(MD5(UTF8(SID字符串))).ToString()`。
  即 `\\.\pipe\wsandbox\<MD5(SID)>`。
- 客户端：`SocketsHttpHandler.ConnectCallback` 返回 `NamedPipeClientStream`，
  `GrpcChannel.ForAddress("http://localhost")`。

> 因此**任何**能对命名管道发起 HTTP/2 的 gRPC 客户端都可以驱动 Windows Sandbox，
> 无需加载微软的程序集。本项目即用自研 proto 生成客户端。

### 4.2 服务契约

```proto
package sandboxserver;
service SandboxCore {
  rpc StartSandbox(StartRequest) returns (StartReply);
  rpc EnumerateSandboxVMs(google.protobuf.Empty) returns (EnumerateSandboxVMsReply);
  rpc GetRdpClientConfig(RdpClientConfigRequest) returns (RdpClientConfigReply);
  rpc CreateProcess(CreateProcessRequest) returns (CreateProcessReply);
  rpc MapFolder(MapFolderRequest) returns (MapFolderReply);
  rpc ShutdownSandbox(ShutdownRequest) returns (ShutdownReply);
  rpc GetTerminateReason(VMTerminateReasonRequest) returns (VMTerminateReasonReply);
  rpc GetIpAddress(IpAddressRequest) returns (IpAddressReply);
  rpc WaitForReboot(WaitForRebootRequest) returns (WaitForRebootReply);
}
```

关键消息（节选）：

- `StartRequest { optional string config = 1; optional string id = 2; optional int32 owner_process_id = 100; }`
  - `config` 是 **`WsbRecipe` 的 XML 序列化字符串**（即 `.wsb` 内容）。
  - `id` 为沙箱实例 GUID；`owner_process_id` 用于「宿主进程退出即关闭沙箱」。
- `StartReply { optional int32 hresult = 1; optional string rdp_client_config = 2; }`
  - `rdp_client_config` 是 `RdpClientConfig` 的 XML（含 `VMId`、`Username`、`Password`、`RdpTransport`、`RailModeEnabled`、各重定向开关）。
- 其余请求均以 `sandbox_id` 定位实例，返回 `hresult`。

### 4.3 运行期行为（反编译自 `WindowsSandboxServer`）

- `StartSandbox` → `SandboxManager.CreateSandboxVMAsync(config, id, ownerProcId)` → `SandboxVMCreator.Create` → `new ManagedWindowsVM(VMOptions)`。
- `GetVMOptionsFromRecipe`：`MaxMemoryMB = MemoryInMB`；`vGPU` → `VirtualGpuOptions.MirroringKind = AllGpus`；`Networking` → `NetworkingMode = Nat`；并加入 LSM/NLM 的 Hyper-V Socket 服务。
- `PrepareVM`：创建/激活账号（默认 `WDAGUtilityAccount`，密码随机 GUID）→ 运行 specialization 命令 → `wcsetupagent.exe NotifyRestoreComplete` → 映射目录 → 等待登录并执行 LogonCommand。
- `MapFolder` → `ManagedWindowsVM.ShareFolder(...)`（运行期热映射）。

---

## 5. 连接模型（RDP over Named Pipe / RAIL）

`RdpClientConfig.RdpTransport` 取值：`NamedPipe`(0, 默认) / `Local`(1) / `TCP`(2)。

- **NamedPipe**：客户端连接 `\\.\pipe\<VMId>`，无需网络即可显示。
  - 传输由 `RDPBASE.dll` 提供：
    - `CLSID_RDPRuntimeSTAContext = {FB332AE7-0055-4208-92B7-20410CA8382B}` → `IRDPENCPlatformContext`（IID `{FB332AE7-000E-...}`）。
    - `CLSID_RDPENCNamedPipeDirectConnector = {FB332AE7-0088-...}` → `IRDPENCNamedPipeDirectConnector`（IID `{4ACF942D-EADC-45BF-8EA8-793FE3CE31E8}`），方法 `InitializeInstance` / `TerminateInstance` / `StartConnect(pipeName)`。
    - 回调 `IRDPENCNamedPipeDirectConnectorCallbacks`（IID `{D923EFE9-...}`）返回 `IRDPENCNetStream`。
  - 得到的 endpoint 交给 `mstscax`（`IMsRdpClient10`）的 `AdvancedSettings.ConnectWithEndpoint` 后 `Connect()`。
- **TCP**：`GetIpAddress` 拿到 guest IP，走经典 3389（需 `<Networking>`）。
- **RAIL / RemoteApp**：`config.RailModeEnabled == true` 时设置 `RemoteProgram3.RemoteProgramMode = true`，以「独立窗口」呈现应用——这正是 WDAG 当年「远程应用」体验的机制（`HvsiRdpClient`）。
  - 现实现（`Wsm.Connection.RailClient`，默认）为 **WDAG 式独立客户端**：独立客户端进程经 RDPBASE
    承载 `mstscax`，登录后 `ServerStartProgram` 显式启动程序；**远程应用窗口是自由浮动的顶层窗口**
    （可拖动、层叠、最小化），宿主只是"启动中"状态框（应用显示后以 `SetWindowRgn` 空区域隐藏），
    RAIL 事件驱动状态。官方客户端取证与实测见 [08 连接](investigation/08-connection-rdp-hvsi.md) §10。
  - 备选（`Wsm.Connection.RemoteApp`）：生成 `.rdp`（`remoteapplicationmode:i:1`、
    `remoteapplicationprogram`、`alternate shell:s:rdpinit.exe`、`password 51:b:<DPAPI>`）由系统
    `mstsc` 打开；命名管道由 loopback TCP ↔ 管道桥转发。连接前开放 guest 端 RemoteApp 允许列表
    （`TSAppAllowList\fDisabledAllowList`、`fAllowUnlistedRemotePrograms`）。
- **全桌面**：`RailModeEnabled == false`，即普通 Remote Desktop 会话（窗口连接器 `mstscax`）。

> 注意：`RailMode` 是**创建时**写入 recipe 的标志（`GetRdpClientConfigAsync` 读取 `_recipe.RailMode`），
> 运行期不可切换；要 RemoteApp 就必须以 `RailMode` 启动。

---

## 6. 多实例限制与绕过

`SandboxCommon.Contracts.SandboxContracts`：

```csharp
public bool AreMultipleInstancesAllowed =>
    IsWVDEnabled()               // SLGetWindowsInformationDWORD("TerminalServices-RemoteConnectionManager-WVD-Enabled") != 0
    || IsInternalBranch();       // HKLM\...\CurrentVersion\BuildBranch 匹配 ^rs(?!.*release).+
```

- `SandboxManager.ThrowIfVMCannotBeCreatedWithId`：当策略为 false 且已有 VM 运行时，抛 `CO_E_APPSINGLEUSE (0x800401F6)`。
- 本机实测：`WVD-Enabled = 0`、`BuildBranch = ge_release` → **不允许**多实例；`wsb start` 第二次即报 `CO_E_APPSINGLEUSE`。

**实现多实例的路径**：

1. **许可策略解锁**（本仓库提供、需管理员、可选）：把 `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\BuildBranch` 改为 `rs_*`，使服务器把主机当作内部分支，从而允许多实例。属机器级、可回滚的改动，**默认不启用**（`wsm multi --enable`）。
2. **直连 HCS**（WDAG 的做法）：绕过 `WindowsSandboxServer` 的策略检查，直接用 `HcsCreateComputeSystem` 创建多个 DisposableVM。代价是需要自行复刻 CmService 的 base image / VHDX / vSMB / template 配置，工作量大；本仓库已提供 HCS 互操作模块作为基础。

> 由于服务器在「无法创建第二实例」时会返回 `S_OK` 但 `rdp_client_config` 为空（一个服务端怪癖），
> 本项目在应用层**主动、确定性地**做单实例守卫，并对空运行时做校验。

---

## 7. 资源控制点小结

| 资源 | `.wsb` | gRPC/`WsbRecipe` | HCS | 本项目 |
| --- | --- | --- | --- | --- |
| 内存 | `<MemoryInMB>` | `MemoryInMB` | `Memory.SizeInMB` | ✅ 全后端 |
| CPU 核数 | ✖ | ✖ | `Processor.Count` | 模型具备；gRPC 后端不支持，HCS 后端可扩展 |
| 网络 | `<Networking>` | `Networking` | HNS | ✅ |
| vGPU | `<vGPU>` | `vGPU` | `EnhancedModeVideo` | ✅ |
| 目录映射 | `<MappedFolders>` | `MappedFolders` + `MapFolder` | vSMB share | ✅ 静态 + 运行期热映射 |
| 剪贴板/打印机/音视频/智能卡 | 各元素 | `RdpClientConfig` | — | ✅ |
| RemoteApp(RAIL) | `RailMode` | `RailMode` | — | ✅ |

---

## 8. 与 WDAG 的关系（佐证）

- 本机 `System32` 同时存在 `madrid.dll`、`hvsirdpclient.exe`、`hvsicontainerservice.dll`、`cmclient.dll`。
- Check Point 的 HCS JSON 中 `"Owner": "Madrid"` 即 WDAG 代号。
- MIRU.CH 描述 WDAG「由特殊 RDP 客户端 `HvsiRdpClient` 呈现容器内应用，即远程应用」——与本文 §5 的 RAIL 机制一致。

结论：**Windows Sandbox 与 WDAG 共用同一套 Hyper-V 容器 + RDP-over-Hvsi 架构**，多实例在底层（HCS）本就支持。

---

## 9. 复现命令（本机实测）

```powershell
# 环境探测
wsm env
hcsdiag list
Get-ChildItem \\.\pipe\ | Where-Object Name -match 'wsandbox'

# 直接使用微软 CLI 观察行为
wsb list --raw
wsb start --raw                 # 返回 { "Id": "<guid>" }
wsb list --raw
wsb ip --id <guid> --raw        # { "Networks":[{"IpV4Address":"172.26.x.x"}] }
wsb start --raw                 # 第二次 -> CO_E_APPSINGLEUSE
wsb stop --id <guid>
```

本项目的引擎实测（`wsm`）：`create → start --remoteapp → ip → share → exec → running → stop`，
并验证了第二实例被确定性拒绝。

---

## 10. 风险与未决问题

- `WindowsSandboxServer` 的 gRPC 契约属**非公开**接口，随 Windows 版本可能变化；已用 `.proto` 固化并附来源注释。
- `RDPBASE.dll` 的 COM 接口为非公开；本仓库仅实现 NamedPipe 连接器所需最小面。
- 服务器在「单实例拒绝」时返回空成功的怪癖，已在客户端防御。
- CPU 直通与真多实例需要 HCS 直连后端（本项目已铺设互操作基础，未完整复刻 CmService 镜像流程）。

---

## 11. HCS 直连方案：基础镜像 / 用户数据 / 检查点（本项目采用）

> 本节回答三个问题，并给出 HCS 直连的实现依据。所有结论均有本机证据。

### 11.0 提权要求（前提）

HCS 需要调用者属于 **Administrators 或 Hyper-V Administrators**。本机实测：

```
HcsEnumerateComputeSystems -> HRESULT 0x8037011B
   "权限不足。只有管理员或 Hyper-V 管理员用户组的用户才能执行此操作"
```

因此本项目的 **HCS 后端为主**、**gRPC 后端为回退**：提权时自动使用 HCS，未提权时回退到 `WindowsSandboxServer` gRPC（`wsm elevate` 可提权重启）。

### 11.1 Q1：能否自定义基础系统镜像层位置？——**能（HCS 层）**

证据：

- 本机 `C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>\Files` 即 OS 基础镜像文件系统；
  注册表 `HKLM\...\Containers\CmService\Layers\<id>` 含 **`Path`**、`ParentType/ParentId` 链、
  `LayerMetadata`（`LayeringMode: Wcifs`、`SnapshotActions`、`FirstBootActions`、`SysprepActions`、`SpecializationFlags`）。
- HCS JSON 把基础 OS 暴露给 guest 的字段均可指向任意绝对路径：
  - `VirtualMachine.Devices.VirtualSmb.Shares[].Path`（基础层 `Files` 目录）
  - `VirtualMachine.RunInSilo.SiloBaseOsPath`（Sandbox 扩展）
  - HCS schema `Layer { Id, Path, PathType, Cache }`
- 原生层/镜像 API：`vmcompute!CreateBaseImageVHD`、`CreateBaseImageVHDWithFolders`、`CreateLayer`、
  `CreateSandboxLayer`、`ImportLayer`、`ExportLayer`、`MountVhdForSetup`、`GetLayerMountPath`、`PrepareLayer`、`ActivateLayer`。

限制：Sandbox 自带栈（`CmService` / `WindowsSandboxServer` / `ManagedWindowsVM`/`VMOptions`）
**不暴露**基础镜像选择（`VMOptions` 无此字段，`GetVMOptionsFromRecipe` 也不设置），因此**只有 HCS 直连**可自定义。

本项目实现：`HcsHostLayout.ResolveBaseLayerPath()` 从注册表解析最新可用基础层（本机实测解析到
`Layers\e01ff4dc-...\Files`，OS 10.0.26200.9445），并写入 `HcsSandboxJson` 的
`VirtualSmb.Shares[0].Path` 与 `RunInSilo.SiloBaseOsPath`。

### 11.2 Q2：能否自定义用户数据位置？——**位置仅 HCS 直连可改**

证据：

- 用户数据落盘于 `C:\ProgramData\Microsoft\Windows\Containers\ContainerStorages\<id>\sandbox.vhdx`（+ `sandbox.vmgs`）。
- HCS JSON：`VirtualMachine.Devices.Scsi["primary"].Attachments["0"].Path` 可指向任意 VHDX。
- `SandboxCommon.Config.StorageConfiguration { StoragePath, DiskSizeInMB, DiskEncryption }` 存在，
  但 `WindowsSandboxServer` 的 `GetVMOptionsFromRecipe` **不映射**它；UDK `VMOptions` 仅有
  **`MaxStorageSizeMB`**（大小，非路径），`VMStorage` 仅暴露 `Id`。
- 原生存储 API：`CmsCreateStorage` / `CmsOpenContainerStorage` / `CmsMapStorageToContainer` /
  `CmsMapVirtualDiskToContainer` / `CmsResizeStorage` / `CmsCreatePersistedStorage`。

结论：**大小**可配（`VMOptions.MaxStorageSizeMB` / HCS `Scsi` 容量）；**位置**只能经 HCS 直连
（`Scsi.Attachments[].Path`）或 `Cms*`。

本项目实现：`SandboxRecipe.Storage`（`SandboxStorageOptions { StoragePath, DiskSizeMb, Encryption }`）
→ `HcsSandboxBackend` 用 `HcsStorageManager.Provision()` 在自定义目录准备 `sandbox.vhdx`/`sandbox.vmgs`
（可从现有容器存储播种），并把路径写入 `Scsi.Attachments[].Path` 与 `GuestState.GuestStateFilePath`。

### 11.3 Q3：能否做检查点？——**能**

证据：

- 本机存在 `C:\ProgramData\Microsoft\Windows\Containers\Snapshots\<id>\SnapshotSavedState.vmrs`。
- 原生：`computecore!HcsSaveComputeSystem(computeSystem, operation, options)`；HCS schema：
  - `SaveOptions { SaveType, SaveStateFilePath }`，`SaveType ∈ { "ToFile", "AsTemplate" }`
    （ToFile=内存+设备全状态；AsTemplate=仅设备状态、可被克隆）。
  - `RestoreState { SaveStateFilePath, TemplateSystemId }`。
  - `GuestState { GuestStateFilePath, RuntimeStateFilePath, ForceTransientState }`。
  - `HcsCreateEmptyGuestStateFile` / `HcsCreateEmptyRuntimeStateFile` / `vmcompute!CreateDifferencingVHD`。
- Windows Sandbox 自身用 `VMTerminatedReason.UserInitiatedShutdownForReboot` + `RebootSupport`
  在内部走模板保存/恢复（`new ManagedWindowsVM(options, _vm.Storage)`），即**已在用检查点**。

本项目实现：`HcsCheckpointManager` 用 `HcsSaveComputeSystem` + `SaveOptions` 保存，
落到 `Snapshots\<id>\SnapshotSavedState.vmrs`；恢复时以 `RestoreState.SaveStateFilePath`
（或 `TemplateSystemId`）创建新 compute system。CLI：`wsm checkpoint save|list|restore`。

### 11.4 HCS JSON 字段映射（本实现）

| 能力 | HCS 字段 |
| --- | --- |
| 基础镜像层 | `VirtualMachine.Devices.VirtualSmb.Shares[0].Path`；`VirtualMachine.RunInSilo.SiloBaseOsPath` |
| 用户数据盘 | `VirtualMachine.Devices.Scsi.primary.Attachments["0"].Path` |
| Guest 状态 | `VirtualMachine.GuestState.GuestStateFilePath` |
| CPU | `VirtualMachine.ComputeTopology.Processor.Count` |
| 内存 | `VirtualMachine.ComputeTopology.Memory.SizeInMB` |
| vGPU | `VirtualMachine.Devices.EnhancedModeVideo.ConnectionOptions` |
| 网络 | `VirtualMachine.Devices.NetworkAdapters["0"].EndpointId`（HNS endpoint） |
| 目录映射 | `VirtualMachine.Devices.VirtualSmb.Shares[]`（host→guest 附加 share） |
| 检查点保存 | `HcsSaveComputeSystem` + `SaveOptions` |
| 检查点恢复 | `VirtualMachine.RestoreState.{SaveStateFilePath,TemplateSystemId}` |
| HvSocket 服务 | `VirtualMachine.Devices.HvSocket.HvSocketConfig.ServiceTable`（含 LSM `F58797F6-…`、NLM `A715AC94-…`） |

### 11.5 原生 API 清单（本实现使用）

- `computecore.dll`：`HcsCreateOperation` / `HcsWaitForOperationResult`（结果串用 `LocalFree` 释放）/
  `HcsCloseOperation` / `HcsCreateComputeSystem` / `HcsOpenComputeSystem` / `HcsCloseComputeSystem` /
  `HcsStartComputeSystem` / `HcsShutDownComputeSystem` / `HcsTerminateComputeSystem` /
  `HcsSaveComputeSystem` / `HcsGetComputeSystemProperties` / `HcsEnumerateComputeSystems` /
  `HcsCreateEmptyGuestStateFile` / `HcsCreateEmptyRuntimeStateFile` / `HcsGrantVmAccess`。
- 仅参考（未封装，签名非公开）：`vmcompute.dll` 的 `CreateBaseImageVHD`、`CreateDifferencingVHD`、
  `CreateLayer`、`CreateSandboxLayer`、`ImportLayer`、`MountVhdForSetup`、`GetLayerMountPath`。
- 容器管理：`cmclient.dll` 的 `CmsCreateStorage` / `CmsOpenContainerStorage` /
  `CmsMapStorageToContainer` / `CmsMapVirtualDiskToContainer` / `CmsResizeStorage`。

### 11.6 已知边界

- 用**裸 HCS** 从零创建可启动的 Sandbox 需要完整复刻 CmService 的镜像/层/模板准备流程；
  本实现假定基础层与用户数据盘已存在（可由运行一次 Windows Sandbox 或既有 `ContainerStorages` 播种）。
  因此 `HcsSandboxBackend.StartAsync` 属**实验性**，并对缺失前置条件给出明确错误。
- 运行期热映射目录、in-guest 进程执行在 HCS 后端未实现（`Capabilities` 如实声明为 false），
  这些场景由 gRPC 后端承担。
- HCS 的 guest IP 由 HNS 维护，未从 HCS 属性直接暴露。

---

## 12. Windows Sandbox 的「系统镜像」原理，以及 HCS 为何能复用它

### 12.1 结论

**能。** Windows Sandbox 的「系统镜像」**不是一块可启动的 VHD**，而是一个**容器层（layer）目录**。
HCS 只是**按路径引用**该层；真正把层「拼装」成 guest 可见的 `C:` 的是容器运行时（`container.dll` +
`wcifs` 过滤驱动），由 HCS JSON 里的 `RunInSilo` 触发。因此 HCS 可以复用 Sandbox 的同一镜像——
前提是该层已在 `CmService` 注册（Windows Sandbox 会注册）。

### 12.2 系统镜像的组成（本机证据）

`C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>\`：

| 项 | 内容 | 说明 |
| --- | --- | --- |
| `Files\` | 完整 Windows 目录树（`Windows`/`Program Files`/`Users`/`EFI`…） | 只读 OS 文件系统；顶层多为 **reparse point**（层链/`ReparseBaseLayer`） |
| `Hives\*_BASE` | `SYSTEM_BASE`/`SOFTWARE_BASE`/`SAM_BASE`/`SECURITY_BASE`/`DEFAULTUSER_BASE` | **符号链接**指向 `Files\Windows\System32\Config\{SYSTEM,SOFTWARE,…}`，供注册表并集使用 |
| `Specialization\` | `Windows\{SYSTEM,SOFTWARE,…}`（hive）+ `config.json` | 特化（sysprep）后的注册表与元数据（语言/.NET/Edge/热补丁/BootFeature hash） |
| `Bindings\config.json` | `{"Version":1}` | 文件系统命名空间绑定 |
| `dotnet.bcf` | 二进制配置 | .NET 相关层配置 |

注册表 `HKLM\...\Containers\CmService\Layers\<id>` 保存层注册信息：

```
Path          = C:\ProgramData\Microsoft\Windows\Containers\Layers\<id>
ParentType    = 2                 # 有父层（层链）
ParentId      = <上一版层 id>      # 每次 Windows 更新产生新层
OSVersion     = 10.0.26200.9445
StorageId     = <ContainerStorages 下的可写 scratch>
SequenceNumber= 17
LayerMetadata = { "LayeringMode":"Wcifs",
                  "WcifsConfiguration":{"LayerDescriptors":[{"LayerId":"6af57529-…","Path":"%SYSTEMDRIVE%"}]},
                  "OverlayDirectories":[{"RelativePath":"Windows\\Servicing\\Packages"},
                                        {"RelativePath":"Windows\\WinSxS"}],
                  "CreateZygote":true, "SnapshotActions":1, "FirstBootActions":1,
                  "SysprepActions":1, "SpecializationFlags":31, "MapEdge":true }
```

`CmService.dll` 字符串进一步印证：`BaseImages`/`DeploymentManager_CreateOrOpenBaseImage`、
`Hives`/`hivestack`、`ReparseBaseLayer`、`\WcifsPort`、`CreateZygote`/`CloneTemplate`/`RuntimeTemplateId`、
`Snapshots`/`SnapshotSavedState.vmrs`/`DeploymentManager_CreateOrOpenSnapshot`、`AsTemplate`/`SavedAsTemplate`。

### 12.3 HCS 如何消费该镜像（原理链）

1. **vSMB 只读暴露**：`VirtualMachine.Devices.VirtualSmb.Shares[0]` =
   `{ Name:"os", Path:<Layer>\Files, ReadOnly:true, ReparseBaseLayer:true, … }`。
   宿主 `vmusrv` 通过 vSMB 把该层以只读方式提供给 guest。
2. **Silo 基路径**：`VirtualMachine.RunInSilo.SiloBaseOsPath` = `<Layer>\Files`；
   `RunInSilo.FileSystemLayers[0]` = `{ Id:"8264f677-40b0-4ca5-bf9a-944ac2da8087"(HOST_LAYER_ID),
   Path:"C:\\", PathType:"AbsolutePath" }`。
3. **引导**：`VirtualMachine.Chipset.Uefi.BootThis` = `{ DeviceType:"VmbFs",
   DevicePath:"\\EFI\\Microsoft\\Boot\\bootmgfw.efi" }` —— 从 vSMB 提供的 OS 启动。
4. **wcifs 拼装**：`RunInSilo` 使 `vmcompute` 调用 `container!WcCreateContainer` →
   `wc_storage!WciSetupFilter` → `FLTLIB!FilterSendMessage(\WcifsPort)`。
   `wcifs.sys` 把 **只读基础层（vSMB）** 与 **可写 scratch** 做并集；注册表经
   `Hives\*_BASE` + `Specialization` hive 做 `hivestack` 并集。
5. **可写层**：`VirtualMachine.Devices.Scsi.primary.Attachments["0"].Path` =
   `ContainerStorages\<id>\sandbox.vhdx`（scratch/用户数据，动态 VHDX，4MB 起，实测 88MB / 917MB）；
   `GuestState.GuestStateFilePath` = `sandbox.vmgs`。

> 关键：**HCS JSON 本身不列注册表 hive**；注册表并集由 `RunInSilo` 触发的容器运行时依据
> **已注册的 layer**（`Files`+`Hives`）完成。所以只要层已注册、`RunInSilo` 存在，注册表/特化也会一并复用。

### 12.4 检查点与「合子」（Zygote / Template）

- `HcsSaveComputeSystem` + `SaveOptions{ "SaveType":"ToFile"|"AsTemplate", "SaveStateFilePath":… }`
  → `Snapshots\<id>\SnapshotSavedState.vmrs`（实测 ~371MB，即 VM 运行时状态）。
- `CreateZygote`/`CmZygote`/`CloneTemplate`/`GetCloneTemplate`/`RuntimeTemplateId` +
  `RestoreState.TemplateSystemId` = **预启动的模板 VM（合子）**，克隆由此 fork → 沙箱秒开；
  `RebootSupport` 即用 `AsTemplate` 走这条路径。
- 持久化：`PersistedStorages` / `PersistenceSupport`。

### 12.5 对本项目的意义

- `HcsHostLayout.ResolveBaseLayerPath()` 从注册表解析最新可用层（本机解析到
  `Layers\e01ff4dc-…\Files`）。
- `HcsSandboxJson` 已把该路径写入 `VirtualSmb.Shares[0].Path` 与 `RunInSilo.SiloBaseOsPath`
  （并含 `ReparseBaseLayer`、`FileSystemLayers`、`Uefi VmbFs` 引导、LSM/NLM HvSocket、EnhancedModeVideo）。
- 仍需的前置条件：**可写 scratch VHDX**（`HcsStorageManager` 从既有 `ContainerStorages` 播种），
  以及（可选，用于秒开/克隆）**模板 system id**。
- 因此「HCS 复用 Sandbox 系统镜像」在原理与实现上均成立；冷启动不依赖模板，模板只影响启动速度。
