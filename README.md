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

## 两种控制面

| | **HCS 后端（主）** | **gRPC 后端（回退）** |
| --- | --- | --- |
| 入口 | `computecore.dll`（operation-based） | `WindowsSandboxServer` gRPC over named pipe |
| 提权 | **需要管理员 / Hyper-V 管理员** | 不需要 |
| 多实例 | ✅ 无许可门槛 | ⚠️ 受许可策略限制（`CO_E_APPSINGLEUSE`） |
| CPU 核数 | ✅ `Processor.Count` | ✖ |
| 自定义用户数据位置 | ✅ `Scsi.Attachments[].Path` | ✖（仅大小） |
| 自定义基础镜像层 | ✅ `VirtualSmb.Shares` / `RunInSilo` | ✖ |
| 检查点 | ✅ `HcsSaveComputeSystem` | ✖ |
| 运行期热映射目录 | ✖（创建时映射） | ✅ `MapFolder` |
| in-guest 进程执行 | ✖ | ✅ `CreateProcess` |
| 获取 IP | ✖（由 HNS 维护） | ✅ `GetIpAddress` |

引擎在提权时**自动选用 HCS**，否则回退 gRPC。`wsm env` 会显示当前选中的后端与能力。

---

## 架构（DDD 分层）

```
┌─────────────────────────────┐        ┌──────────────────────────────┐
│  WindowsSandboxManager.App  │        │ WindowsSandboxManager.Cli    │
│  (WinUI3 + MVVM)            │        │ (wsm, 控制台)                │
└──────────────┬──────────────┘        └──────────────┬───────────────┘
               │  ISandboxApplicationService          │
┌──────────────▼──────────────────────────────────────▼───────────────┐
│  WindowsSandboxManager.Application  (用例 / DTO / 后端选择器)         │
└──────────────┬───────────────────────────────────────────────────────┘
               │  ISandboxBackend, ISandboxSessionConnector, ISandboxRepository,
               │  ISandboxRecipeSerializer, ISandboxEnvironment   (端口)
┌──────────────▼───────────────────────────────────────────────────────┐
│  WindowsSandboxManager.Infrastructure                                │
│   • Hcs/      : HcsService, HcsSandboxBackend, HcsSandboxJson,        │
│                 HcsHostLayout, HcsStorageManager, HcsCheckpointManager│
│   • Grpc/     : SandboxCore.proto + 命名管道通道 + ServerBackend       │
│   • Connectors: WindowsSandboxSessionConnector, RdpBaseNamedPipe...   │
│   • Serialization: WsbRecipeXmlSerializer (.wsb)                      │
│   • Repositories / Environment / Elevation / MultiInstancePolicy      │
└──────────────┬───────────────────────────────────────────────────────┘
               │
┌──────────────▼───────────────────────────────────────────────────────┐
│  WindowsSandboxManager.Domain                                        │
│   SandboxInstance(聚合根) · SandboxRecipe · StorageOptions ·          │
│   Checkpoint · 值对象 · 领域事件 · 端口                                │
└──────────────────────────────────────────────────────────────────────┘
```

---

## 构建与运行

需要 **.NET 10 SDK**、Windows 11（已启用 Windows Sandbox 可选功能）。
HCS 后端需要以管理员运行。

```powershell
dotnet build WindowsSandboxManager.slnx
dotnet test  WindowsSandboxManager.slnx

# CLI
dotnet run --project src\WindowsSandboxManager.Cli -- env

# WinUI3 GUI（HCS 需要管理员，可右键“以管理员身份运行”）
dotnet run --project src\WindowsSandboxManager.App
```

---

## CLI（`wsm`）用法

```text
wsm env                        显示主机环境、提权状态与后端能力
wsm list / running             列出实例 / 运行中实例
wsm hcs                        列出 HCS compute system（需提权）
wsm layers                     显示层/存储/快照布局并解析基础镜像层
wsm create [options]           创建实例定义
wsm show <id>                  查看实例
wsm start <id> [--remoteapp]   启动
wsm stop <id>                  停止
wsm connect <id> [--remoteapp] 连接 RDP 会话
wsm ip <id>                    获取 guest IP（gRPC 后端）
wsm exec <id> <command>        在沙箱内执行命令（gRPC 后端）
wsm share <id> <host> [sbx] [--rw]   运行期热映射目录（gRPC 后端）
wsm checkpoint save <id> <name> [--template]  保存 HCS 检查点
wsm checkpoint list <id>                      列出检查点
wsm checkpoint restore <id> <checkpointId>    恢复检查点
wsm multi [--enable|--disable] 查看/切换多实例许可策略（gRPC 后端用）
wsm elevate                    提权重启（启用 HCS 后端）

create 选项：
  --name <名称>
  --memory <MB>                默认 4096
  --cpu <核数>                 HCS 后端
  --networking enable|disable|default
  --vgpu enable|disable|default
  --rail                       以 RemoteApp(RAIL) 启动
  --fullscreen / --protected
  --storage-path <目录>        自定义用户数据位置（HCS 后端）
  --disk-size <MB>             用户数据盘大小
  --map "C:\host;C:\guest;rw"
  --logon "cmd.exe /c echo hi"
```

示例（HCS，需管理员）：

```powershell
wsm elevate
wsm layers                                             # 确认基础镜像层
$id = (wsm create --name Demo --cpu 8 --memory 8192 --storage-path E:\sbx --networking enable)
wsm start $id
wsm checkpoint save $id snap1 --template
wsm checkpoint list $id
wsm stop $id
```

---

## 三项能力（HCS 直连）

| 问题 | 结论 | 关键 HCS 字段 / API |
| --- | --- | --- |
| 自定义基础镜像层位置 | **允许**（HCS 层） | `VirtualSmb.Shares[0].Path`、`RunInSilo.SiloBaseOsPath`；`Layer.Path`；`vmcompute!CreateBaseImageVHD/CreateLayer/ImportLayer` |
| 自定义用户数据位置 | **允许**（位置仅 HCS） | `Scsi.primary.Attachments["0"].Path`；`CmsCreateStorage`/`CmsMapVirtualDiskToContainer` |
| 检查点 | **允许** | `HcsSaveComputeSystem` + `SaveOptions{ToFile\|AsTemplate}` + `RestoreState`；落盘 `Snapshots\<id>\SnapshotSavedState.vmrs` |

引擎实现：

- 基础镜像层：`HcsHostLayout.ResolveBaseLayerPath()` 从 `HKLM\...\Containers\CmService\Layers` 解析最新可用层
  （本机实测解析到 `Layers\<id>\Files`）。
- 用户数据：`SandboxRecipe.Storage`（`SandboxStorageOptions`）→ `HcsStorageManager.Provision()` 在自定义目录
  准备 `sandbox.vhdx`/`sandbox.vmgs`。
- 检查点：`HcsCheckpointManager` 保存/枚举/恢复；`SaveType=AsTemplate` 对应 Windows Sandbox 内部用于快速重启的模板。

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
- `HCS` 的 guest IP 由 HNS 维护，未从 HCS 属性直接暴露。
- `WindowsSandboxServer` gRPC 契约与 `RDPBASE`/`computecore`/`cmclient` 接口属**非公开**接口，可能随版本变化。
- `ISandboxEnvironment.PackageInstallPath` 在非提权下可能为 `null`。
- WinUI3 项目需 `x64`。

---

## 许可

示例代码仅供学习与研究使用。所依赖的非公开接口归 Microsoft 所有。
