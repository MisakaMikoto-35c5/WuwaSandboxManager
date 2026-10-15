
### 13.7 让 SYSTEM 把窗口显示到用户 Console（已实现并验证）/ A SYSTEM process showing a window on the user console

「不同会话也能共享窗口」里**成立且必需**的那一半：宿主侧 **SYSTEM 服务把 UI 显示到用户交互会话**。
这正是 WSM 这种提权/服务形态必须具备的能力（否则服务在 session 0，用户永远看不到窗口）。

| 脚本（`tools/container-desktop-agent/host/`） | 作用 |
|---|---|
| `sessions.ps1` | 报告本进程看到的**会话 / 窗口站 / 桌面**（容器与宿主各跑一次做对照） |
| `launch-in-console.ps1` | `WTSQueryUserToken(console)` → `DuplicateTokenEx` → `CreateProcessAsUser(..., lpDesktop="winsta0\default")`；必须**以 SYSTEM 运行**（需要 `SeTcbPrivilege`，UAC 提权的 admin 令牌没有） |
| `run-assystem.ps1` | 用一次性计划任务（`schtasks /ru SYSTEM /rl HIGHEST`）执行上面的脚本，跑完即删 |

**会话对照（同一脚本两边跑）：**

| | 容器内 | 宿主 |
|---|---|---|
| 自身会话 | `4` | `1` |
| `WTSGetActiveConsoleSessionId()` | **5**（它自己的 console） | **1** |
| 进程窗口站 | `Service-0x0-…` | `WinSta0` |

⇒ 容器自认的 console(5) 与用户 console(1) 不是同一个会话，窗口站也不是同一个对象。

**实测（`launch.log`，SYSTEM 进程写的）：**

```
identity: NT AUTHORITY\SYSTEM        own pid=101168 session=0 ; active console session=1
WTSQueryUserToken(1) ok=True token=0x970
DuplicateTokenEx     ok=True primary=0x93C
CreateProcessAsUser  ok=True pid=102056   (winsta0\default, session 1)
child pid=102056 session=1  (should equal 1)
```

**验证（宿主侧查询）：**

```
Id      SessionId MainWindowTitle
102056  1         WSM - container desktop over shared memory   frame 398
```

即：**session 0 的 SYSTEM 进程把子进程放进了 session 1（用户 Console）**，而这个窗口里显示的正是
**容器桌面**（共享内存里的第 398 帧）。⇒「System 级别会话在用户 Console 显示窗口」**成立**，
并且这就是「把容器桌面呈现给用户」的产品级落点：**画面来自句柄直通/共享内存，窗口由 SYSTEM 服务
按会话创建**，全程不需要 RDP，容器里也不需要任何 listener。

（附注：在这个 SYSTEM 任务里 `AdjustTokenPrivileges` 对 `SeTcb/SeAssignPrimaryToken/SeIncreaseQuota/SeDebug`
都回 `err 1300`，但 `CreateProcessAsUser` 仍然成功——SYSTEM 令牌默认已带所需特权，显式开启只是保险。）

**同时锁定的边界**：这只解决「把（我们自己构造的）窗口显示在用户 Console」；**容器的原生窗口仍然
跨不过来**（§13.6 B：`FindWindow` 看不到、`SetParent`/`SetThreadDesktop` 都不通）。要真正的原生窗口，
只有 **Hyper-V 隔离 + 增强会话**。
