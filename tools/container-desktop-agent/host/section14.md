
---

## 14. 把容器桌面接进管理 service / Container desktop in the manager

> §13 给出了机制与实测；本节把它落进项目：**能力由管理 service 承载**（service 是 SYSTEM，
> 既能在容器里起 agent，也能把窗口创建到用户 Console），CLI/UI 只是它的驱动方。

### 14.1 分层 / Where it lives

| 层 | 位置 | 承担什么 |
|---|---|---|
| 引擎能力 | `Wsm.Engines.Abstractions/IContainerFileSystemAccess.cs` + `WindowsContainerEngine` 实现 | 暴露**运行中容器的宿主可见根**（`\\?\Volume{GUID}\`）：这是把 agent 放进容器、以及"容器里的普通文件=共享内存"的前提 |
| 连接层 | `src/30-Connections/Wsm.Connection.ContainerDesktop/` | `IContainerDesktopHost` 编排：materialise agent → 在容器内启动 → 等第一帧 → 可选**在交互会话里开窗**；`ISessionProcessLauncher` 封装 `WTSQueryUserToken`+`CreateProcessAsUser`（只有 SYSTEM 能跨会话） |
| 容器内 | `Agent/container-desktop-agent.ps1`（嵌入资源） | 抓屏进共享内存 + 轮询输入队列 + `SetCursorPos`/`mouse_event`/`keybd_event` 注入；响应 `agent-stop.txt` 退出 |
| 服务 | `Wsm.Service.Host`（`WsmServiceModule` 依赖新模块） | 以 SYSTEM 承载同一套组件 → 窗口落在用户 Console |
| CLI | `wsm desktop …` / `wsm desktop-view …`（`DesktopViewCommand`） | 驱动与查看器窗口（WinForms；`wsm.exe` 本身就是查看器宿主，与 `wsm connect` 的既有模式一致） |

新增/改动文件：`Wsm.Connection.ContainerDesktop`（csproj、`ContainerDesktopOptions`、`ContainerDesktopSession`、
`IContainerDesktopHost`、`ISessionProcessLauncher`、`Internal/ContainerDesktopHost`、`Internal/SessionProcessLauncher`、
模块、`Agent/container-desktop-agent.ps1`）、`IContainerFileSystemAccess`、`WindowsContainerEngine`（实现该能力）、
`CliApplication`/`CliModule`/`Wsm.Cli.csproj`、`WsmServiceModule`/`Wsm.Service.Host.csproj`、`WindowsSandboxManager.slnx`、
`tests/…/Connection/ContainerDesktopHostTests.cs`。

### 14.2 命令 / Commands

```powershell
# WSM 自己管的沙箱（引擎用新能力解析容器根）
wsm desktop <sandboxId> --attach

# 任意运行中的容器（工具/另一个所有者，例如代理服务持有的容器）
wsm desktop --compute-system <id> --container-root \\?\Volume{GUID} --attach
wsm desktop --compute-system <id> --container-root \\?\Volume{GUID} --input "keys F3"
wsm desktop --compute-system <id> --container-root \\?\Volume{GUID} --stop

# 查看器（`--attach` 会自动用同一条命令启动它；也可手工跑）
wsm desktop-view --frame \\?\Volume{GUID}\wsm\frame.shm --seconds 120
```

`--attach` 是否落在**用户 Console**取决于是谁在跑：以 **SYSTEM** 跑（服务）→ `WTSQueryUserToken`+
`CreateProcessAsUser` 把窗口放进交互会话（§13.7 已单独验证）；以**普通/提权管理员**跑（CLI）→ 回退成
"在调用者自己会话里启动"，因此用户在终端里敲 `wsm desktop … --attach` 也能直接看到窗口。

### 14.3 本次实现顺带修掉/发现的问题 / Bugs found on the way

| 问题 | 说明 |
|---|---|
| **`Path.Combine("\\?\Volume{…}", "C:\wsm")` 返回 `C:\wsm`** | 第二个参数是**有根**路径时会**替换**第一段 ⇒ 宿主去找自己的 `C:\wsm`（实测在那台机器上真的建出了 `C:\wsm\container-desktop-agent.ps1` 与 `agent-stop.txt`，已清理）。修法：把容器内目录转成**相对**路径再合并（`ToRelativeContainerPath`），并加回归测试 |
| 重复调用会重启 agent | 服务/多次 CLI 调用会对同一个 `frame.shm` 打架 ⇒ `AttachAsync` 先读帧头，**已存在就"收养"**（幂等），而不是重置目录再启动 |
| 短命进程无法 `--stop` | 会话表是进程内的 ⇒ 增加 `StopAsync(computeSystemId, containerRoot)`，直接写停止标记 |
| PowerShell 5.1 的 `0xC0000000`/`0xFFFFFFFF` 字面量 | 见 §13.4（容器内 agent 已用十进制 `[uint32]` 写法） |

### 14.4 验证状态 / Verification status

- **编译**：`wsm`(CLI) 与 `Wsm.Service.Host` 均 **0 警告 0 错误**；`dotnet test` **293 通过**（含 9 个新用例：
  `ToRelativeContainerPath`、容器根不被替换的回归断言、能力开关、缺引擎报错、空会话 stop）。
- **机制**：§13 已端到端证明（共享内存帧 → 宿主读帧；注入 `F3` → 前台窗口变 `Find`；SYSTEM → 用户 Console 开窗）。
- **本次未能跑通的**：CLI 级端到端（`wsm desktop … --attach`）被**宿主容器栈**挡住——
  连**全新**容器都 `HcsCreateProcess` 失败 `0x80370105`（"当前状态无效"），且新容器起来时 `ip=` 为空
  （HNS 没给地址），`cmd.exe /c echo alive` 在容器里也失败。这是**机器态**问题（多轮容器创建/销毁之后
  `vmcompute`/`hns` 状态需要重置），不是本功能的缺陷。恢复步骤：重启 `vmcompute`/`hns`（或重启机器），
  然后用上面的命令复核；WSM 自己 `wsm start` 出来的沙箱还能顺带走**引擎解析容器根**的产品路径。

### 14.5 后续 / Next

1. 给 gRPC 契约加 `StartDesktop/StopDesktop/GetDesktop`（`wsm_service.proto` + `WsmServiceGrpc` + 客户端），
   让 GUI（WinUI）通过代理服务请求桌面，而不是自己起进程；
2. 输入通道换成 HCS **`MappedPipes`**（官方跨 silo 通道），替掉"每命令一个文件"；
3. 画面换成**页文件节 + 提权句柄直通**（§13.6 A 已验证可行）→ 去掉 NTFS 脏页写回；
4. 脏矩形 + 压缩（现在 3 MB/帧 × 5 fps 全量）。
