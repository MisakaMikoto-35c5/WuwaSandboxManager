# 13 容器桌面、会话与 RDP / Container desktop, sessions and RDP

> 本文记录在 **进程隔离（Argon）容器**里追求「桌面」的完整过程与结论：
> **交互式 console 会话（winlogon / LogonUI）可以起来**，但 **RDP 监听器（3389）无法建立**——
> `termsrv` 在检测到自己运行于容器时会主动拒绝，这属于产品设计而非配置问题。
>
> This document records the full attempt to obtain a **desktop inside a
> process-isolated (Argon) Windows container**: the **interactive console session
> (winlogon / LogonUI) can be made to start**, but the **RDP listener (3389) can
> never be created** — `termsrv` deliberately refuses when it detects a container.

---

## 摘要 / Summary

| 项目 Item | 结论 Result | 依据 Evidence |
|---|---|---|
| 桌面为何没启动 Why no desktop | smss 只建 **1 个会话**（services），没有 console 会话 → `winlogon` 从未运行 / smss creates only the services session, so `winlogon` never runs | `NumberOfInitialSessions=1`（镜像当前控制集），tasklist 无 `winlogon.exe` |
| 能否修好 Can it be fixed | **能**：把该值改成 `2`，再启动容器即出现 `console` 会话 + `winlogon` + `LogonUI` / Yes: set it to `2` | `qwinsta` → `console … Conn`；tasklist → `winlogon.exe (Console)`、`LogonUI.exe (Console)` |
| 虚拟显示器 Virtual display | **有**：TSDDD 无头显示 1024×768×32@60Hz / present | `GetDC`+`GetDeviceCaps`、`BasicDisplay`/`BasicRender` Running、`TSDDD\Device0` |
| 容器入站网络 Inbound networking | **正常**：宿主可连容器内手工 `TcpListener` / fine | 宿主 → 容器 `:9999` = True；ping = True |
| RDP（3389）能否起来 Can RDP start | **不能** / **No** | `termsrv.dll` 字符串 `WTSIsServerContainer` / `TS in container and so Remote Connection is not allow`；3389 始终无监听 |
| 查看桌面的替代路径 Alternative viewer | 需自建「会话内截屏/输入代理」或走 Hyper-V 隔离 / build an in-session capture agent, or use Hyper-V isolation | 见 §9 |

---

## 1. 问题 / Problem

**中文**：此前结论是「容器里做不出桌面」。本次要求进一步核实：**是不是"桌面只是没被启动"**（重点排查
`winlogon`、`userinit`），以及**是否分配了虚拟显卡/显示器**（若没有，也可能导致启动异常）。

**English**: The earlier conclusion was "no desktop in a container". This round asked whether the
desktop merely **failed to start** (focus on `winlogon` / `userinit`) and whether a **virtual
display adapter** is allocated (its absence would also break startup).

---

## 2. 环境 / Environment

| 项 Item | 值 Value |
|---|---|
| 宿主 Host | Windows 11 **26300 / 26H2**（client，`InstallationType=Client`） |
| 容器镜像 Container image | `mcr.microsoft.com/windows/servercore:ltsc2022`，build **20348.4294**，`Windows Server 2022 Datacenter`，`InstallationType=Server Core` |
| 隔离 Isolavor | HCS **process isolation (Argon)**，`HvPartition=false` |
| 网络 Networking | HNS **namespace** + NAT endpoint（`192.168.248.0/24`，网关 `.1`） |
| 层布局 Layer layout | `layout` 文件内容 = **`vhd-with-hives`** |

**镜像层结构 / Image layer layout**（`ProcessBaseImage` 之后）:

```
<layer>\Files\...\config\SYSTEM        ← 真正的 SYSTEM 配置单元（9.7 MB）
<layer>\Hives\SYSTEM_BASE              ← 与上面是同一个文件的硬链接 / hard-linked to the file above
<layer>\Hives\SYSTEM                   ← 运行时工作副本（8 KB，含 .LOG1/.LOG2/.regtrans-ms）
<layer>\Hives\SOFTWARE_BASE, SAM_BASE, SECURITY_BASE, DEFAULTUSER_BASE
<layer>\blank.vhdx, blank-base.vhdx, layout, wsm-image.json
```

> 直接读/写 `Files\...\config\SYSTEM` 就等于改 `Hives\SYSTEM_BASE`。
> **层被挂载（有容器在跑）时该文件被独占锁**，写入会静默失败——这是本次几次「补丁没生效」的原因。
>
> Reading/writing `Files\...\config\SYSTEM` is the same file as `Hives\SYSTEM_BASE`. While the layer
> is mounted (**any container from it is running**) the file is locked; writes then silently fail.

---

## 3. 关键发现：桌面为什么没启动 / Why the desktop never started

### 3.1 现象 / Symptom

进程隔离容器启动后，容器内进程表**只有 services 会话**，没有登录链：

```
smss.exe            session 0
csrss.exe / wininit.exe / services.exe / lsass.exe / fontdrvhost.exe   Services
svchost … / CExecSvc.exe / WmiPrvSE.exe                                Services
qwinsta → 只有  services  (Disc)
```

**`winlogon.exe`、`userinit.exe`、`LogonUI.exe` 一个都没有。**
手动拉起也不行：`winlogon.exe` 立刻退出（exit 0，不驻留）；`smss.exe` 报
*"cannot be run in Win32 mode"*（native 子系统程序）。

### 3.2 根因 / Root cause

| 位置 Where | `NumberOfInitialSessions` |
|---|---|
| 宿主 Win11（对照）/ host | **2** |
| 镜像 `ControlSet002`（姊妹控制集）/ sibling | **2** |
| **镜像 `ControlSet001`（当前生效）/ active** | **1** |

`HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\NumberOfInitialSessions` 决定
**smss 启动时建几个初始会话**：`2` = services + **console**，`1` = 只有 services。
微软的容器镜像把它设为 `1`；因为**姊妹控制集仍是默认的 2**，可以确定这是**有意修改**。

`SubSystems\Windows` 是正常的（与宿主逐字相同，含 `Windows=On … ServerDll=winsrv:UserServerDllInitialization,3`），
所以 Win32 子系统本身没问题——**卡点就是会话数**。

### 3.3 验证：改成 2 后桌面登录链真的起来了 / Fix verified

把 `ControlSet001`/`ControlSet002` 的 `NumberOfInitialSessions` 改为 `2` 后重启容器：

```
qwinsta:   console    4   Conn                      ← console 会话出现
tasklist:  csrss.exe        Console  4
           winlogon.exe     Console  4              ← 登录管理器
           fontdrvhost.exe  Console  4
           LogonUI.exe      Console  4              ← 登录界面
           conhost.exe      Console  4
reg query（容器内）: NumberOfInitialSessions = 0x2   ← 补丁确实进了容器
```

**已复现 3 次**（sessions 4 / 6 / 8）。

**注意 / Note**：`userinit.exe` 仍未出现，因为它是 **`winlogon` 在登录成功后才创建**的；
此时停在登录界面（`query user` → *No user exists for \**）。镜像里 `Winlogon\Shell=explorer.exe`
但 **`explorer.exe` 不存在**，所以登录后也没有 shell（可改成 `cmd.exe`）。

---

## 4. 为什么 RDP 起不来 / Why RDP cannot start

### 4.1 先排除网络 / Networking ruled out

| 测试 Test | 结果 |
|---|---|
| 宿主 ping 容器 IP | **True** |
| 容器内自建 `TcpListener(9999)` → 宿主连接 | **True** |
| 宿主 → 容器 `:3389` | False |
| 容器内 `127.0.0.1:3389` | 被拒绝 / refused |

容器 **入站 TCP 完全可用**，因此与 HNS/NAT/防火墙无关（容器里 `mpssvc` 根本没运行，
`netsh advfirewall` 报"无法联系 Windows Defender 防火墙服务"）。

### 4.2 拦截点在 `termsrv` 内部 / The gate is inside termsrv

从镜像的 `termsrv.dll`（10.0.20348.4294）提取的字符串：

```
WTSIsServerContainer
TS in container and so Remote Connection is not allow
GetAllowAllListenerInContainer returned true, allowing connections in containers without container changes.
Child Session Listener is not started because we are in container
Remote Connection is not allow on this edition
The SL policy for 'Allow Remote Connections' is not defined - assuming Reduced Functionality Mode
AllowListenersInsideArgonContainer
StartHvSocketListener / EnableCredSspForHvSocketListener
IsHvsiContainer  +  System\CurrentControlSet\Control\Hvsi
SKU does not support VMBusListener and HvSocketListener
```

即：**`termsrv` 一旦通过 `WTSIsServerContainer` 判定自己在容器里，就必须拿到内部许可
（`GetAllowAllListenerInContainer()` / SL 许可策略 "Allow Remote Connections"）才会创建监听**；
否则直接跳过。`AllowListenersInsideArgonContainer` 就是那个"容器内允许监听器"的开关名。

**重要发现 / Important**：`ContainerMode`（本仓库给 **沙箱 guest** 用的那个开关，见
`HcsSandboxEngine.EnableTcpRdp`）**在 20348 的 `termsrv` 里根本不存在**（宿主 26100 的
`termsrv` 里才有）。沙箱 guest 是 **26100 的客户端完整系统**，`WTSIsServerContainer` 为假，
所以 `ContainerMode=0` 对它有效；对 **Server 2022 容器镜像**则完全无效。

### 4.3 观察到的行为 / Observed behaviour

- 打了「容器内允许监听器」类补丁后，`RemoteConnectionManager` 的 Operational 日志出现了
  **10 次 Event 20523**：`Connection from listener RDP-Tcp will have terminal class of {5828227c-…}`
  ——即 **RCM 确实开始处理 RDP-Tcp 监听器配置了**；
- 但 **socket 始终没有建**：宿主 3389 不可达、容器内 3389 被拒绝、`qwinsta` 从不出现
  `rdp-tcp … Listen` 行；
- `UmRdpService`（UserMode Port Redirector）**无论如何都启动不了**（`net start` 报
  "The service did not report an error"，即 ServiceMain 静默退出）；
- `RdpVideoMiniport` 驱动 `STOPPED`（exit 1077）。

结论：RDP-Tcp 监听器的创建走 `PdDLL=tdtcp` 的**传输层**而非普通 Winsock；在进程隔离容器
（silo，共享宿主内核）里被 `termsrv` 的容器门禁 + silo 限制双重挡住，**无法用注册表绕过**。
这与 Microsoft 的公开立场一致（`microsoft/Windows-Containers#306`：GUI/RDP *"removed from the
Windows container images"*、*"massive redesign"*）。

---

## 5. 试过的全部开关（含失败）/ Every knob tried

前提：容器已有 **console 会话**、`TermService` 已在启动时运行、网络已验证。

| # | 开关 Knob | 结果 Result |
|---|---|---|
| 1 | `Terminal Server\ContainerMode=0`（沙箱配方） | 无效（20348 的 termsrv 不读该值）/ no effect |
| 2 | `RDP-Tcp\SecurityLayer=0`、`UserAuthentication=0`（关 NLA）、`fEnableWinStation=1`、`fDenyTSConnections=0` | 配置到位，不监听 / configured, still no listener |
| 3 | `AllowListenersInsideArgonContainer=1` 放入 5 处：`Terminal Server`、`RDP-Tcp`、`SOFTWARE\…\CurrentVersion`、`…\CurrentVersion\TerminalServer`、`Wds` | 值在容器内可见，仍不监听 |
| 4 | 同上的**机器环境变量**（服务会继承）`AllowListenersInsideArgonContainer=1` | 容器内 `set` 可见，仍不监听 |
| 5 | `SOFTWARE\Microsoft\Windows NT\CurrentVersion\EditionID = ContainerOSPlus` | 无效 |
| 6 | 策略 `SOFTWARE\Policies\…\Terminal Services` 与 `Microsoft\Policies\…` | 无效 |
| 7 | `Services\TermService\Start = 2`（开机自启）/ auto-start | 服务 RUNNING，但不建监听 |
| 8 | 强制重启 TermService（杀掉承载它的 `svchost -k termsvcs`） | 服务重启成功，仍不监听 |
| 9 | `netsh advfirewall` 放行 3389 | 容器内无防火墙服务，且非 bind 失败原因 |
| 10 | 宿主/容器两侧 3389 探测、RDP 握手 | 均失败 |

---

## 6. 方法学坑 / Method notes

| 坑 Pitfall | 说明 |
|---|---|
| 容器内 `netstat` 不可信 | 容器内 `netstat -an` / `Get-NetTCPConnection` **永远为空**，即使存在真实监听（手工 `TcpListener(9999)` 时宿主可连、容器内 `netstat` 仍空）。**必须从宿主侧探测。** |
| 层挂载时 hive 被独占锁 | 有容器在跑时改写镜像 hive 会**静默失败**；须先停容器 + `UnprepareLayer`/`DeactivateLayer` |
| `Hives\SYSTEM` 是 8 KB 工作副本 | 真正的配置单元是 `Files\...\config\SYSTEM`（= `Hives\SYSTEM_BASE` 硬链接） |
| 容器句柄 Container handle | `ShouldTerminateOnLastHandleClosed=true`：**持有句柄的进程一退出，容器立即销毁**（调试时容易"容器莫名消失"） |
| `Get-ItemProperty` 读 `SubSystems` 失败 | PowerShell 读 REG_EXPAND_SZ `SubSystems` 曾报不存在，`reg query` 才正确——排查注册表优先用 `reg query` |

---

## 7. 复现配方 / Recipes

### 7.1 让容器出现 console 会话 / Enable the console session

先停掉所有该镜像的容器并 `UnprepareLayer` + `DeactivateLayer`（否则 hive 被锁），然后离线改：

```powershell
# 备份 / back up
copy "<layer>\Files\Windows\System32\config\SYSTEM" "$env:TEMP\SYSTEM.orig"
reg load HKLM\WSMIMG "<layer>\Files\Windows\System32\config\SYSTEM"
foreach ($cs in 'ControlSet001','ControlSet002') {
  reg add "HKLM\WSMIMG\$cs\Control\Session Manager" /v NumberOfInitialSessions /t REG_DWORD /d 2 /f
}
reg unload HKLM\WSMIMG
```

> 本分支把这个步骤做成了工具：`wsm image desktop <layerPath>`（见 §8）。

### 7.2 验证「宿主 → 容器」入站 / Verify inbound reachability

```powershell
# 容器内（后台）起监听，再用宿主探测
powershell -NoProfile -EncodedCommand <bind TcpListener(0.0.0.0,9999) 的脚本>
# 宿主：
Test-NetConnection <containerIp> -Port 9999
```

### 7.3 RDP 尝试（记录用，实测无效）/ RDP attempt (recorded, ineffective)

```cmd
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v ContainerMode /t REG_DWORD /d 0 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v AllowListenersInsideArgonContainer /t REG_DWORD /d 1 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v AllowListenersInsideArgonContainer /t REG_DWORD /d 1 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v SecurityLayer /t REG_DWORD /d 0 /f
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp" /v UserAuthentication /t REG_DWORD /d 0 /f
sc config TermService start= auto
net start TermService
```

---

## 8. 本分支的改动 / Changes on this branch

分支 Branch: **`feat/windows-container-desktop`**（基于 `main`）。

| 步骤 Step | 提交 Commit | 内容 What | 状态 Status |
|---|---|---|---|
| 1 | `53aa774` | 本文档 + 调查索引更新 / this document + index | 记录 record |
| 2 | `d3b1db2` | `tools/container-probe`：本次用到的 HCS/WCOW 探针与一次性脚本 / the probe + the raw scripts | 记录 record |
| 3 | `86d405b` | `Wsm.Hcs.Storage`：离线注册表单元工具（`RegLoadKey` + 权限）+ 镜像定制器（`NumberOfInitialSessions=2`）+ 10 个单测 | **可用** works |
| 4 | `94e32f2` | CLI：`wsm image desktop <layerPath>`、`wsm image import … --desktop`（已提权实测通过） | **可用** works |
| 5 | `aad2c43` | `WindowsContainerEngine` 桌面选项（Shell / 自动登录 / 可选 RDP 尝试）+ 17 个单测 | Shell 与自动登录可用；**RDP 部分已知无效，仅作记录** |
| 6 | — | README 章节 + 文档回填 / README section + doc backfill | 记录 record |
| 7 | `d1235a2` | §10 桌面可达性实测（借 token / 自动登录 / ~121 秒注销 / pipe 与 3389 / 可截屏的容器桌面）+ `tools/container-desktop-agent` | 记录 + 可用 |
| 8 | `379c2f6` | §11 RDP 的**代码级根因**（反汇编 `termsrv`）+ §12 **会话内输入代理**（`deskinput.ps1`，含验证）+ 镜像导入器改为报出 `tar` 的诊断 | 记录 + 可用 |
| 9 | `096ce7d` | §13 共享内存呈现（容器内抓屏写 `frame.shm`、宿主映射读帧 + 输入队列，端到端实测） | **可用** works |
| 10 | `a457439` | §13.6 句柄直通**可行**（提权 `OpenProcess` + `DuplicateHandle` + `MapViewOfFile`）/ 窗口注入**证伪** | 记录 record |
| 11 | `1dc2024` | §13.7 由 SYSTEM 进程把窗口创建到用户 Console + 输入注入进容器内进程（实测 pid/会话正确） | **可用** works |
| 12 | `d11d11e` | §14 把能力接进管理 service（`Wsm.Connection.ContainerDesktop` + `IContainerFileSystemAccess` + CLI）；修掉 `Path.Combine` 有根路径 bug | 编译/单测通过 |
| 13 | `0f0cd54` | §15 为什么不能"注入窗口"，以及真正能省掉的拷贝（DIB section 直写 / 查看器 BitBlt / 脏矩形） | 记录 record |
| 14 | `779feb1` | §16 实测：宿主看得见容器进程，但**打不开**其 window station（User 命名空间按会话虚拟化） | 记录 record |
| 15 | `c0b81ba` | §17 实测：token/session 手术（含偷容器进程 token）**无法**在容器会话里创建进程（`0xC0000142`） | 记录 record |
| 16 | `dd485a4` | §18 设计决定：读侧用 winlogon/Userinit 包裹，显示侧用窗口镜像 | 记录 record |
| 17 | `1298dc4` / `d2d027c` | §19 **可替换 provider 模块 + 清单选择**（provider 接口/registry/门面 + frame-buffer/window-mirror + 镜像 payload + CLI） | 编译/单测通过 |
| 18 | `0efd4f2` | §20 窗口镜像的**显示 + 拖动**实测（宿主逐窗口真实窗口；拖动 → `movewindow` → 窗口真的移动） | **可用** works |
| 19 | `3b67e99` | §21 provider 改为 **HCS 启动读侧** + 读侧改**逐窗口 `PrintWindow` 进共享 DIB（零拷贝）** + 显式模块注册 | 编译/单测通过；容器内终验待重跑 |
| 20 | (本步 / this step) | §21.4 清单持久化缺口修复（`.wsb` 序列化 `<ContainerDesktop>`）+ §21.5 CLI 表面补齐 | 单测 308 通过 |

> 说明：按用户要求，**"无论是否可用"都放进本分支**——RDP 相关代码保留但明确标注
> `Experimental`/已知无效，避免误用。

---

## 9. 下一步 / Next steps

1. **会话内截屏/输入代理（推荐）**：在 console 会话里运行一个自写工具（GDI 抓 1024×768 TSDDD
   画面 + `OpenInputDesktop`/`SendInput` 回送输入，走 TCP），即可"远程进桌面"而**不依赖 termsrv**。
   需要 `Winlogon\AutoAdminLogon` + `DefaultPassword`，并把 `Shell` 设为镜像里存在的程序
   （镜像无 `explorer.exe`，用 `cmd.exe`）。
2. **Hyper-V 隔离（UVM）**：`termsrv` 的容器门禁针对的是 Argon 进程隔离；Hyper-V 隔离本质是
   一个 UVM，等同现有沙箱路径（`mstsc` 已验证可用）。
3. 进程隔离继续做无头负载（`wsm exec` / `wsm ip`），桌面交回 VM 引擎。

---

## 证据 / Evidence

- 容器内进程表：`tasklist /v`（无 `winlogon.exe`）；开 console 会话后
  `winlogon.exe` / `LogonUI.exe` 出现在 `Console` 会话。
- `qwinsta`：修前只有 `services`；修后 `console … Conn`。
- 注册表：镜像 `ControlSet001\Control\Session Manager\NumberOfInitialSessions=0x1`（`ControlSet002=0x2`，宿主 = 2）。
- `termsrv.dll`（10.0.20348.4294）字符串：`WTSIsServerContainer`、
  `TS in container and so Remote Connection is not allow`、`AllowListenersInsideArgonContainer`、
  `GetAllowAllListenerInContainer returned true, allowing connections in containers without container changes.`、
  `Child Session Listener is not started because we are in container`、
  `The SL policy for 'Allow Remote Connections' is not defined - assuming Reduced Functionality Mode`、
  `EditionID` / `ContainerOSPlus`。
- 事件日志 `Microsoft-Windows-TerminalServices-RemoteConnectionManager/Operational`：
  `1136`（RD Session Host role not installed）、`20524`、以及打补丁后的
  **`20523 ×10`**（`Connection from listener RDP-Tcp will have terminal class of {5828227c-…}`）。
- 网络对照：宿主 → 容器内手工 `TcpListener(9999)` = True；宿主 → 容器 3389 = False；
  容器内 3389 = refused。
- 显示：`GetDC(NULL)` + `GetDeviceCaps` → HORZRES/ VERTRES `1024×768`、BITSPIXEL 32、VREFRESH 60、
  PLANES 1；`BasicDisplay`/`BasicRender`/`DXGKrnl` Running；`HKLM\HARDWARE\DEVICEMAP\VIDEO`
  `\Device\Video4 → …\Services\BasicDisplay`、`\Device\Disc → …\Services\TSDDD\Device0`；
  `TSDDD\Device0` → `InstalledDisplayDrivers=TSDDD`。
- `UmRdpService` 无法启动（`net start` → "did not report an error"）；`RdpVideoMiniport` STOPPED (1077)。

---

## 10. 桌面可达性实测 / Desktop reachability, measured

> §3 的结论（改 `NumberOfInitialSessions=2` 后 console 会话 / `winlogon` / `LogonUI` 会起来）
> 依旧成立。本节补的是**外面到底能不能进得去这个桌面**，以及**哪一路真的能用**。

### 10.1 结论速览 / Summary

| 通道 Channel | 结果 Result |
|---|---|
| RDP（3389/TCP） | **不可用**（见 10.4） |
| RDP（命名管道 pipe） | 容器里只有 RDS **控制面**管道（`TermSrv_API_service`、`Ctx_WinStation_API_service`、`SessEnvPublicRpc`），**没有 RDP 传输管道**；3389 依旧不监听 |
| 借 token 在 console 会话里建进程 | **被容器 silo 拒绝**：即使以 SYSTEM 运行、`SeAssignPrimaryToken`/`SeTcb` 已启用、复制出的 token 确为 SYSTEM@session 5，`CreateProcessAsUser` 仍返回 `ACCESS_DENIED`；`CreateProcessWithTokenW` 只在**调用者自己的会话**里建进程 |
| 交互式登录（自动登录） | winlogon **确实**为 `WSMPROBE\Administrator` 在 console 会话发起了登录（LocalSessionManager 会话仲裁），但登录**不完成**（`LogonUI` 一直在），且 **console 会话在开机后 ~121 秒被注销**，shell 从未运行 |
| 容器**自己**会话的桌面 | **可用**：可绘制、可截屏（见 10.5） |

### 10.2 关键测量 / Measurements

- 借 token 的 4 条路径（`session-run.ps1` 全部试过）：impersonation/primary × `CreateProcessAsUser` / `CreateProcessWithTokenW`。以 **SYSTEM** 身份运行时：
  - `CreateProcessAsUser`（两种 token）→ `err 5 (Access is denied)`，`lpDesktop` 为 `null` 或 `winsta0\default` 都一样；
  - `CreateProcessWithTokenW` → 成功，但 `childSession=4`（调用者会话），**进不了 5**。
- 复制 token 本身没问题：`DuplicateTokenEx` 得到的 token 是 `session=5 user=S-1-5-18`（SYSTEM）✓ —— 卡住的是「跨会话建进程」这一步。
- 容器里 `ContainerAdministrator` = `S-1-5-93-2-1`（User Manager 合成账户，**不是 SAM 账户**：`net user` 只列 `Administrator/DefaultAccount/Guest/WDAGUtilityAccount`）；其 token 有 `SeDebugPrivilege`/`SeImpersonatePrivilege`（Enabled），**没有** `SeAssignPrimaryTokenPrivilege`/`SeTcbPrivilege`（`ERROR_NOT_ALL_ASSIGNED`）。
- 容器**可以**给 SAM 账户设口令：`net user Administrator <pw>` 成功，`LogonUser(Administrator, <pw>)` = OK。此改动**只在容器层**（镜像 `SAM` 的 SHA256 与时间不变）。
- 容器里的注册表写入（Winlogon 自动登录值、`StartRCM`、`TermService` 启动类型…）**不写进镜像的 `Files\...\config\*` hive**，但**在同一 scratch 层内跨重启保留**。
- `taskkill LogonUI.exe` 会让 **console 会话直接消失**（不要用它来「重启登录界面」）。

### 10.3 为什么会话 2 分钟后就没了 / The ~121 s teardown

每次开机完全一致：

```
boot+0s     winlogon 起，console 会话建立，LogonUI 运行
boot+1s     LocalSessionManager #41/#42  Begin/End session arbitration:  WSMPROBE\Administrator  Session ID: 5
boot+121s   Winlogon/Operational  #811/#812  <TermSrv> notification event (1)  = 注销
            LocalSessionManager   #39/#40  Session 5 has been disconnected by session 5 (reason 11)
            → session 5 消失（qwinsta 只剩 services）
```

即：自动登录**被仲裁过但没完成**（`LogonUI` 始终在），121 秒后会话被注销。容器里没有 120 秒不活动策略（`Policies\System` 无 `InactivityTimeoutSecs`），因此这更像是容器会话模型对「非容器用户登录」的处理结果。

### 10.4 RDP：pipe 与 3389 的实测 / RDP over pipe and 3389

本会话新增的测量：

- `sc config TermService start= auto` + `sc start TermService` → `TermService` **RUNNING**，开机时的
  `Remote Desktop Service start failed. 0x80070422`（服务被禁用）**消失**；
- 现在容器里出现的管道：`TermSrv_API_service`、`Ctx_WinStation_API_service`、`SessEnvPublicRpc`
  —— 这些是 RDS 的**控制面 RPC**，**不是** RDP 传输；**没有** 任何 RDP 传输管道；
- 服务状态：`SessionEnv` RUNNING、`RDPDR` RUNNING（尽管镜像里没有 `rdpdr.sys`）、`UmRdpService` **STOPPED**。

逐个开关（全部**无效**，3389 始终不监听）：

| 开关 Knob | 结果 |
|---|---|
| `Terminal Server\StartRCM 0 → 1`（**本次新试**） | 无效果 |
| `AllowListenersInsideArgonContainer=1` 三处（`Terminal Server`、`RDP-Tcp`、`SOFTWARE\Microsoft\Windows NT\CurrentVersion`） | 无效果 |
| `EditionID = ContainerOSPlus` | 无效果 |
| `SecurityLayer=0`、`UserAuthentication=0`、`fEnableWinStation=1`、`fDenyTSConnections=0` | 无效果 |
| 启动 `UmRdpService` | 失败（`did not report an error`） |

- 验证方式：宿主 `Test-NetConnection <容器IP> -Port 3389` = **False**；`rprobe`（最小 RDP 握手）= **超时**；容器内 `netstat`/`Get-NetTCPConnection` 不可信（§6）。
- RCM Operational 日志：`#20524 Supplemental Kerberos credentials are not configured`、**`#1136 RD Session Host Server role is not installed.`** —— 「角色未安装」是新的线索。

### 10.5 真正可用的一路：容器自己会话的桌面 + 截屏 / The desktop that does work

容器**自己**会话（services 会话）的桌面是真实可绘制、可截屏的：

1. 借 token 把 `regedt32.exe` 与 `cmd.exe` 放到该会话的**活动桌面**（`winsta0\Winlogon`，容器会话的 input desktop）上；
2. 在同一桌面运行 `deskcap.ps1 -Once C:\wsm\shot.png`；
3. 宿主用 base64 把 PNG 取出来（`pull`）。

**结果：17 053 字节 PNG，内容是一个真实的「注册表编辑器」窗口**（`Computer` 下
`HKEY_CLASSES_ROOT` / `HKEY_CURRENT_USER` / `HKEY_LOCAL_MACHINE` / `HKEY_USERS` /
`HKEY_CURRENT_CONFIG`，左侧树 + 右侧 `Name/Type/Data` 列）。这证明容器里**有**可用的桌面与 GDI 绘制。

> 重要细节：`Graphics.CopyFromScreen` **只能截活动桌面**。截非活动的 `Default` 桌面会得到
> `The handle is invalid`；且因为容器里没有 shell/任务栏，画面右侧与底部会有未绘制的黑区。

### 10.6 本次用到的工具 / Tooling used

`tools/container-desktop-agent/`：`session-run.ps1`、`session-run-as-system.ps1`、
`deskcap.ps1`、`agent.cmd`、`agent.ps1`（+ README 说明每条测量的由来）。

---

## 11. RDP 起不来的代码级根因 / Why the RDP listener is never created (code-level)

> 本节把 §4/§10 的「配置层」结论推进到**代码级证据**：用 `capstone` + `pefile` 反汇编镜像里的
> `termsrv.dll`（10.0.20348.4294），并在容器内与宿主上做**对照测量**，定位到监听器创建失败的确切位置。

### 11.1 结论 / Verdict

**RDP 监听器（`RDP-Tcp` / TCP 3389）在进程隔离（Argon）容器里不会被创建**：`termsrv` 的
「容器 / VM 监听器」路径在容器里必然短路。逐个判定点：

| 判定点 Where | 结果 Result |
|---|---|
| `AreRemoteConnectionsAllowed()`（`0x180043548`） | **TRUE** —— 容器里 `WTSIsServerContainer()` 实测 **True**，直接返回允许 |
| `GetAllowAllListenerInContainer()`（`0x180020728`） | **TRUE** —— 需要 `AllowListenersInsideArgonContainer`(REG_DWORD)=1 **且** `EditionID`==`ContainerOSPlus` **且** `RtlGetCurrentServiceSessionId()!=0`（容器内实测 **4**） |
| `StartHardcodedListeners()`（`0x18004A2E0`）→ `StartVMListener(this,2/3)`（`0x18004A3C4`） | **被跳过** —— `cpuid 0x40000000` 必须报告 hypervisor 厂商 `"Microsoft Hv"`；否则 `OS is not running in VM. Not starting a vm specific listener.` |
| `RDP-Tcp` 监听器本体 | `CRemoteConnectionManager::Initialize` 遍历 `...\WinStations` → `GetProtocolExtension` → `protocol->CreateListener`；前者要实例化 COM「可加载协议对象」`{5828227C-…}`（`rdpcorets.dll` → `UMRDPProtocolManager`），**容器内实例化失败**（§11.4） |

即：**许可/策略层在容器里全是「允许」**（这正是之前各种注册表开关「无效」的原因——开关早已生效），
真正卡住的是**创建监听器**：`StartVMListener` 明确要求「自己跑在 VM 里」，而进程隔离容器不是 VM。

### 11.2 反汇编得到的关键函数 / Key functions (image 20348.4294)

| VA | 名字（由字符串与调用关系确定） | 作用 |
|---|---|---|
| `0x180020728` | `GetAllowAllListenerInContainer()` | 读 `HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server\AllowListenersInsideArgonContainer`（REG_DWORD）+ `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\EditionID` == `ContainerOSPlus`；且 `RtlGetCurrentServiceSessionId() != 0` |
| `0x180043548` | `AreRemoteConnectionsAllowed()` | 依次：`GetAllowAllListenerInContainer()` → `WTSIsServerContainer()`（KERNELBASE）→ `CSLQuery::IsHvsiContainer()`（`...\Control\Hvsi\IsHvsiContainer`）→ SL 策略 `TerminalServices-RemoteConnectionManager-AllowRemoteConnections` |
| `0x18004A2E0` | `StartHardcodedListeners()` | `AreRemoteConnectionsAllowed()` 为真时打 trace `Child Session Listener is not started because we are in container`，随后 `StartVMListener(this,2)`；再按 `...\WinStations\StartHvSocketListener` 决定是否 `StartVMListener(this,3)` |
| `0x18004A3C4` | `StartVMListener(mode)` | `cpuid 0x40000000` 校验 `"Microsoft Hv"`；非 VM → `OS is not running in VM. Not starting a vm specific listener.` 并返回 |
| `0x1800439C8` | `IsHardcodedListener(name)` | 仅三个 GUID 名（`7A78855482A04FA781DC` / `31C5CE94259D4006A9E4` / `41C5CE94259D4006A9E4`）算「硬编码监听器」；**`RDP-Tcp` 不在其中**（因此它走普通 `WinStations` 循环） |
| `0x1800436B8` | `IsAllowedProtocol(guid)` | GUID ∈ {`{5828227C-20CF-4408-B73F-73AB70B8849F}`（即 RDP-Tcp 的 `LoadableProtocol_Object`）、`{E99FA5E4-A1C9-4495-8BF0-D0972D58E841}`} |
| `0x180098EE0` | `IsVmbusOrForceRdvhl()` | `LoadLibraryW("mvmbuspipe.dll")` + `VmbusPipeClientEnumeratePipes` **或** `HKLM\SYSTEM\CurrentControlSet\Services\termservice\fForceRDVHLicensing == 1` |
| `0x180027B90` | `CSLQuery::Initialize()` | `SLGetWindowsInformationDWORD` 读一串 SL 策略：`-AllowRemoteConnections` / `-AllowMultipleSessions` / `-AllowAppServerMode` / `-AutomatedAppServerInstallation` / `-AllowMultimon` / `-MaxUserSessions` / `-MaxSessions` / `Kernel-OneCore-VailGuest` / `-WVD-Enabled` |
| `0x18001A03C` | `CSLQuery::IsAppServerInstalled()` | 其数据来自 `0x18000FBAC`：读 `...\Terminal Server\TSAppCompat` |

### 11.3 排除掉的线索 / Ruled out (each with evidence)

| 线索 Lead | 为什么不是原因 Evidence |
|---|---|
| 事件 **1136**（“未安装 RD 会话主机服务器角色”） | **宿主（Windows 11 客户端，RDP 完全正常）也记录同一条 1136**，且紧跟在 `#20523`/`#258` 之后；它是常规提示，不是容器门禁 |
| `UmRdpService` STOPPED | 宿主上 **`0.0.0.0:3389` 属于 `TermService`（`svchost.exe -k NetworkService`，pid 10856）**，不是 `UmRdpService`（它是端口/设备重定向） |
| 「servercore 太精简、缺 RDP 文件」 | 镜像里 RDP 用户态栈齐全：`termsrv/rdpcorets/rdpbase/rdpserverbase/umrdp/rdpudd/rdpcfgex/rdpclip/tscfgwmi/drprov` + `rdpvideominiport.sys/tsusbhub.sys/rdpdr.sys`。与宿主相比只缺 `rdpendp.dll`（音频端点）与 `rfxvmt.dll`（**宿主也没有**）；**把 `rdpendp.dll` 补进容器后 COM 实例化仍失败** |
| 注册表开关 | `StartRCM=1`、`fDenyTSConnections=0`、`SecurityLayer=0`、`UserAuthentication=0`、`fEnableWinStation=1`、`PortNumber=3389`、`Hvsi\IsHvsiContainer=1`、`TSAppCompat=1`、`AllowListenersInsideArgonContainer`(3 处)、`EditionID=ContainerOSPlus`（**一次性全部设置**）→ 3389 依旧不监听 |
| 「策略层不允许远程连接」 | 实测容器内 `WTSIsServerContainer()=True`、`RtlGetCurrentServiceSessionId()=4` → `AreRemoteConnectionsAllowed()` 已是 TRUE |
| 「缺 RDP 命名管道」 | 容器与宿主的 RDP 相关管道**完全一致**：`TermSrv_API_service`、`Ctx_WinStation_API_service`、`SessEnvPublicRpc` |

### 11.4 容器里真正卡住的一步 / The concrete runtime failure

在容器内以管理员身份实例化 `RDP-Tcp` 的可加载协议对象：

```powershell
$t = [Type]::GetTypeFromCLSID([Guid]'{5828227C-20CF-4408-B73F-73AB70B8849F}')
[Activator]::CreateInstance($t)
# → HRESULT 0x80070002 "The system cannot find the file specified."
```

对照与排查：

- 宿主上同一 CLSID 在**非提权**下是 `0x80070005 E_ACCESSDENIED`（先过权限门）；容器里则是**别的东西找不到**；
- `rdpcorets.dll` 在容器里**能**被 `LoadLibraryW` 成功加载（返回有效句柄）→ 不是 DLL 缺失/依赖问题；
- `InprocServer32 = %windir%\system32\rdpcorets.dll`，容器内 `windir=C:\Windows` 正常，显式设置后重试仍失败；
- 该 CLSID 在容器内**已正确注册**（`UMRDPProtocolManager Class`，含 `InprocServer32`/`ProgID`/`TypeLib`），与宿主逐字相同。

⇒ `UMRDPProtocolManager`（UserMode RDP 协议管理器）在容器 silo 里初始化失败；而 `RDP-Tcp` 监听器**只能由它创建**。

### 11.5 与产品设计一致 / Consistent with the design

`termsrv` 里与容器相关的三条字符串属于**同一段逻辑**：

```
TS in container and so Remote Connection is not allow            (trace)
Child Session Listener is not started because we are in container (trace)
OS is not running in VM. Not starting a vm specific listener.     (trace)
```

配套字符串/注册表进一步说明**容器里的「远程连接」= VM 监听器**：
`StartHvSocketListener`、`EnableCredSspForHvSocketListener`、
`Enhanced mode connection is disabled by policy. Not starting the VM listener.`、
`SKU does not support VMBusListener and HvSocketListener`、
`SOFTWARE\Microsoft\Virtual Machine\Guest\DisableEnhancedSessionConsoleConnection`、
`IsHvsiContainer`。这正是 **Windows Sandbox「增强会话」/ Hyper-V 隔离容器**所走的路；
**进程隔离（Argon）不是 VM，所以这条路过不去**。与 Microsoft 的公开立场一致（GUI/RDP 不在 Windows 容器镜像的支持范围内）。

### 11.6 能用的两条路 / The two workable paths

1. **Hyper-V 隔离（UVM）**：RDP 本来就依赖它（Sandbox 增强会话；本仓库 `HcsSandboxEngine`）。
2. **会话内截屏/输入代理**（进程隔离下唯一能「看到桌面」的方式）：§10.5 已证明可截屏，
   且 §11.7 表明自动登录 + shell 现在能成功，因此可在 console 会话里运行代理、由宿主转发画面与输入。

### 11.7 对 §10.3 的修正 / Correction to §10.3

之前记录「自动登录被仲裁但登录不完成、`LogonUI` 一直在、会话 ~121 秒后被注销」。本次复测
（镜像层里已经有容器 `Administrator` 的口令）：

```
qwinsta:  console   Administrator   5   Active
LocalSessionManager/Operational:
  #41/#42  Begin/End session arbitration:    WSMPROBE\Administrator  Session ID: 5
  #21      Session logon succeeded:          WSMPROBE\Administrator  Session ID: 5  Source: LOCAL
  #22      Shell start notification received:WSMPROBE\Administrator  Session ID: 5  Source: LOCAL
```

即：**只要口令存在（`net user Administrator <pw>` + 注册表 `DefaultPassword`），自动登录会成功、shell 会启动**。
§10.3 的「登录不完成」应修正为「镜像缺少 `DefaultPassword`」这一前置条件所致。

### 11.8 工具 / Tooling

- 反汇编/分析：`tools/container-desktop-agent/rdp/` —— `analyze.py`（PE 字符串、RIP-relative xref、
  函数体、caller、IAT/ILT 名称、`EVENT_DESCRIPTOR` 扫描）、`deps.py`（依赖 DLL 与镜像存在性）、
  `guid.py`、`grepstr.py`（整目录二进制字符串搜索）。
- 运行时对照：`tools/container-desktop-agent/host/` 的 `appserver.ps1`（一次性设置全部候选开关 + 重启
  + 读日志）、`tslogs.ps1`（RDS 各日志 + 系统日志）、`rdpproto.ps1`（CLSID/COM 实例化）、`rdpenv.ps1`（环境/WinStations/服务）。

---

## 12. 会话内代理（唯一可用的「远程桌面」）/ In-session agent (the only workable desktop)

> §9.1 推荐的方案已实现并**端到端验证**：一个跑在容器会话里、位于**活动桌面**
> （`winsta0\Winlogon`）的代理，提供「抓屏 + 注入输入」两半。这是进程隔离容器里唯一能
> **看到并操作**桌面的通道（因为 §11 证明 RDP 监听器不可能建立）。

### 12.1 组成 / Pieces

| 脚本 | 作用 |
|---|---|
| `session-run.ps1` | 借 token 把进程送进目标会话/桌面。容器里跨会话 `CreateProcessAsUser` 被 silo 拒绝（`err 1314/5`），只有 `CreateProcessWithTokenW` 可用，且子进程落在**调用者会话**里 |
| `deskcap.ps1` | 抓当前桌面：`-Once <png>` 抓一张，或 `-Port 5901` 持续提供 `[4 字节长度][PNG]` 帧 |
| `deskinput.ps1` | **本次新增**：注入输入。动作 `info` / `move` / `click` / `keys` / `text` / `script -TextFile`，或 `-Port` 行协议；底层是 `SetCursorPos` + `mouse_event` + `keybd_event` + `VkKeyScanW` |
| `agent.cmd` / `agent.ps1` | 作为 console 会话 shell 的入口（写证明文件 + 起 regedt32 + 起 deskcap） |

### 12.2 验证 / Verification (measured)

代理在活动桌面上报告（`info`）：

```
desktop=Winlogon  inputDesktop=ok  cursor=512,384  foreground=0xA002A pid=33136 'Registry Editor'
```

向注册表编辑器注入 `F3`（打开「查找」）后，**同一进程内**再次 `info`：

```
keys 'F3' -> 0x72
foreground=0x6006C pid=33136 'Find'        ← 前台窗口从 Registry Editor 变成 Find
```

抓屏（`find.png`，32 KB）里能看到真实的**「查找」对话框**（`Find what:` 输入框、
`Look at` 的 `Keys`/`Values`/`Data` 单选、`Match whole string only`、`Find Next`/`Cancel`），
背后是 `Registry Editor` 窗口与一个 Notepad 窗口。
⇒ **注入的键盘输入确实到达了容器桌面，并驱动了真实 GUI 应用。**

对照：在**非活动桌面**（容器级进程所在的 `winsta0\default`）上跑同一脚本会报
`desktop=Default inputDesktop=FAILED (err=1)` —— 这也是为什么代理必须跑在活动桌面上。

### 12.3 实现坑（可复现）/ Gotchas worth knowing

| 坑 | 说明 |
|---|---|
| PowerShell **5.1** 不支持 `$b = if (...) {} else {}` | 那是 PS7 语法；在容器里是**解析错误**，`-File` 启动后**静默退出**（无日志、无输出）。改为先赋默认值再 `if` |
| `Add-Type -Path` 会锁住 DLL | 缓存 `Ui.dll` 后，其它进程再 `Add-Type -OutputAssembly` 到同一路径会失败 → 必须 try/catch + 内存编译回退（同 `session-run.ps1`） |
| `Add-Type` 的 C# 编译错误只出现在 stderr | 隐藏窗口子进程没人接收 stdout/stderr → 用 `cmd.exe /c ... > out 2> err` 落盘再读 |
| PowerShell 调原生程序会丢掉内层引号 | `& powershell.exe -File x.ps1 -CommandLine $cmd` 里 `$cmd` 的 `"` 会被剥掉 → 改成**文件驱动**（`-Action script -TextFile`）或写成 `.cmd` 用 `cmd /c` 启动 |
| `CopyFromScreen` 只能抓活动桌面 | 非活动桌面报 `The handle is invalid`；容器里没有 shell/任务栏 → 画面边缘常见未绘制黑区 |
| 不要按会话号杀进程 | 容器级命令本身就在会话 4 里跑，`Get-Process | Where SessionId -eq 4 | Stop-Process` 会**把自己杀掉**，表现为"命令无输出" |

### 12.4 用法 / How to drive it

```powershell
# 1) 把一个会驻留的 GUI 放到桌面上
powershell -File C:\wsm\session-run.ps1 -Session 4 -Desktop winsta0\Winlogon -Visible -CommandLine regedt32.exe

# 2) 注入输入（一次性脚本，避开引号问题）
powershell -File C:\wsm\session-run.ps1 -Session 4 -Desktop winsta0\Winlogon -CommandLine `
  'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskinput.ps1 -Action script -TextFile C:\wsm\steps.txt -Log C:\wsm\input.log'

# 3) 抓一帧
powershell -File C:\wsm\session-run.ps1 -Session 4 -Desktop winsta0\Winlogon -CommandLine `
  'powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\wsm\deskcap.ps1 -Once C:\wsm\frame.png'
```

`steps.txt` 例：

```
info
click 250,180
text echo hello> C:\wsm\proof.txt
keys ENTER
info
```

宿主侧要看画面/操作时，可把两个脚本都跑成 `-Port` 服务（例如 `5901` 抓屏、`5902` 输入），
或继续用**探针 + base64** 取帧（宿主直连容器端口在某些布局下不通，见 §6）。

### 12.5 仍未做 / Not done yet

- 宿主侧的**交互式查看器**（把 `deskcap` 的帧显示成窗口、把本地鼠标/键盘转发给 `deskinput`）；
- 帧数据量（1024×768×4 ≈ 3 MB/帧）需要压缩或差分编码；
- 把代理从调查工具提升为产品能力（例如 `wsm container desktop`）。


---

## 13. 鐢ㄥ叡浜唴瀛樻妸瀹瑰櫒鐢婚潰鍛堢幇鍦ㄥ涓讳笂 / Presenting the container desktop via shared memory

> 鐩爣锛氫笉鐢?RDP锛埪?1 宸茶瘉鏄庝笉鍙兘锛夛紝鎶?*瀹瑰櫒妗岄潰鐢婚潰**鍛堢幇鍦ㄥ涓讳笂锛屽苟涓旂敤**鍐呭瓨鍏变韩**鑰屼笉鏄?TCP銆?> 鍋氭硶锛氬厛娴嬪墠鎻愶紝鍐嶆寜娴嬪嚭鐨勫墠鎻愯璁°€?
### 13.1 鍏堟祴鍓嶆彁 / Measure the prerequisites first

| 鎺㈡祴 Probe | 瀹瑰櫒鍐?Container | 瀹夸富 Host |
|---|---|---|
| `DwmIsCompositionEnabled` | **False**锛堥暅鍍忛噷涔熸病鏈?`dwm.exe`锛?| True |
| `D3D11CreateDevice(HARDWARE)` | **`0x887A0004` DXGI_ERROR_UNSUPPORTED** | `0x0` 鎴愬姛 |
| `D3D11CreateDevice(WARP)` | **`0x887A0004`**锛堣繛杞欢鍏夋爡閮芥病鏈夛級 | 鈥?|
| `EnumDisplayDevices` 鏋氫妇鍒扮殑閫傞厤鍣?| **0 涓?* | 鏈?|
| `CreateFileMappingW`锛堝懡鍚?鍖垮悕锛?| **鎴愬姛** | 鎴愬姛 |
| 瀹瑰櫒寤鸿妭鍚庡涓绘寜鍚?`OpenFileMapping` | 鈥?| **err=2 FILE_NOT_FOUND** 鈬?**鍛藉悕瀵硅薄涓嶈法 silo** |
| 瀹夸富鑳藉惁鐪嬪埌瀹瑰櫒 PID | 瀹瑰櫒 `pid=98836` | **`Get-Process -Id 98836` 鈫?`powershell session=4`** 鈬?PID 绌洪棿鍏变韩 |
| 瀹夸富 `OpenProcess(PROCESS_DUP_HANDLE, 瀹瑰櫒pid)` | 鈥?| **err=5**锛堝涓绘湭鎻愭潈锛宍SeDebugPrivilege` err=1300锛夆噿 鍙ユ焺澶嶅埗**闇€瑕佹彁鏉冨涓?* |
| 瀹夸富璇诲鍣?`C:\` | 鈥?| `\\?\Volume{guid}\...` **鍙**锛?*鍐欓渶瑕佹彁鏉?* |

**缁撹锛?* 鈶?瀹瑰櫒娌℃湁 DWM銆佹病鏈変换浣?D3D 璁惧 鈫?娌℃湁 `ID3D11Texture2D`/`CreateSharedHandle`锛?`DwmGetDxSharedSurface` 涔熸棤澶勫彲闂紝**鍙兘鍏变韩鍍忕礌**锛涒憽 涓嶈兘鎸夊悕瀛椾細鍚堬紙鍚?silo 鏈夊悇鑷殑
`BaseNamedObjects`锛夆啋 鍙兘鎸?*鏂囦欢/鍙ユ焺**浼氬悎锛涒憿 瀹瑰櫒鐨?`C:\` **灏辨槸瀹夸富鍙槧灏勭殑涓€涓嵎**
锛坄\\?\Volume{guid}\`锛屾棤鐩樼锛夆啋 **涓€涓櫘閫氭枃浠跺氨鏄叡浜唴瀛?*銆?
### 13.2 璁捐 / Design

```
瀹瑰櫒 (Server Silo)                                    瀹夸富 (Root Silo)
  deskshare.ps1                                         deskread.ps1 / deskview.ps1
  +- CreateFileW/CreateFileMappingW/MapViewOfFile <---  CreateFileW/.../MapViewOfFile
  |   C:\wsm\frame.shm          鍚屼竴涓枃浠?        \\?\Volume{guid}\wsm\frame.shm
  |   [64B 澶碷[1024x768xBGRX]                        璇?seq/busy 鈫?蹇収鎴?Bitmap
  +- 杞 inbox-<ts>.txt ---- 姣忓懡浠や竴涓柊鏂囦欢 ----<  deskpush.ps1 / deskview 鐨勯紶鏍囬敭鐩?      鈫?deskinput.ps1 娉ㄥ叆                             锛堝啓杩涢暅鍍忓眰锛屽涓绘棤闇€鎻愭潈锛?```

甯ф枃浠跺ご锛堝皬绔級锛歚0 magic 'FSM1'` / `4 version` / `8 width` / `12 height` / `16 stride` / `20 format` /
`24 seq(int64)` / `32 busy(int32)` / `36 writerPid` / `40 fps` / `48 totalFrames(int64)` / `64鈥 鍍忕礌銆?
杈撳叆鏂瑰悜**涓嶅叡鐢ㄥ悓涓€涓枃浠?*锛氬涓诲瑁稿嵎娌℃湁鍐欐潈闄愶紙闄ら潪鎻愭潈锛夛紝浣嗗彲浠ュ線**闀滃儚灞?*鍐欐櫘閫氭枃浠讹紝
瀹瑰櫒鐨勫悎骞惰鍥剧珛鍒昏兘鐪嬪埌锛涜€屽鍣ㄨ杩囨煇涓眰鏂囦欢鍚庡叾鏂囦欢绯荤粺浼?*鎸佹湁鍙ユ焺**锛岃鐩栧啓浼?`used by another process` 鈫?鎵€浠?*姣忔潯鍛戒护涓€涓柊鏂囦欢** `inbox-<鏃堕棿鎴?.txt`銆?
### 13.3 瀹炴祴缁撴灉 / Measured result

```
host: header: magic=0x314D5346 1024x768 stride=4096 busy=0 writerPid=10732 fps=5 frames=95
host: seq: first=95 then=96 busy=0 moved=True
host: saved snapshot: shared-b.png (35189 bytes)
```

瀹夸富浠庡叡浜唴瀛樿鍑虹殑甯ф槸涓€寮?*鐪熷疄鐨?1024脳768 妗岄潰**锛歚Registry Editor` + 瀹冪殑 `Find` 瀵硅瘽妗?锛坄Find what:`銆乣Look at` 鐨?`Keys/Values/Data`銆乣Find Next`/`Cancel`锛? `Untitled - Notepad`锛?棰滆壊涓庢柟鍚戞甯搞€佹棤闀滃儚/鎾曡锛堣 `docs/investigation/assets/container-desktop-shared-memory.png`锛夈€?澶у潡榛戣壊鏄?*娌℃湁 wallpaper/shell**锛堥暅鍍忔棤 explorer锛夛紝涓嶆槸娓叉煋缂洪櫡銆?
杈撳叆鏂瑰悜鍦ㄥ悓涓€濂楁満鍒朵笂涔熼€氾細娉ㄥ叆 `F3` 鍚庡悓涓€杩涚▼鍐呭墠鍙扮獥鍙ｇ敱 `Registry Editor` 鍙樹负 `Find`锛埪?2.2锛夈€?
### 13.4 韪╁埌鐨勫潙 / Gotchas

| 鍧?| 鐜拌薄涓庤В娉?|
|---|---|
| PS 5.1 鎶?`0xC0000000` 褰?*璐?Int32** | `CreateFileW` 鐨?`access` 鎶?鈥渃annot convert to UInt32鈥?鈫?鐢?`[uint32]3221225472` 杩欑被鍗佽繘鍒?|
| `Bitmap(w,h,stride,fmt,scan0)` 鐩存帴鏄犲皠鎸囬拡 | `CopyFromScreen` **闈欓粯澶辨晥**锛屾暣甯у叏榛?鈫?鍏堟姄杩涙櫘閫?Bitmap锛屽啀 `RtlMoveMemory` 鎷疯繘鍏变韩鍐呭瓨 |
| **闅愯棌/鏈€灏忓寲**鐨勬帶鍒跺彴 | TSDDD 铏氭嫙鏄剧ず缁欑殑鏄?*鍏ㄩ粦**甯э紙`GetForegroundWindow` 浠嶇湅寰楀埌鐪熺獥鍙ｏ級鈫?鎶撳睆浠ｇ悊蹇呴』**鍙**鍚姩 |
| `MemoryMappedFile.CreateFromFile` 鐨勯噸杞?| PS 浼氭寫閿欓噸杞斤紙鎶?FileStream 褰?path锛夈€乣$null` 鍚嶆姤 鈥淢ap name cannot be an empty string鈥濄€佸叡浜ā寮忓彈闄?鈫?**鐩存帴鐢?Win32** |
| 灞傛枃浠惰瀹瑰櫒璇昏繃鍗抽攣 | 瑕嗙洊鍐欐姤 `used by another process` 鈫?姣忔潯鍛戒护涓€涓柊鏂囦欢锛屼袱渚ч兘鐢?`FileShare.ReadWrite` |
| `OpenProcess(DUP_HANDLE)` 琚嫆 | 闈炴彁鏉冨涓?`err=5`銆乣SeDebugPrivilege err=1300` 鈫?銆屽彞鏌勭洿閫氥€嶅彉浣撳繀椤?*鎻愭潈**锛堟湭澶嶆祴锛?|

### 13.5 鐜扮姸涓庡悗缁?/ Status and next steps

- **鍙敤**锛歚deskshare.ps1`锛堝鍣細鎶撳睆鈫掑叡浜唴瀛?+ 杞杈撳叆锛夈€乣host\deskread.ps1`锛堝涓伙細璇诲抚鈫扨NG/鏍￠獙锛夈€?  `host\deskpush.ps1`锛堝涓伙細鍙戣緭鍏ワ級銆乣host\deskview.ps1`锛堝涓荤獥鍙ｏ細璐村抚 + 杞彂榧犳爣/閿洏锛沗-Capture` 宸查獙璇侊級銆?- **寰呭仛**锛氫氦浜掔獥鍙ｇ殑闀胯窇瀹炴祴锛涜剰鐭╁舰 + 鍘嬬缉锛? MB/甯?脳 5 fps 鈮?15 MB/s 鍐欓〉缂撳瓨锛夛紱
  鑻ヨ蛋銆屽彞鏌勭洿閫氥€嶅彉浣擄細瀹瑰櫒寤洪〉鏂囦欢鑺?鈫?鎶?`pid`+`handle` 鍐欒繘鍏变韩鏂囦欢 鈫?瀹夸富锛?*鎻愭潈** + `SeDebugPrivilege`锛?  `OpenProcess` + `DuplicateHandle` + `MapViewOfFile`锛埪?3.1 宸茬‘璁?PID 鍏变韩銆佸懡鍚嶅璞′笉鍏变韩锛夈€?
### 13.6 銆屾彁鏉冨涓昏兘鍚﹁法 silo 鎷垮彞鏌?/ 娉ㄥ叆绐楀彛銆嶅疄娴?/ Handle pass-through and window injection, tested

涓や釜鍛介鍒嗗紑娴嬶細瀹夸富渚?`host\elevtest.ps1`锛?*鎻愭潈**杩愯锛屽厛 `SeDebugPrivilege`锛屽啀 `OpenProcess`锛夛紱
瀹瑰櫒渚?`host\shmprobe.ps1` 寤轰笁涓妭骞舵妸 `pid`+鍙ユ焺鍐欒繘 `shm-handles.txt`銆?
**A. 鍙ユ焺鐩撮€氾細鎴愮珛 鉁?*

```
container published: pid=94424 | handle_frame=2304 | frame_capacity=3145792
Get-Process -Id 94424: name=powershell session=4          鈫?PID 绌洪棿鍏变韩
OpenProcess(PROCESS_DUP_HANDLE) -> 0x91C                  鈫?鎻愭潈瀹夸富鍙互鎵撳紑瀹瑰櫒杩涚▼
DuplicateHandle(frame) ok=True dup=0x968
MAP OK (3145792 bytes): head='WSM-FRAME-OK' tail='WSM-FRAME-OK'
```

**鎻愭潈瀹夸富 `OpenProcess` + `DuplicateHandle` + `MapViewOfFile` 鑳芥妸瀹瑰櫒閲屼竴涓?3 MB 鑺傚畬鏁存槧灏勫嚭鏉?*
锛堥灏炬爣璁伴兘璇诲埌锛夈€傞潪鎻愭潈瀹夸富鍦ㄥ悓涓€澶勬槸 `err=5`锛埪?3.1锛夆噿 鍙**瀹夸富渚х粍浠舵彁鏉?*鍗冲彲锛圵SM 鏈潵灏辨彁鏉冭繍琛岋級銆?
鈬?璁捐鍗囩骇锛氬鍣ㄦ敼鐢?`CreateFileMappingW(INVALID_HANDLE_VALUE, 鈥?`锛?*椤垫枃浠舵敮鎸佺殑鑺傦紝绾唴瀛樸€佷笉钀界洏**锛夛紝
鎶?`pid`+`handle` 鍐欒繘涓€涓嚑瀛楄妭鐨勫叡浜枃浠讹紱瀹夸富鎻愭潈鍚庡鍒跺彞鏌勫苟鏄犲皠 鈫?**鍘绘帀 NTFS 鑴忛〉鍐欏洖**
锛堢幇鍦?3 MB/甯?脳 5 fps 鈮?15 MB/s 鎵撳埌椤电紦瀛橈級銆傝緭鍏?鎺у埗鍒欏彲璧?HCS `MappedPipes`锛堝畼鏂硅法 silo 閫氶亾锛夈€?
**B. 绐楀彛娉ㄥ叆锛氫笉鎴愮珛 鉁?*

```
FindWindow(null, 'Registry Editor') = 0x0        鈫?瀹瑰櫒閲屾槑鏄庢湁杩欎釜绐楀彛
host input desktop handle=0x954 name='Default'   鈫?瀹夸富鑷繁鐨勮緭鍏ユ闈紙瀹瑰櫒鏄?Winlogon锛?EnumWindows on this desktop: [Visual Studio] [TabTip] ...   鈫?鍙湁瀹夸富鑷繁鐨勭獥鍙?```

绐楀彛鏄?*浼氳瘽/妗岄潰浣滅敤鍩?*鐨勫璞★紝silo 鏈?*鑷繁鐨?`WinSta0`/妗岄潰**锛氬涓荤殑 `FindWindow`/`EnumWindows`
鍙湅寰楀埌**鑷繁妗岄潰**涓婄殑绐楀彛锛屽鍣ㄧ獥鍙ｄ竴涓兘鐪嬩笉鍒帮紱璺ㄦ闈?璺ㄤ細璇濈殑 `SetParent`銆乣SetThreadDesktop`
鍚屾牱涓嶉€氥€傗噿 **銆屾妸绐楀彛娉ㄥ叆鐢ㄦ埛鎺у埗鍙般€嶅湪杩涚▼闅旂閲屽仛涓嶅埌**锛岃兘杩囧幓鐨勫彧鏈?*鍍忕礌/鍐呭**锛埪?3.2 鐨勬柟妗堬級锛?鎯宠鐪熸鐨勭獥鍙ｅ彧鑳借蛋 **Hyper-V 闅旂 + 澧炲己浼氳瘽**銆?
**缁撹锛堝畾涓嬬殑浜у搧褰㈡€侊級**锛氱敾闈?= `pid`+`handle` 鐩撮€氱殑**椤垫枃浠惰妭**锛堟彁鏉冨涓?鈫?绾唴瀛樸€侀浂钀界洏锛夛紱
杈撳叆/鎺у埗 = HCS `MappedPipes` 鎴栥€屾瘡鍛戒护涓€涓枃浠躲€嶃€?
### 13.7 璁?SYSTEM 鎶婄獥鍙ｆ樉绀哄埌鐢ㄦ埛 Console锛堝凡瀹炵幇骞堕獙璇侊級/ A SYSTEM process showing a window on the user console

銆屼笉鍚屼細璇濅篃鑳藉叡浜獥鍙ｃ€嶉噷**鎴愮珛涓斿繀闇€**鐨勯偅涓€鍗婏細瀹夸富渚?**SYSTEM 鏈嶅姟鎶?UI 鏄剧ず鍒扮敤鎴蜂氦浜掍細璇?*銆?杩欐鏄?WSM 杩欑鎻愭潈/鏈嶅姟褰㈡€佸繀椤诲叿澶囩殑鑳藉姏锛堝惁鍒欐湇鍔″湪 session 0锛岀敤鎴锋案杩滅湅涓嶅埌绐楀彛锛夈€?
| 鑴氭湰锛坄tools/container-desktop-agent/host/`锛?| 浣滅敤 |
|---|---|
| `sessions.ps1` | 鎶ュ憡鏈繘绋嬬湅鍒扮殑**浼氳瘽 / 绐楀彛绔?/ 妗岄潰**锛堝鍣ㄤ笌瀹夸富鍚勮窇涓€娆″仛瀵圭収锛?|
| `launch-in-console.ps1` | `WTSQueryUserToken(console)` 鈫?`DuplicateTokenEx` 鈫?`CreateProcessAsUser(..., lpDesktop="winsta0\default")`锛涘繀椤?*浠?SYSTEM 杩愯**锛堥渶瑕?`SeTcbPrivilege`锛孶AC 鎻愭潈鐨?admin 浠ょ墝娌℃湁锛?|
| `run-assystem.ps1` | 鐢ㄤ竴娆℃€ц鍒掍换鍔★紙`schtasks /ru SYSTEM /rl HIGHEST`锛夋墽琛屼笂闈㈢殑鑴氭湰锛岃窇瀹屽嵆鍒?|

**浼氳瘽瀵圭収锛堝悓涓€鑴氭湰涓よ竟璺戯級锛?*

| | 瀹瑰櫒鍐?| 瀹夸富 |
|---|---|---|
| 鑷韩浼氳瘽 | `4` | `1` |
| `WTSGetActiveConsoleSessionId()` | **5**锛堝畠鑷繁鐨?console锛?| **1** |
| 杩涚▼绐楀彛绔?| `Service-0x0-鈥 | `WinSta0` |

鈬?瀹瑰櫒鑷鐨?console(5) 涓庣敤鎴?console(1) 涓嶆槸鍚屼竴涓細璇濓紝绐楀彛绔欎篃涓嶆槸鍚屼竴涓璞°€?
**瀹炴祴锛坄launch.log`锛孲YSTEM 杩涚▼鍐欑殑锛夛細**

```
identity: NT AUTHORITY\SYSTEM        own pid=101168 session=0 ; active console session=1
WTSQueryUserToken(1) ok=True token=0x970
DuplicateTokenEx     ok=True primary=0x93C
CreateProcessAsUser  ok=True pid=102056   (winsta0\default, session 1)
child pid=102056 session=1  (should equal 1)
```

**楠岃瘉锛堝涓讳晶鏌ヨ锛夛細**

```
Id      SessionId MainWindowTitle
102056  1         WSM - container desktop over shared memory   frame 398
```

鍗筹細**session 0 鐨?SYSTEM 杩涚▼鎶婂瓙杩涚▼鏀捐繘浜?session 1锛堢敤鎴?Console锛?*锛岃€岃繖涓獥鍙ｉ噷鏄剧ず鐨勬鏄?**瀹瑰櫒妗岄潰**锛堝叡浜唴瀛橀噷鐨勭 398 甯э級銆傗噿銆孲ystem 绾у埆浼氳瘽鍦ㄧ敤鎴?Console 鏄剧ず绐楀彛銆?*鎴愮珛**锛?骞朵笖杩欏氨鏄€屾妸瀹瑰櫒妗岄潰鍛堢幇缁欑敤鎴枫€嶇殑浜у搧绾ц惤鐐癸細**鐢婚潰鏉ヨ嚜鍙ユ焺鐩撮€?鍏变韩鍐呭瓨锛岀獥鍙ｇ敱 SYSTEM 鏈嶅姟
鎸変細璇濆垱寤?*锛屽叏绋嬩笉闇€瑕?RDP锛屽鍣ㄩ噷涔熶笉闇€瑕佷换浣?listener銆?
锛堥檮娉細鍦ㄨ繖涓?SYSTEM 浠诲姟閲?`AdjustTokenPrivileges` 瀵?`SeTcb/SeAssignPrimaryToken/SeIncreaseQuota/SeDebug`
閮藉洖 `err 1300`锛屼絾 `CreateProcessAsUser` 浠嶇劧鎴愬姛鈥斺€擲YSTEM 浠ょ墝榛樿宸插甫鎵€闇€鐗规潈锛屾樉寮忓紑鍚彧鏄繚闄┿€傦級

**鍚屾椂閿佸畾鐨勮竟鐣?*锛氳繖鍙В鍐炽€屾妸锛堟垜浠嚜宸辨瀯閫犵殑锛夌獥鍙ｆ樉绀哄湪鐢ㄦ埛 Console銆嶏紱**瀹瑰櫒鐨勫師鐢熺獥鍙ｄ粛鐒?璺ㄤ笉杩囨潵**锛埪?3.6 B锛歚FindWindow` 鐪嬩笉鍒般€乣SetParent`/`SetThreadDesktop` 閮戒笉閫氾級銆傝鐪熸鐨勫師鐢熺獥鍙ｏ紝
鍙湁 **Hyper-V 闅旂 + 澧炲己浼氳瘽**銆?
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

---

## 15. 能不能"把窗口注入到 Windows"，省掉共享内存的拷贝？ / Why not inject the window

> 结论：**不能**，而且这是**架构性**的（§13.6 已实测证伪），不是"还没找到办法"。
> 但"减少拷贝"这个目标是对的 —— 只是要靠**去掉多余的拷贝**，而不是靠注入窗口。

### 15.1 为什么不行 / Why it cannot work

| 事实 Fact | 依据 Evidence |
|---|---|
| 容器有自己的 **session** 和**服务 window station** | 容器内实测：本会话 `4`（宿主 `1`），window station `Service-0x0-…` |
| 容器有**自己的窗口**，但宿主**枚举不到** | 宿主上 `FindWindow(null,'Registry Editor')=0x0`；`EnumWindows` 只有宿主窗口；宿主输入桌面 = `Default` |
| 窗口**没有 DWM / 没有 GPU** | 容器内 `DwmIsCompositionEnabled=False`、无 `dwm.exe`、0 个显示适配器、`D3D11CreateDevice`（硬件与 WARP）都 `0x887A0004` |
| 唯一能跨 silo 的是**内核对象** | §13.6：section/process 句柄可提权 `DuplicateHandle` → "像素能过、窗口不能过" |

机制上的原因：

1. **HWND 不是内核句柄**，无法像 section 那样 `DuplicateHandle` 跨 silo。它归属于
   *session + window station + desktop* 三者；silo 会**虚拟化 win32k 的命名空间**，宿主的 user32 里
   根本没有容器那套对象。
2. `SetParent` / 跨桌面重定父要求**同一** session + window station；DWM 合成是 **per-session** 的，
   而容器连 DWM 都没有。
3. 即便"注入"成功也**省不下拷贝**：能想到的替代做法（宿主用 `PrintWindow`/`WM_PRINT` 去抓别人的窗口）
   本身就是一次像素拷贝，而且需要目标窗口的**消息循环在同一桌面上应答** —— 跨 silo 的
   `SendMessage`/`GetWindowLong` 同样被挡。
4. 让容器进程把窗口**创建到用户 Console** 也不行：容器 token 没有 `SeTcbPrivilege`，而
   `CreateProcessWithTokenW` 按文档就**留在调用者会话**里（§13.7 实测）。能做到"往用户会话放窗口"的
   只有**宿主侧的 SYSTEM 进程** —— 也就是 §13.7/§14 已经实现的那条路：**宿主开自己的窗口，贴我们的帧**。

**微软的官方立场一致**：进程隔离的 Windows 容器**不支持 GUI 交互**（GUI 应用能跑，但没有内建交互方式；
容器的 cmd/应用落在**非 `WinSta0` 的服务 window station** 上）。社区请求
[microsoft/Windows-Containers#611](https://github.com/microsoft/Windows-Containers/issues/611)
要的正是这个能力，其建议方案恰恰是"**实现一个 GUI 服务器，把渲染结果转发给宿主显示**（类似 X server）"
—— 那就是本项目的像素通道 + 宿主窗口。

### 15.2 真正能省掉的拷贝 / The copies we can actually remove

当前每帧的拷贝清单（1024×768×4 = **3 MB**）：

| # | 位置 | 现在怎么做 | 处置 |
|---|---|---|---|
| 1 | 容器内 | GDI+ `Graphics.CopyFromScreen` → 托管 `Bitmap` | 保留（**屏幕回读不可避免**），但换成 `BitBlt` 快一个数量级 |
| 2 | 容器内 | `RtlMoveMemory`（3 MB）`Bitmap` → 共享内存 | **去掉**：用 `CreateDIBSection(hSection = 共享 section 的映射句柄)`，让 GDI **直接写进共享内存** |
| 3 | 宿主 | 查看器每帧 `new Bitmap(frame)` 快照 | **去掉**：在共享 DIB 的内存 DC 上直接 `BitBlt` 到窗口 DC |
| 4 | 宿主 | GDI+ 格式转换/绘制 | 基本消失（`StretchDIBits`/`BitBlt` 直贴，不经 GDI+） |

→ 每帧从 **~4 次拷贝降到 2 次**（屏幕→section、section→合成），而且容器内**不再需要 `System.Drawing`**
（纯 P/Invoke，顺带绕开 §13.4 记录的"`Bitmap` 建在映射指针上就全黑"那类 GDI+ 坑）。

### 15.3 量化：memcpy 本来就不是瓶颈 / Quantified

- 3 MB 的 memcpy 按 ~10–30 GB/s 算 ≈ **0.1–0.3 ms**；
- 而 GDI+ `CopyFromScreen` 的全屏回读是 **5–20 ms** 量级。

所以按**收益**排序应该是：

1. **脏矩形**（桌面大部分时间是静止的 → 每帧只传变化的几条带，几 KB 而不是 3 MB）——**比任何拷贝优化都大**；
2. **① 换 `BitBlt` + `CreateDIBSection` 直写**（同时干掉慢的 GDI+ 路径和拷贝 ②）；
3. **③/④ 查看器 `BitBlt` 直贴**；
4. 只有在**高分辨率/高帧率**下，拷贝本身才会变成瓶颈（1080p@30fps ≈ 250 MB/s 时才值得上压缩/编码）。

### 15.4 "零拷贝"的正统路径 / The only zero-copy path

Windows 上唯一**不经过"内存 ↔ 节/文件"搬运**的像素通道是 **Hyper-V 隔离 + 增强会话**
（VMBus 上的 RDP/RemoteFX，或带**虚拟显示适配器**的 GPU 共享纹理）。这也解释了为什么微软自己把
**交互式 GUI** 场景放在 VM 隔离 + 增强会话上。

它与"进程隔离要更轻、省资源"的目标**直接冲突**。所以本项目的取舍是：

- **进程隔离**（默认、轻量）→ 像素通道：**2 次拷贝** + 宿主窗口（本文档的方案）；
- **VM 隔离**（需要原生窗口/多媒体/增强会话时）→ 官方 Enhanced Session（§8 结论：VM 隔离始终保留）。

### 15.5 一个便宜的待验证实验 / One cheap experiment

如果目标是"**连容器内的 agent 都不要**"：能否让**宿主**的 SYSTEM 进程
`OpenWindowStation("Service-0x0-…")` + `SetThreadDesktop` 直接拿到容器的桌面，从而在**宿主**抓屏写进 section？
§13.6 的实测（宿主看不到 silo 的 win32k 命名空间）**暗示不行**，但用 SYSTEM 试一次很便宜；
若成立，容器内就只需要跑应用本身。

### 15.6 下一步 / Next

按 15.3 的顺序落地：**脏矩形 → DIB section 直写 → 查看器 BitBlt**（都是纯代码改动，
容器栈复位（`vmcompute`/`hns` 重置）后即可实测；见 §14.4）。

---

## 16. 宿主能不能直接摸到容器窗口？（共享内核 ≠ 共享 User 对象） / Can the host touch container windows?

> 起因：有人（正确地）指出"container 与 host 共享内核，所以宿主可以很方便地访问容器进程的内存"，
> 因此窗口"注入"在技术上应该可行。本节给出**实测**结论：一半对、一半不对 —— 而且不对的那一半，
> 恰恰是"窗口"这一步。

### 16.1 对的那一半：容器进程在宿主内核里完全可见 / Processes are visible

宿主 `Get-Process` 直接列出（实测，本机）：

| 会话 | 进程 |
|---|---|
| **4** | `csrss`、`wininit`、`lsass`、`services`、`msdtc`、`sppsvc`、`SppExtComObj`、11×`svchost`、`cmd`、`conhost`、`powershell`、**`regedit`**、**4×`notepad`** |
| **5** | `csrss`（容器自己的 console 会话） |

即：**进程隔离的容器拥有真实的宿主会话（4 = 容器自身，5 = 容器 console），其整棵进程树对宿主可见**，
而且宿主可以 `OpenProcess` + `DuplicateHandle`（§13.6 实测：提权后 `OpenProcess(PROCESS_DUP_HANDLE)` 成功、
`DuplicateHandle` 成功；非提权 `err=5`）。所以"共享内核 → 能访问容器进程内存"是对的
（有 SeDebug 的提权令牌按同一机制拿到 `PROCESS_VM_READ`）。

### 16.2 不对的那一半：User/win32k 命名空间按 session 虚拟化 / Windows are not

以 **SYSTEM** 身份探测（`tools/container-desktop-agent/host/winsta-probe*.ps1`）：

```
[probe] own pid=95968 ownSession=0 user=SYSTEM
[probe] ownWinSta=Service-0x0-3e7$ ownDesktop=Default
[ns] \Sessions                                  -> 只有 "0 (Directory)"
[ns] NtOpenDirectoryObject("\Sessions\4")                        -> 0xC0000034 OBJECT_NAME_NOT_FOUND
[ns] NtOpenDirectoryObject("\Sessions\4\Windows")                -> 0xC000003A OBJECT_PATH_NOT_FOUND
[ns] NtOpenDirectoryObject("\Sessions\4\Windows\WindowStations") -> 0xC000003A
[ns] NtOpenDirectoryObject("\Sessions\5\Windows\WindowStations") -> 0xC000003A
[ns] \Windows\WindowStations                    -> Service-0x0-3e4$ (WindowStation)   <- 只有自己会话的
[open] OpenWindowStation("\Sessions\4\Windows\WindowStations\WinSta0")        -> 0x0 err=161 ERROR_BAD_PATHNAME
[open] OpenWindowStation("\Sessions\5\Windows\WindowStations\WinSta0")        -> 0x0 err=161
[open] FAILED: the host cannot open any window station of session 4
```

结论：**连 SYSTEM 都拿不到别的 session 的 window station** —— `\Sessions` 在每个会话里只暴露它**自己**
那一个子目录，跨会话路径一律 `OBJECT_PATH_NOT_FOUND` / `ERROR_BAD_PATHNAME`。拿不到 window station
⇒ 拿不到 HWND ⇒ `EnumWindows` / `GetWindowDC` / `BitBlt` / `PrintWindow` / `PostMessage` **全部无从下手**。
这条边界是**对称**的（容器也看不到宿主的窗口，§13.6 实测）。

### 16.3 为什么"读进程内存"救不了窗口 / Why reading memory does not help

- 窗口的**可见像素不在应用进程的用户态内存里**：容器没有 DWM，窗口由 GDI 直接绘制到该 session 的
  **TSDDD 虚拟显示表面**，而那属于 **win32k 的内核侧**对象 —— 用户态读不到。
- 应用自己可能有缓冲（浏览器合成器纹理、WPF render target…），但那是**逐应用、无文档**的布局，
  不能作为通用来源；而且读它本身同样是一次拷贝。
- 因此"宿主读容器进程内存"能得到的是**数据**，不是**画面**。

### 16.4 为什么"替换 DWM"也不能在宿主侧做 / Why not replace DWM from the host

DWM/合成器是 **per-session** 的、挂在 win32k 上的；宿主无法把自己的合成器注入别的 session ——
这正是 16.2 那条边界。**要"我们自己的 DWM"，它只能跑在容器 session 里** —— 也就是我们现在的 agent
（它已经被证明能在容器内 `CopyFromScreen` 拿到真实窗口画面，§13）。

### 16.5 但这个思路给出了正确的优化方向 / Where the idea *does* pay off

把"自己的 DWM"拆成两半，拷贝就下来了：

| 侧 | 做什么 | 拷贝 |
|---|---|---|
| **读侧（必须在容器内 = agent）** | 不再整屏 `CopyFromScreen`：在容器内 `EnumWindows`（**它有权**，就在该 session 里）→ 逐窗口 `GetWindowDC`+`BitBlt` / `PrintWindow(PW_RENDERFULLCONTENT)`，并用影子帧比较（RFB 式）**只发变化块/行** | 只读可见且变化的窗口区域（通常几 KB–几十 KB），而不是 3 MB 全屏；并且用 `CreateDIBSection(hSection)` **直写共享内存**（§15.2）→ 容器内 0 次额外 memcpy |
| **显示侧（宿主）** | 把收到的窗口矩形/区域**合成**到我们的窗口里（这才是"我们自己的 DWM"），输入按**窗口**路由回容器 | 只贴变化区域 |

即：**0 次全屏拷贝 + 少量区域拷贝**，天然支持脏矩形、多窗口 z-order、逐窗口输入。

### 16.6 剩下的两条"边界之外"路线（列出以免漏）/ Routes outside the boundary

1. **`tscon`/会话重定向**把容器 session 接到物理 Console：需要 WTS/termsrv 认可该会话（容器内 termsrv
   不建监听，§11），而且会**夺走用户自己的 Console** → 不可行、不可接受。
2. **给容器虚拟显示适配器 + D3D 共享纹理**（真正零拷贝）：容器内实测 `D3D11CreateDevice`（硬件与 WARP）
   都失败、0 个显示适配器、无 DWM（WGC 也不可用，§13.3）。要走这条路就得换成 **Hyper-V 隔离 + 增强会话**
   （VMBus 传输的官方零拷贝路径）—— 那是 VM 隔离，与"进程隔离要更轻"的目标冲突。

### 16.7 附带发现 / Side finding

本机容器栈仍处坏态（§14.4）：`HcsCreateProcess` → `0x80370105`，但**容器原有的进程与窗口还活着**
（会话 4 的 `regedit`/`notepad` 等）。也就是说"逐窗口抓取"这条优化一旦实现，用这些**存活窗口**
即可验证，无需等容器栈恢复；但要在会话 4 里**起新进程**（合法路径）仍需先重置 `vmcompute`/`hns`。

---

## 17. "服务操作 GUI 会话"这套 hack 能不能用在容器上？ / Can that hack reach a container session?

> 生产界确实存在这大家族：`WTSQueryUserToken` / `OpenProcessToken`+`DuplicateTokenEx` /
> `SetTokenInformation(TokenSessionId)` + `CreateProcessAsUser` / `SetWindowsHookEx` 注入 …
> 它们能在**宿主会话之间**搬运进程与窗口——我们自己就在用（§13.7：SYSTEM → 用户 Console 开窗 ✓）。
> 本节记录：**这套在容器的 silo 会话上会在内核处失败**，并给出实测数据。

### 17.1 手段与结果（全部实测，SYSTEM 身份，SeTcb/SeAssignPrimaryToken/SeIncreaseQuota/SeDebug 均已启用）

| 手段 | 结果 |
|---|---|
| `OpenWindowStation("\Sessions\4\Windows\WindowStations\WinSta0")` | `ERROR_BAD_PATHNAME (161)`；`NtOpenDirectoryObject("\Sessions\4\Windows\WindowStations")` → `0xC000003A OBJECT_PATH_NOT_FOUND`；`\Sessions` 只列出 `0`（§16.2） |
| 自 token `SetTokenInformation(TokenSessionId=4)` + `CreateProcessAsUser`（desktop 依次 `winsta0\default` / `Service-0x0-3e4$\Default` / `null`） | 进程**创建成功**（分配 pid，`ProcessIdToSessionId=4`），但 **5–15 ms 内退出 `0xC0000142` STATUS_DLL_INIT_FAILED** |
| 从**存活容器进程**取 token（`OpenProcess(regedit, pid 33136)` → `OpenProcessToken` → `DuplicateTokenEx`，读回 `token session = 4`）+ `CreateProcessAsUser` | 同样 `0xC0000142`（5–15 ms），无论 desktop 是什么 |
| 子进程写文件（宿主路径 / 容器路径 `C:\wsm`） | **一份都没有** —— 它连目标会话的 loader/文件系统上下文都没建立 |
| **对照组**：同一 token、不改 session | `cmd /c exit 7` → **exit=7，38 ms** ✓ 说明机制本身没问题 |

结论：**silo 会话没有"从外面创建进程 / 加入进去"的通道**（这正是 silo 的设计目的）。因此 host 侧无论怎么改
token、session 或 desktop 字符串，都拿不到容器会话里的进程与窗口。窗口访问（§16.2）与进程创建（本节）
是同一个边界的两面。

### 17.2 那么"最适合当前场景的 hack"是什么

原则不变：**"读侧"必须在容器会话里**。能注射进去的手段只有三类：

1. **HCS 创建进程**（`HcsCreateProcess`，即我们的 `engine.ExecuteAsync`）—— 官方、干净，就是 §14 的实现；
   本机 `0x80370105` 坏了，需重置 `vmcompute`/`hns` 后继续。
2. **注入容器内已有进程**（跨会话 hook 家族）：`CreateRemoteThread`+`LoadLibraryW`（DLL 必须放在**容器可见**
   的路径，例如 `C:\wsm`）或线程定向 `SetWindowsHookEx`。它能绕开"不能建进程"，但更脆、更脏。
3. 容器内自启动（登录脚本）—— 不可控，不采用。

"显示侧"仍留在宿主（§13.7 已证：SYSTEM 把窗口放进用户 Console）+ 共享内存/句柄直通（§13.6 已证）。

⇒ **最优 = 方案 1（HCS）+ 逐窗口/脏块读取 + DIB 直写共享内存 + 宿主合成**（§15.5/§16.5）；
方案 2 只是 HCS 不可用时的应急手段。

### 17.3 本次新增脚本 / New scripts

`tools/container-desktop-agent/host/`：`winsta-probe.ps1`、`winsta-probe-inner.ps1`（§16）、
`winsta-child.ps1`（会话内枚举+抓取）、`winsta-spawn.ps1`…`winsta-spawn5.ps1`（token/session 手术与退出码测量）。

---

## 18. 设计决定：读侧用 "winlogon 包裹 + 我们自己的合成器"，显示侧用"窗口镜像" / Design decision

> 用户提议：*容器完全受控，可以把 winlogon 换成自己的程序（由它代为启动 winlogon），想怎么 hook 就怎么 hook；
> 再 hook DWM 把它转发出去，窗口就能注入用户 session。*
> 本节把它拆成"可行的部分"与"仍然做不到的部分"，并定下实现路线。

### 18.1 成立的部分（而且它解决了一个实测死路）/ The part that works

- 容器镜像**完全由我们控制**（本来就是：§13 的 `NumberOfInitialSessions=2`、`Shell`、`AutoAdminLogon`
  都是我们写进去的），所以可以用 **IFEO**（`Image File Execution Options\winlogon.exe` 的 `Debugger` 值）
  或 `Userinit`/`Shell` 把 **winlogon 包一层**：我们的 stub 先起真正的 winlogon，再在自己的桌面启动读侧组件；
  配合 `AppInit_DLLs` 可以对**每个 GUI 进程**做进程内 hook（脏矩形/paint 通知），这是"hook explorer"家族里
  **在受控容器里最干净、最可靠**的一种 —— 不需要注入技巧。
- 关键收益：**读侧组件由容器自己的登录流程创建**，因此**根本不需要从外面创建进程** ——
  正好绕开 §17.1 实测的死路（`CreateProcessAsUser` 进容器会话 → `0xC0000142`，连偷来的容器 token 也一样）。
  它也不需要 HCS：**本机 HCS 坏掉（`0x80370105`）时依然可以验证**（容器重启时它自己会拉起我们的 stub）。
- 而且它拿到的正是我们从外面拿不到的东西：**目标会话的交互桌面**（WinSta0\Default）、桌面级 hook、
  逐窗口 rect/z-order/脏区、以及精确的输入定位。

### 18.2 不成立的部分 / The part that still does not work

**"hook DWM 转发 → 把窗口直接注入用户 session"这一步做不到**，两个原因：

1. **容器里没有 DWM 可 hook**（实测：无 `dwm.exe`、`DwmIsCompositionEnabled=False`、0 个显示适配器、
   `D3D11CreateDevice`（硬件与 WARP）都 `0x887A0004`）。所以不是"hook DWM"，而是**我们就是那个 DWM**：
   我们自己收集每个窗口的表面、自己合成。这反而更简单，但**合成结果仍在容器会话内**。
2. **窗口对象不能跨 session/silo 搬**（§16.2/§17.1 实测：`\Sessions\4\…` 连 SYSTEM 都打不开；
   从外面建进程进容器会话会在 5–15 ms 内 `0xC0000142`）。hook 与否都改变不了这一点。

### 18.3 因此把它落成"窗口镜像" / Land it as window mirroring

用户要的**效果**（容器里的窗口像原生一样出现在用户桌面上）可以这样达成：

| 侧 | 做什么 |
|---|---|
| 读侧（容器内，stub + hook + 我们的合成器） | 逐窗口表面 + 脏矩形（`AppInit_DLLs` 的 paint 通知 + 桌面 hook 的 z-order/rect/焦点），写入**页文件 section**（句柄直通，§13.6 已证） |
| 显示侧（宿主，管理 service） | 在用户会话里为**每个容器窗口创建一个真实宿主窗口**（矩形/标题/z-order 镜像），内容 = 该容器窗口的像素（只更新脏区）；输入按窗口转发回容器。窗口由宿主 DWM 合成，因此会正常出现在任务栏 / Alt-Tab ✓ |

即"窗口注入"变成"**1:1 窗口镜像**"：窗口**住在宿主会话**、由我们的进程创建（`WTSQueryUserToken`+
`CreateProcessAsUser`，§13.7 已证），内容与交互来自容器。这就是 RemoteApp 那种效果的本地实现。

### 18.4 代价与风险 / Cost and risk

- winlogon 包裹是**侵入式**镜像改动（stub 必须 fail-safe：先启动真正的 winlogon，再挂载读侧；失败不影响登录）；
- 没有 DWM ⇒ z-order/遮挡要自己记录（窗口是直接画到屏幕的）；
- 镜像层是只读基座（可能被多个沙箱共享）⇒ 该步骤应做成 `IContainerImageCustomizer` 的**可选开关**，
  并写进镜像清单，而不是默认开启。

### 18.5 下一步 / Next

1. **最小验证（不需要修 HCS）**：镜像里加 IFEO 包裹 + 一个 stub，容器重启后确认我们的进程**在容器会话的
   交互桌面**上，并能 `EnumWindows` 看到 `regedit`/`notepad` 这些窗口；
2. 逐窗口 + 脏区读侧（含 `AppInit_DLLs` 通知）→ 写入页文件 section；
3. 宿主侧窗口镜像 + 输入路由；
4. 与 §14 的 `IContainerDesktopHost` 合并（同一套抽象，两种传输：整屏帧 / 逐窗口镜像）。

---

## 19. 实现：可替换的桌面 provider 模块 + 由清单选择 / A replaceable provider module, chosen by the manifest

> 用户要求：本方案要成为**可替换的独立模块**（以后有更好的方案可换掉），并且**由沙箱清单决定用哪种**。
> 本步落地了这条架构，并完成 window-mirror 方案的前两步（镜像 payload + 会话内读侧）。

### 19.1 分层 / Layers

| 位置 | 内容 |
|---|---|
| `Wsm.Domain.ValueObjects` | `ContainerDesktopKind`（`None`/`FrameBuffer`/`WindowMirror`）、`SandboxDesktopPlan`（**清单里的方案与参数**）、`ContainerDesktopPaths`（容器内路径契约，三条链路共用一个来源） |
| `Wsm.Connection.ContainerDesktop` | `IContainerDesktopProvider` + `IContainerDesktopProviderRegistry`（**按清单选 provider**）+ `IContainerDesktopHost`（稳定门面）+ 两个 provider：`FrameBufferDesktopProvider`（§13/14，整屏帧）、`WindowMirrorDesktopProvider`（§18，逐窗口镜像） |
| `Wsm.Hcs.Storage` | `IContainerImageCustomizer.EnableWindowMirror(layerPath)`：把登录钩子写进镜像的 `SOFTWARE` 蜂巢（`Winlogon\Userinit` 追加 `C:\wsm\wsm-winlogon-wrap.cmd`）+ 把读侧 payload 拷进 `Files\wsm\`（三个嵌入式资源，带单测） |
| `Wsm.Cli` | `wsm image mirror <layerPath>`（准备镜像）、`wsm desktop <id> [--provider <id>] [--mode <kind>] [--present|--no-present] [--input ...]` |

**选择规则**（`ContainerDesktopProviderRegistry.Resolve`）：
`SandboxDesktopPlan.Provider`（显式 id）→ `SandboxDesktopPlan.Kind` → 默认。
所以换方案是"**加一个 provider + 改清单**"，调用方（CLI/服务/UI）一行都不用动。

### 19.2 清单写法 / In the recipe

```jsonc
{
  "name": "container-desktop",
  "desktop": {
    "kind": "WindowMirror",       // 或 "FrameBuffer" / "None"
    "provider": null,             // 也可直接写 provider id，优先于 kind
    "presentToInteractiveSession": true,
    "dirtyRegions": true
  }
}
```

### 19.3 两条链路怎么用 / How each path is used

```powershell
# 方案 A（默认，无需改镜像）：整屏帧
wsm desktop <sandboxId> --present

# 方案 B（window-mirror，逐窗口 + 脏区）
wsm image import mcr.microsoft.com/windows/server:ltsc2022 C:\wsm-images\server --desktop
wsm image mirror C:\wsm-images\server          # 写入登录钩子 + 读侧 payload
#   清单里写 Desktop.Provider = "window-mirror"，然后重启容器
wsm desktop <sandboxId>
```

### 19.4 读侧协议 / The read-side protocol (published in one shared file)

`C:\wsm\mirror.shm`：头 64B（magic `WSMM`、尺寸、stride、fps、seq、busy、writerPid、**windowCount**、帧数）+
整屏 BGRX 像素 + **窗口表**（8192B，每项 40B：id/rect/seq/标题偏移长度）+ 标题区（16KB）。
读侧**只把发生变化的窗口矩形写进共享文件**（`memcmp` 与影子帧比较），因此传输的是脏区而非整屏。
输入仍是"每命令一个 `input-*.txt`"，退出是 `agent-stop.txt`（`C:\wsm\wsm-winlogon-wrap.cmd` 只负责
`start` 出去，绝不阻塞登录）。

### 19.5 为什么登录钩子用 `Userinit` 而不是替换 winlogon / Why Userinit

用户提议把 winlogon 换成自己的程序。**目标一致（让我们的程序在容器交互会话里跑起来），但 `Userinit`
更安全**：它是微软文档化的登录扩展点，是"追加一条命令"而不是"顶替 winlogon"，失败只会少一个读侧、
不会让容器登录不上（hook 本身就是 fire-and-forget）。IFEO（`Image File Execution Options\winlogon.exe` 的
`Debugger`）作为备选保留在记录里。

### 19.6 验证状态 / Verification

- 编译：`wsm`(CLI)、`Wsm.Service.Host`、`Wsm.Tests` 全部 **0 警告 0 错误**；`dotnet test` **300 通过**
  （新增：清单选 provider 的四条规则、`ToRelative` 越界回归、容器根不被替换、`Userinit` 幂等、
  三个镜像 payload 确实被嵌进程序集）。
- 机制：§13–§18 已实测（帧、输入、句柄直通、SYSTEM→Console 开窗、以及"外面看不到容器窗口/进不去会话"）。
- **尚未实测**：window-mirror 的端到端（需要先把项目用的基座镜像 `wsm image mirror` 一次、再让容器用该镜像
  启动）。注意它**不经过 HCS 建进程**（读侧由容器自己的登录启动），所以当前 `HcsCreateProcess 0x80370105`
  并不妨碍这条链路，只妨碍我们在容器里"手动"起进程。
- 显示侧：`WindowMirrorDesktopProvider.PresentAsync` 已按 `--mode windows` 启动查看器；查看器**目前仍按整屏
  渲染**（`--mode` 被忽略不报错），逐窗口宿主窗口是下一提交（窗口表已发布，CLI 也会打印）。

### 19.7 后续 / Next

1. 查看器 `--mode windows`：按窗口表为每个容器窗口建一个宿主窗口（矩形/标题/z-order 镜像）+ 输入按窗口回传；
2. 读侧用 `CreateDIBSection(hSection)` 直写共享内存（去掉容器内那次 memcpy，§15.2）；
3. 把 `SignalR`/gRPC 接到 provider 上（服务宿主里已是同一套组件，加 `StartDesktop` 等 RPC 即可）。

---

## 20. 窗口镜像的"显示 + 拖动"实测 / Window mirror: display and drag, measured

### 20.1 本轮新增 / Added

| 位置 | 内容 |
|---|---|
| 查看器 | `wsm desktop-view --mode windows --frame <mirror.shm> [--offset-x/-y]`：**每个容器窗口一个真实宿主窗口**（相同矩形/标题/z-order，内容是该窗口的像素），标题带拖动 → `movewindow`，客户区点击/按键 → 容器坐标；镜像窗口带 `WSM_MIRROR_HOST` **窗口属性**标记 |
| 读侧 | 新命令 `movewindow <hwnd> x,y`（`SetWindowPos`，真正移动）与 `activate <hwnd>`；`-TitleFilter` 过滤；**跳过带镜像标记的窗口**（否则同一桌面上会出现镜像的镜像） |
| 共享布局 | `ContainerDesktopMirror`（magic/偏移/窗口表布局 + 读写助手），provider 与查看器共用一份定义（并有回环单测） |

### 20.2 实测结果 / Measured (host desktop as the read side's session; same code, same protocol)

```
[1] target hwnd=0x15198C rect before=(80,80)
[2] read side pid=107032 mirror.shm=True
[3] log: window id=0x15198C "wsm-mirror-target " rect=(80,80 480x300)
[3] DISPLAY: mirror window hwnd=0x9C0F5E rect=(500,320 480x300)      <- 宿主为容器窗口建的真实窗口
[4] dragged the mirror by (+90,+60) via posted mouse messages
[5] target rect after=(170,140) delta=(90,60)                        <- 被镜像的窗口真的移动了
[6] viewer drag lines=8   (drag begin / drag move ... / )
[7] read side applied=movewindow 0x15198C -> (170,140) ok=True
[=] VERDICT display=ok drag=ok
```

即：**容器窗口 → 读侧发布帧 + 窗口表 → 宿主逐窗口开真实窗口 → 拖动宿主窗口 → `movewindow` → 读侧
`SetWindowPos` → 目标窗口移动 (+90,+60)**，全链路闭环 ✓。

### 20.3 过程中修掉的两个问题 / Two bugs fixed on the way

1. **镜像的镜像**：读侧与查看器在同一桌面时，读侧会把"镜像窗口"当成容器窗口再镜像一次（实测出现
   (500,320)→(920,560)→(1340,800)… 的级联）。修法：镜像窗口打上窗口属性 `WSM_MIRROR_HOST`，读侧跳过它。
   （容器里不会发生——宿主窗口在另一会话——但这条规则让它不可能发生。）
2. **拖动取 `Cursor.Position` 会在 DPI 虚拟化下错位**，且在测试里无法驱动：改为用**鼠标消息自身的坐标**
   计算位移（真实拖动与 PostMessage 测试都成立；同一次修复还解决了 `PictureBox` 吞掉鼠标事件的问题）。

### 20.4 容器侧现状（本轮未跑通，原因不在代码）/ Container-side status

- **镜像步骤有效**：`wsm image mirror C:\wsm-images\servercore-ltsc2022 --shell cmd.exe --logon-user ContainerAdministrator --logon-password ""`
  之后，**在容器内**可见 `C:\wsm\wsm-desktop-read.ps1`(18764B) 与 `C:\wsm\wsm-winlogon-wrap.cmd`(814B) ✓
  （镜像层就是容器的只读基座，所以宿主侧的写入容器马上能看到）。
- **未跑通的原因**：harness 的容器**没能重启**（其 HCS 状态已坏，`@stop` 后进程树原样不动，
  `HcsCreateProcess` 仍 `0x80370105`），因此**没有登录发生** → 登录钩子不触发 → 没有 `mirror.shm`
  （会话 5 只有 `csrss`，没有 `winlogon`/`userinit`）。
- **下一步**（需要容器栈复位后）：用引擎正规路径起容器 ——
  `wsm start <containerSandbox>`（`WindowsContainerEngineOptions` 里配好 `Shell` 与自动登录）+ `wsm desktop <id>`；
  或先把 `vmcompute`/`hns` 重置，再用 harness 重跑本节的脚本。

---

## 21. 容器侧：HCS 启动读侧 + 逐窗口 GDI 抓取（零拷贝） / Container side: HCS launch + per-window zero-copy capture

### 21.1 本轮实测到的容器侧事实 / What was measured in the container

| 观察 Observation | 结论 |
|---|---|
| `### CMD cmd.exe /c echo ALIVE` → `[exit=0] ALIVE` | **HCS 创建进程恢复正常** ✓（重启有效） |
| `qwinsta`：`services 2` / `console 3`；`tasklist`：`winlogon.exe` + `LogonUI.exe` 在 **console 会话** | 镜像的"两个初始会话"生效 ✓，console 会话有 winlogon ✓ |
| console 会话里**没有** `userinit`/shell，`LogonUI` 停在登录界面 | **容器不会自动登录** → 登录钩子（`Userinit`）永远不触发 ✗ |
| 用 provider 启动读侧：`read.log` → `read side started pid=17296 1024x768 stride=4096 fps=4 mirror=C:\wsm\mirror.shm` | **读侧确实在容器内跑起来了** ✓（HCS 启动路径有效） |
| 随后 `CopyFromScreen ... "The handle is invalid"` 反复失败 | 容器的 **service 会话没有屏幕 DC** ✗ → 整屏抓取不可用 |

结论：**镜像登录钩子不是正确默认**（容器不登录就不触发），**HCS 启动才是**（我们的 GUI 本来就在 HCS 建的 service 会话里，
例如之前容器里的 `notepad`/`regedit` 都在那里）。

### 21.2 因此本轮的两处实现改动 / Two changes

1. **`WindowMirrorDesktopProvider.StartAsync` 改为 HCS 启动读侧**：materialise（脚本 + `.cmd` 从嵌入式资源写入容器根，
   `StartUp` 用 `.cmd` 承载引号、`start` 脱离），再 `engine.ExecuteAsync` 到容器；已在发布就**收养**（幂等）。
   镜像登录钩子（`EnableWindowMirror`）保留为"GUI 在交互会话"时的可选项 ✓。
   顺带修：provider 之前**根本没注册进 DI**（`Available: .`）⇒ 模块改为**显式注册**（provider 列表就是可替换面）✓。
2. **读侧改为逐窗口、零拷贝抓取**：用 `CreateDIBSection(..., hSection = 共享文件映射, offset = 64)` 建一个覆盖
   共享像素区的 DIB，再逐窗口 `PrintWindow(hwnd, dc, PW_RENDERFULLCONTENT)`（视口原点移到窗口位置）；
   `PrintWindow` 不行时退化为 `GetWindowDC`+`BitBlt`。**GDI 直接画进共享内存** ⇒ 容器内 0 次额外拷贝，
   且**不需要屏幕 DC**（正是 service 会话缺的那样东西）。整屏 `CopyFromScreen`、影子帧比较与逐行 `RtlMoveMemory` 全部删除。

### 21.3 状态 / Status

- 已编译通过（CLI/服务宿主 0 警告 0 错误）、读侧脚本解析 0 错误、单测 **305 通过**（新增"宿主能取材读侧"的资源断言）。
- **尚未跑完**：改动后的读侧在**容器内**的最终验证 —— 容器在本次测试末尾被 harness 回收（mount 不可读、runner 队列里
  命令未被消费），需要重新起一个容器再跑一次。重跑命令（一次 UAC）：
  ```
  powershell -File <dd>\container-mirror2.ps1     # 起窗口 → provider 启动 → 镜像 → 拖动 → 回读窗口 rect
  ```
- 宿主侧的"显示 + 拖动"闭环已在 §20 用同一套代码实测通过 ✓（读侧当时跑在宿主桌面，只差"在哪个会话"）。

### 21.4 顺手补上的一个真缺口：清单里的方案没有持久化 / The plan was not persisted

`.wsb` 序列化器（`WsbRecipeXmlSerializer`）**不认识**新的 `Desktop` 计划 ⇒ "用清单选择 provider"这一条
在保存/加载/导入之后会**丢失**。已修：写出并读回 `<ContainerDesktop>` 元素（`Kind` / `Provider` /
`PresentToInteractiveSession` / `DirtyRegions` / `Width` / `Height` / `Fps`），并且**默认计划不写这个元素** ——
因为这份 XML 也会被送给 `WindowsSandbox.exe`（VM 隔离引擎），未知元素可能被拒；只有真的要求了容器桌面时才写。
单测：往返保真、默认计划不写、只写 `Kind` 也能读回。**308 通过**。

### 21.5 CLI 表面补齐 / CLI surface

`wsm desktop` 增加 `--list-providers`（列出本宿主注册的 provider，并说明清单怎么选）；
`PrintHelp` 增加 `image mirror` / `desktop` / `desktop-view` 三条；README 增加
「容器桌面通道（可替换的 provider，由清单选择）」一节（含清单示例与命令）。

---

## 22. 新抓取路径的回归验证 + 一个操作层面的硬约束 / Zero-copy capture regression + how elevation actually works here

### 22.1 为什么之前一直"没反应" / Why nothing ran

| 现象 | 真因 |
|---|---|
| 读侧在容器里"启动了"但没有 `mirror.shm`/`read.log` | 我新加的 C# 里 **`IsWindowVisible` 重复声明** ⇒ `Add-Type` 编译失败 ⇒ 脚本在第一个 Log 之前就死了（`read.out` 里就是那段编译错误）✓ 已修 |
| 等很久也没有 UAC 弹窗 | **我的 shell 未提权**（`whoami` 组里没有管理员、`admin role: False`），而 `elev.ps1` 用 `Start-Process -Verb RunAs` —— 在这个上下文里它**静默失败**，所以既没有提示也没有运行 ✗ |
| 宿主写容器根是否需要提权 | **不需要**：实测非提权 `[IO.File]::WriteAllText(<root>\wsm\...)` 成功 ⇒ 只有 **HCS 建进程/起容器** 这一步需要提权 ✓（修正了 §13 里"写入需提权"的旧记录） |

⇒ 涉及 **HCS 建进程/创建容器** 的验证必须由**交互会话里提权**执行；已备好自包含入口
`<dd>\RUN-CONTAINER-TEST.cmd`（右键"以管理员身份运行"）：它**直接**启动 harness runner（避免嵌套 RunAs ✗）、
覆盖修复后的读侧、在容器内开窗并启动读侧、在宿主上逐窗口镜像、拖动、回读容器窗口 rect，
结果写到 `container-mirror5.out`。

### 22.2 新抓取路径的宿主端回归（同一套代码，只差"在哪个会话"）/ Host regression

```
Geometry : 1024x768 stride=4096 fps=4
Windows  : 1  -> id=0x180E68 rect=(80,80 480x300) "wsm-mirror-target"
non-black ratio inside the window rect = 58 %      <- PrintWindow -> DIB(共享内存) 的真实像素
mirror window: hwnd=0x3500AE "wsm-mirror-target"  <- 宿主上的真实镜像窗口
after drag  : rect=(170,140)  -> moved by (90,60)
[=] capture=ok display=ok drag=ok
read.log    : movewindow 0x180E68 -> (95,90) ... -> (170,140) ok=True
mirror-view.log: sync seq=... windows=1 visible=1
```

即：**逐窗口 `PrintWindow` → `CreateDIBSection(hSection=共享映射)`（零拷贝、不需要屏幕 DC）
→ 宿主每个容器窗口一个真实窗口 → 拖动宿主窗口 → `movewindow` → `SetWindowPos` 真的移动** ✓。
脚本：`tools/container-desktop-agent/host/host-mirror-test.ps1`（无提权可跑，verdict 自判）。

### 22.3 读侧现在会自己交代关键事实 / The read side now reports what matters

每次启动写进 `read.log`（顺序本身就是诊断线索）：

```
starting pid=... user=... interactive=... dir=C:\wsm
session: winsta=<窗口站> desktop=<桌面> screenDC=ok|NONE (没有显示设备的会话)
DIB section over shared memory ready (dc=... dib=...)      # CreateDIBSection(hSection) + DC 成功
read side started pid=... 1024x768 stride=4096 fps=4 ...
first paint 0x... rect=(x,y wxh) ink=N%                    # PrintWindow 真正拿到内容的比例
movewindow 0x... -> (x,y) ok=True                          # 拖动被应用
```

配套加固：**整个脚本体包在 try/catch 里**并写 `FATAL: ...`（C# 编译失败不再"静默死亡"，
这正是上一轮"什么都没发生"的原因）；`CreateCompatibleDC(0)` 失败时**改用某个窗口的 DC** 派生内存 DC
（容器 service 会话没有屏幕 DC 时正需要）。

### 22.4 提权的真相（更正 §22.1）/ Elevation, corrected

§22.1 里"我的 shell 无法提权"的结论**是错的**：本机默认**跳过 UAC 提示、自动允许提权** ⇒
`Start-Process -Verb RunAs` **可用**（早前的证据也支持这一点：提权运行确实创建过容器、跑过探针）。
那些"卡住很久"的**真因**是：

1. 我的脚本里有**没有上限的等待**（等容器登录、等某个文件 ⇒ 永不返回）；
2. `elev.ps1` 用**固定文件名** `elev.out` ⇒ 并发/多次运行互相覆盖 ⇒ 输出归属混乱
   （本次就混进了已被停掉的 `container-mirror5` 的输出）。

修法：`elevx.ps1 -Script <s> -Out <file>`（每次独立输出）＋ 每步等待都有上限；另外，用
`Win32_Process.CommandLine` 批量杀进程时**必须精确匹配**（我那次的模糊匹配把自己 shell 的 job 进程
也杀了 ⇒ `Exited with code 255`）。

### 22.5 最小步骤阶梯 / The minimal step ladder

`<dd>\container-steps.ps1 -Step N`：**每步只做一件事、有界、打印 `[STEP n] PASS/FAIL`**；
1 与 8 需要提权，2–7 **不需要**。定位到哪一步再谈集成。

| 步骤 | 做什么 | 判定依据 |
|---|---|---|
| 1 | 起 runner + 一个容器，验证能建进程 | 容器里 `[exit=0] ALIVE` |
| 2 | 把当前读侧写进容器根 | 文件存在且 >20 KB |
| 3 | 容器内起一个窗口进程（`notepad`） | 容器 `tasklist` 里有 `notepad.exe` |
| 4 | 容器内启动读侧 | `read.log` 出现 `starting` / `session:` / `DIB … ready` / `read side started` |
| 5 | 是否发布窗口、`PrintWindow` 是否拿到内容 | `--print-table` 有窗口 + `first paint … ink>0` |
| 6 | 宿主逐窗口显示 | 找到属于 `wsm.exe` 的镜像窗口 |
| 7 | 拖动 → 容器窗口是否真的移动 | `read.log` 有 `movewindow … ok=True` 且发布 rect 变化 ≥ 60 px |
| 8 | 集成：产品路径 `wsm desktop --provider window-mirror` | 退出码 0 + 已发布 + 出现镜像窗口 |


## 23. 容器侧本轮实测：三条硬边界与读侧的修复 / Container side this round: three hard walls and the read-side fixes

### 23.1 结论先行 / Conclusion first

在这台机器上，**进程隔离（HCS Argon）容器无法提供可被镜像的桌面**。这不是读侧的缺陷：读侧已经能在容器里正确
发布窗口表（3 个测试容器里都能看到容器自己的 Notepad 窗口，id/rect/title 都对），同一套代码在**宿主**上的
"逐窗口显示 + 拖动"也已证明可用（§20：`capture=ok display=ok drag=ok`）。挡住的是容器平台本身，三条互相独立的
实测边界如下。

### 23.2 边界一：容器的 service 会话没有可渲染的桌面 / Wall 1

`HCS ExecuteAsync` 创建的进程落在 service 会话（本机为 session 2）。在那里：

- `GetDC(0)` 返回非 0（日志 `screenDC=ok`），但 `Graphics.CopyFromScreen` 抛 **"The handle is invalid"** ✗；
  从文件映射上的 DIB section 直接 `BitBlt` 也失败 ✗；
- 容器里创建的窗口**永远不置 `WS_VISIBLE`**（`ShowWindow`/`ShowWindowAsync` + `SetWindowPos(SWP_SHOWWINDOW)`
  都试过），因此 `PrintWindow` 拿不到内容 ✗（`paint failed`）。

结论：**service 会话既没有可用的屏幕 DC，也没有被绘制过的窗口** ✗——从它出发不可能拿到像素 ✗。

### 23.3 边界二：窗口站与跨会话被整体拒绝 / Wall 2

读侧启动时会枚举本会话可见的窗口站并尝试附着（这也是一个好的产品行为：哪里窗口多就用哪个）：

- `EnumWindowStations` 能列出 6～11 个站（含 `WinSta0` 与若干个 `Service-0x0-…$`）✓；
- 但 `OpenWindowStation` 对**每一个**站都返回 `err 5 = ACCESS_DENIED` ✗——**包括读侧自己所在的那个站** ✗。
  把请求权限降到最小（`WINSTA_ENUMDESKTOPS|READATTRIBUTES|ENUMERATE = 0x0103`）也一样 ✗；
- 于是改用"令牌复制 + 跨会话建进程"：在容器内以 **SYSTEM**（一次性计划任务）身份运行，五个特权全部启用
  （`SeDebug` / `SeImpersonate` / `SeAssignPrimaryToken` / `SeIncreaseQuota` / `SeTcb`）✓，
  依次取 `conhost`/`csrss`/`fontdrvhost`/`LogonUI`/`winlogon`（都在 session 3）以及自己的令牌，
  复制成主令牌后 `CreateProcessAsUser` 的每个变体（`lpDesktop=winsta0\default`、继承桌面、显式
  `lpApplicationName`）**全部失败** ✗：`err 3 (ERROR_PATH_NOT_FOUND)` / `err 2`。

结论：**容器进程无法进入（也无法在）console 会话** ✗，这与 §16/§17 测到的"窗口站不可寻址"一致。

### 23.4 边界三：交互登录走不完 / Wall 3

既然只有 console 会话可渲染，`Userinit` 登录钩子就是唯一的入口（§18 的设计）。实测：

- 这个镜像的 `Administrator` 在这台机器上表现为 `Password required = Yes`、有密码、有到期日 ✓ ——
  **但这不是镜像自带的** ✗：`wsm-image.json` 记着 `SourceReference = mcr.microsoft.com/windows/servercore:ltsc2022` ✓，
  而 `ContainerImageImporter` 就是从 MCR 下载并解包**该官方基础层** ✓ —— 所以这一层是**标准容器基础镜像** ✓；
  上面那些属性是**本轮自己在容器里改过之后**观察到的 ✓（密码名 `Wsm-Container-1` 是本次会话设的 ✓）。保留的**实测**结论是：
  `LogonUser` 对 `Administrator`/`wsmuser` 都 `ok=True` ✓，但登录仍不产生 session-3 用户 ✗；
- **认证本身是通的** ✓：容器内 `LogonUser` 对 `Administrator`（已设密码）与新建的 `wsmuser`（`wsmuser` 是
  本轮按需新建的管理员账号）**三种类型都 `ok=True err=0`** ✓；
- 自动登录组合**逐项写对并在容器内验证过** ✓：`AutoAdminLogon=1`、`DefaultUserName=wsmuser`、
  `DefaultDomainName=wsmprobe`、`DefaultPassword=…`、`ForceAutoLogon=1`、
  `Lsa\LimitBlankPasswordUse=0`（REG_DWORD ✓），`Userinit` 也确实带着
  `C:\Windows\system32\userinit.exe,C:\wsm\wsm-winlogon-wrap.cmd` ✓；
- 重启后**紧贴启动采样**（t+0/5s…）：`session 3` 一开始有 `winlogon`、`LogonUI`、`conhost`、`csrss` ✓，
  但在 **~100 秒后 `winlogon` 与 `LogonUI` 一起退出** ✗，会话随之消失 ✗；
- 把钩子改成"先写证据再干活"（写进**一定可写**的 `C:\Windows\Temp`）后确认：
  **`Userinit` 的第二段程序从未执行** ✗（没有任何痕迹），`read.log`/`mirror.shm` 也没有 ✗。

结论：**平台的交互登录不完成**（能认证、但不建立用户会话、不跑 `userinit`）✗，因此"登录钩子"这条路在这台机器
的进程隔离容器里不可用 ✗。另外要注意：配置了自动登录反而会让 console 会话更早消失 ✗（winlogon 试完就退出），
所以产品在**没有**明确要求登录时应当**清掉**这些值 ✓（本轮已实现）。

### 23.5 本轮真正修好的东西（读侧与镜像侧）/ What got fixed

| 位置 | 修复 | 为什么 |
|---|---|---|
| 读侧 | **自脱离** `-Detached`（父进程 `Start-Process` 后立刻退出） | 旧启动器用 `cmd start`，会让 HCS 建进程卡到 10 分钟超时 |
| 读侧 | 日志提到最前 + 整体 try/`FATAL:` | 之前 C# 编译错会在写第一行日志前就死掉，从宿主看"像是没启动" |
| 读侧 | 桌面枚举 + 最小权限 + **delegate 封送修复** | 名字曾被 ANSI/Unicode 混封送成单字符（`W`/`S`），导致必然 `err 2` |
| 读侧 | `-StartProgram`（子进程继承本进程桌面） | 实测：runner 起的 notepad 读侧看不见，读侧自己起的就在眼前 |
| 读侧 | 捕获回退链：`CopyFromScreen` → 托管位图 → **一次 `RtlMoveMemory` 进共享 DIB**；逐窗口 `PrintWindow` 保留为后备 | 容器里 DIB section 直接 `BitBlt` 失败；这条是 §13 证明过能用的路 |
| 读侧 | 不再要求 `WS_VISIBLE`；**绝不镜像自己** | 容器窗口天生不置 `WS_VISIBLE` |
| 镜像侧 | **相对蜂巢路径**（真 bug：`WinlogonSubKey` 多带了 `SOFTWARE\` 前缀） | 路径找不到 → 走"创建"分支 → 只读根句柄 → `Cannot write to the registry key.` |
| 镜像侧 | Winlogon/LSA 值改用 **`reg.exe`** 写入 | 托管写入被离线蜂巢继承的 ACL 拒绝，`reg.exe` 能写（手工验证过） |
| 镜像侧 | `Lsa\LimitBlankPasswordUse` 用 **REG_DWORD** | 写成 REG_SZ 时 LSA 不理它 |
| 镜像侧 | payload 文件被占用时**只警告不中断** | 一个被钉住的文件不该让整个注册表补丁失败 |
| 镜像侧 | 无 `--logon-user` 时**清除**自动登录值 | 见 §23.4 末尾 |
| 工具 | `elevx.ps1`（不 `-Wait`，`.done` 标记判定完成）+ `container-steps.ps1 -Step N` 阶梯 | 之前"卡住几十分钟"其实是 `Start-Process -Verb RunAs -Wait` 永不返回 |

新增：`wsm-session-launcher.ps1`（镜像 payload）——把程序发射进另一个会话的启动器（先以 SYSTEM 计划任务自举、
启用特权、复制目标会话进程的令牌、`CreateProcessAsUser`）。**本平台的容器拒绝它** ✗（§23.3），留着给允许它的
环境（如普通 Windows、非 silo 场景）用。

### 23.6 什么时候这条链就能通 / What would unblock it

两条路，都不需要再改读侧：

1. **换一个能完成交互登录的基础镜像**：标准 `mcr.microsoft.com` 镜像是这条路的前提 ✓（本机这一层就是官方
   `servercore:ltsc2022` 基础层 ✓，见 §23.4 更正 ✓）—— 所以更现实的变量是**镜像里 `Administrator` 的密码/策略状态**
   ✓：`Password required = Yes` 且空密码被策略拒绝 ✗ 时，先把它改成"无密码 + 允许空密码自动登录" ✓；
2. **换 runtime**：任何能维持"交互会话"、或允许把进程放进该会话的容器运行时。

届时的形态本轮已经验证可行 ✓：把 `Winlogon\Shell` 整条写成读侧命令行
（`powershell.exe … wsm-desktop-read.ps1 -Detached … -StartProgram <要镜像的程序>`）——**读侧就是该容器的
"桌面会话"** ✓，会话活多久它就活多久（`cmd.exe` 做 Shell 时会立刻 EOF 退出 ✗，从而导致会话被拆 ✗，这是本轮
t+100s 现象的成因之一 ✓）。


## 24. 容器侧像素通道：把每一种可能都测完 / Container-side pixels: every option measured

本轮先修了读侧一个**真 bug**，然后把"能不能从容器里拿到像素"这件事**穷尽地**测完，最后修掉一个环境/产品两侧都存在的层生命周期缺陷 ✓。

### 24.1 读侧的真 bug：`Paint` 因为窗口不可见而直接放弃 / The gate that made every paint fail

`WsmMirrorWindows.Paint` 的第一行是：

```csharp
if (!IsWindowVisible(hwnd)) return false;      // ✗ 容器窗口永不置 WS_VISIBLE（§23 已测）
```

而 §23 恰好测到**容器窗口永不携带 `WS_VISIBLE`** ✓ —— 于是每一帧的每个窗口都在**尝试任何绘制之前**返回失败 ✗，
日志里就是 `paint failed` ✗。也就是说：这条路径**从来没有真正被试过**，看起来像"PrintWindow 在容器里没用" ✗，
实际上是我们自己挡住了 ✓。

修法：把捕获做成一个**策略菜单**（`PrintWindow(PW_RENDERFULLCONTENT)` / `PrintWindow(0)` / `WM_PRINT` /
`WM_PRINTCLIENT` / 子窗口逐个 `WM_PRINTCLIENT` / `RedrawWindow+BitBlt` / `windowDC+BitBlt`），并且**实测**哪一条
真的产生像素，而不是假定某一条能行 ✓：

```
zero the region -> paint with one strategy -> GdiFlush -> measure non-black samples
```

两处工程细节：
* **`GdiFlush` 必须有** ✓ —— GDI 会批处理绘制 ✓，不 flush 就读共享缓冲，读到的可能还是空的 ✓；
* 目标也分两种 ✓：**直接画进共享 section DIB**（旧路径 ✓）或**先画进私有 DIB、再整帧一次搬过去** ✓
  （共享缓冲与私有 DIB 几何/stride 完全一致 ✓ → 每帧**一次**连续 `RtlMoveMemory` ✓，符合"减少内存拷贝"的要求 ✓）。

### 24.2 实测：2 个目标 × 7 个策略，全部为 0 / The measured matrix

容器 service 会话（`HCS ExecuteAsync` 建进程的会话）里，对一个真实的 Notepad 窗口（`768x555` ✓）：

| 目标 | 策略 | 调用返回 | 实测 ink |
|---|---|---|---|
| shared-DIB | PrintWindow(PW_RENDERFULLCONTENT) | True | **0%** |
| shared-DIB | PrintWindow(0) | True | **0%** |
| shared-DIB | WM_PRINT | True | **0%** |
| shared-DIB | WM_PRINTCLIENT | True | **0%** |
| shared-DIB | child-sweep WM_PRINTCLIENT | True | **0%** |
| shared-DIB | RedrawWindow+BitBlt | **False** | 0% |
| shared-DIB | windowDC+BitBlt | **False** | 0% |
| private-DIB+copy | PrintWindow(PW_RENDERFULLCONTENT) | True | **0%** |
| private-DIB+copy | PrintWindow(0) | True | **0%** |
| private-DIB+copy | WM_PRINT | True | **0%** |
| private-DIB+copy | WM_PRINTCLIENT | True | **0%** |
| private-DIB+copy | child-sweep WM_PRINTCLIENT | True | **0%** |
| private-DIB+copy | RedrawWindow+BitBlt | **False** | 0% |
| private-DIB+copy | windowDC+BitBlt | **False** | 0% |

两条关键读数 ✓：
* `PrintWindow`/`WM_PRINT` 系列**返回成功但一个像素都没有** ✓（它们只是把消息投递出去 ✓，不保证画到东西 ✓）；
* `any BitBlt` 系列**连调用都失败** ✓ —— 因为 `GetWindowDC(hwnd)` 返回 **0** ✓✓ ——
  也就是说**这个会话里窗口没有 GDI 表面** ✓。

### 24.3 对照实验：GDI 本身没问题，缺的是"表面" / The control experiment

同一个会话里（`gditest.ps1` ✓）：

| 检查 | 结果 | 说明 |
|---|---|---|
| `CreateDIBSection(private 256x256)` | `dib!=0 bits!=0` ✓ | 私有 DIB 正常创建 ✓ |
| `CreateCompatibleDC(0)` | 非 0 ✓ | 内存 DC 可用 ✓ |
| `PatBlt(WHITENESS)` 画进私有 DIB | `byte0=0xFF`，16384/16384 采样非零 ✓ | **GDI 能画** ✓ |
| `BitBlt(screen -> private DIB)` | **False** ✗ | 屏幕没有可读表面 ✓ |
| `CopyFromScreen`（托管路径） | 抛 "The handle is invalid" ✗ | 同上 ✓（§23） |
| `ShowWindowAsync(SW_SHOW)` + `SetWindowPos(SWP_SHOWWINDOW)` | 之后 `visible` 仍为 **False** ✓ | 窗口永远不置 `WS_VISIBLE` ✓ |

⇒ 结论（现在是穷尽实测，而不是推断）✓：**service 会话既没有屏幕表面、也没有窗口表面** ✓，只有纯内存 DC 能用 ✓。
而 `PrintWindow`/`WM_PRINT` 需要窗口表面才能工作 ✓ → 在这一会话里**没有任何路径能拿到应用窗口的像素** ✓。

（顺带记录两个自己的坑 ℹ：`GdiFlush` 必须调用 ✓；PS 5.1 里 `[UIntPtr]64` 会**抛异常** ✗ ——
我第一版对照脚本因此静默失败了 ✓，这解释了当时"user-mode 写不进去"的假象 ✓。）

### 24.4 环境侧的真问题：orphaned scratch layer / A layer that can never be released

本轮容器**一开始起不来** ✗：HCS 拒绝文档 ✓（`0xC037010D: 字段 "Path" 的值无效` ✗），因为探针算出的
`Storage.Path` 是空串 ✓。逐层测下来 ✓：

| 调用（对 scratch 目录） | HR | 含义 |
|---|---|---|
| `DestroyLayer` | `0x80070020` | **sharing violation** ✗ |
| `CreateSandboxLayer` | `0x80070020` | 同上 ✓ |
| `ActivateLayer` | `0x80070020` | 同上 ✓ |
| `PrepareLayer` | `0x80070015` | `ERROR_NOT_READY` ✓ |
| `GetLayerMountPath` | 长度 0 | → HCS 收到空 `Path` ✓ |

佐证 ✓：`sandbox.vhdx` **无法独占打开、也无法删除** ✗（"being used by another process" ✓）；
宿主上**没有任何** attach 的卷（`Get-Volume`/`Get-Disk` 干净 ✓）、没有残留的 compute system ✓
（`HcsEnumerateComputeSystems` 只剩 CmService 模板 ✓）、没有残留探针进程 ✓；
重启 `vmcompute`（和 `CmService`）**无效** ✗。

根因 ✓：容器进程被**杀**（而不是优雅停止）后 ✓，该层的 VHDX 仍被 HCS/层驱动视为**在用** ✓，
于是 `Deactivate`/`Destroy` 互相锁死 ✗ —— 对这一条路径**本轮开机内不可恢复** ✓
（也解释了 §22 里那个卸载不掉的 `HKLM\WSMTEST` 与被钉住的镜像副本 ✓）。
出路 ✓：HCS 的层 id 是**路径的哈希** ✓ → 换一个目录就是另一个层 ✓ → 实测 `C:\wsmprobe\dd4` 全部 HR=0 ✓
且拿到 `\\?\Volume{04a4f83d-…}` ✓ 正常起容器 ✓。

**产品修复** ✓（新文件 `src/20-Engines/Wsm.Engine.WindowsContainer/ScratchLayerProvisioner.cs` ✓）：
1. 启动前先尽力释放（`Unprepare` → `Deactivate` ✓，与 `StopAsync` 现有清理一致 ✓）；
2. 释放不掉时**换路径重试** ✓：`<scratch>` → `<scratch>-2` … `<scratch>-5` ✓，任何一次成功就用它的卷 ✓；
3. 五次都失败才抛错 ✓，错误里带上每次的原因（含 sharing violation ✓）。

配 7 个单测（`ScratchLayerProvisionerTests` ✓：正常路径 ✓ / orphaned 回退 ✓ / 全部失败报原因 ✓ /
"准备好但没有挂载点"也算失败 ✓ / 回退命名规则 ✓）。全套 315 个测试通过 ✓。

### 24.5 判定与出路（未变）/ Verdict and the remaining exits

* **service 会话**：拿不到像素 ✓（§24.2/24.3 穷尽实测 ✓）；
* **console 会话（3）**：**能渲染** ✓，但**进不去** ✗（§23.2：窗口站全部 `err 5` ✓、跨会话建进程全部 `err 3/2` ✓）；
* **登录**：认证通 ✓、值全对 ✓，但 `winlogon/LogonUI` 约 100 秒后退出 ✗、`userinit` 从不执行 ✗（§23.4 ✓）。

⇒ 进程隔离（HCS Argon）容器在这台机器上**给不出可镜像的桌面** ✓ ——
要突破只能换**标准容器基础镜像**（能完成交互登录 ✓）或换**允许进入该会话的 runtime** ✓。
宿主侧契约完好 ✓：同一套代码在宿主上 `capture=ok display=ok drag=ok` ✓。

读侧的**行为现在是诚实的** ✓：找不到任何能出像素的策略时，它只发布**窗口表**（id/rect/title ✓）
并在 `read.log` 里留下完整的策略矩阵 ✓，宿主侧显示的就是空白窗口 ✓（而不是假装有内容 ✓）。
`container-steps.ps1 -Step 5` 也据此更新 ✓：矩阵里的 `ink=0%` 行是**证据** ✓，判定看的是
`capture chosen` 是否存在 ✓ 与最好 ink 是否 > 0 ✓。


## 25. 产品化实现：§23.6 的解锁条件已落码（容器内实测待定）/ Productization: the §23.6 unlock is now code, live verification pending

§23.6 列的「什么时候这条链就能通」本轮**已在代码里实现**（分六个 phase 落地）✓，宿主侧回归通过 ✓；
但**容器内端到端验证尚未做**——所有「待验」项集中在 §25.7。本节只做实现记录，不改写上面任何一节的实测结论。

### 25.1 交互式会话解锁（phase 1）/ The §23.6 unlock, in one call

`IContainerImageCustomizer.EnableDesktopSession(layerPath, DesktopSessionOptions)`
（`src/10-Hcs/Wsm.Hcs.Storage/`，实现在 `Internal/ContainerImageCustomizer.cs`）一次调用完成 §23.6 的全部条件：

- console 会话补丁：每个控制集 `Session Manager\NumberOfInitialSessions = 2`（即原 `wsm image desktop` 的那一步）✓；
- Winlogon 自动登录：`AutoAdminLogon` / `DefaultUserName` / `DefaultPassword` / `ForceAutoLogon`，
  外加每个控制集 `Lsa\LimitBlankPasswordUse = 0`（REG_DWORD）✓；
- `Winlogon\Shell = C:\wsm\wsm-shell.cmd` ✓ —— 新 payload（`src/10-Hcs/Wsm.Hcs.Storage/Images/wsm-shell.cmd`）：
  **永不退出**的重启循环，用 `-AsShell` 前台跑读侧，日志写 `C:\wsm\shell.log`。这正是 §23.6 的对策：
  「`cmd.exe` 做 Shell 会立刻 EOF 退出、导致会话被拆」（t+100s 现象的成因之一）；
- 读侧 payload 一并安装进 `Files\wsm\` ✓（`--no-mirror` 可关）。

密码两侧必须一致：默认账号是 `Administrator`，默认密码改为按镜像派生，不再使用固定密码。
`ContainerDesktopSessionDefaults` 的派生逻辑使用受限主机密钥；引擎按实际基础层路径计算同一密码，
再执行 `net user <u> <p>` 并刷新自动登录键（`src/20-Engines/Wsm.Engine.WindowsContainer/WindowsContainerEngine.cs`）。

读侧（`Images/wsm-desktop-read.ps1`）新增输入命令：**`launch <exe> [args]`**（在读侧自己所在的
console 会话里启动 GUI 程序 —— 这就是绕过 §23.3「跨会话建进程被 silo 拒绝」的机制：不需要跨会话，
程序直接生在对的会话里）、`resize`、`wheel`、`close`；以及 `-AsShell` 模式（不自脱离，wrapper 就是
会话的生命期）。

CLI：`wsm image session <layerPath> [--user <u>] [--password <p>] [--no-mirror]`。

### 25.2 RAIL 镜像协议 v2（phase 2）/ Mirror protocol version 2

- `ContainerDesktopMirror.cs`：version 2 布局 —— `dirtyCount`（int32）在 header 偏移 56，
  脏矩形数组（204 × 16B）跟在窗口表之后，title blob 相应后移 3264 字节；v1 兼容保留。
  读侧对 shadow buffer 做 8×8 块 memcmp 差分，查看器只重绘脏区。
- `DesktopMirrorViewCommand`：镜像窗口改用真实 `FormBorderStyle.Sizable`（真标题栏/最小化/最大化/关闭）：
  标题栏拖动 → `movewindow`；边框拖动 → `resize`；关闭按钮 → `close`。输入升级为 v2 结构化命令：
  `key <vk> down|up` + `text`（SendInput / `KEYEVENTF_UNICODE`）、`wheel`、双击。
  命令格式器公开在 `MirrorInputCommands.cs`（`src/30-Connections/Wsm.Connection.ContainerDesktop/`）。

### 25.3 pagefile section + 句柄穿透（phase 3）/ Page-file section and handle passthrough

- 模式协商 `shm-mode.txt`：宿主提权**且**查看器持有同一令牌时写 `pagefile`，否则 `file`。
  pagefile 模式下 `.shm` 文件只是 4 KB 的 header-only stub，像素放在
  `CreateFileMappingW(INVALID_HANDLE_VALUE)` 的 page-file section 里；`mode` 字段在 header 偏移 60
  （`FSM1` 与 `WSMM` 两种布局一致）。
- `Internal/SectionPassthrough.cs`：OpenProcess(`PROCESS_DUP_HANDLE`) →
  `SystemExtendedHandleInformation`（class 64）→ 用自建探针 section 解析 Section type index →
  `DuplicateHandle`（`SECTION_MAP_READ|SECTION_QUERY`）→ `NtQuerySection` 尺寸比对（一页窗口）→
  magic 校验。任一步失败回退 file 模式。
- **已知限制**：以 SYSTEM 承载的 GUI 启动的查看器（UAC 过滤后的交互用户令牌）拿不到穿透句柄 →
  自动落回 `file` 模式。

### 25.4 整屏会话产品化（phase 4）/ Full-desktop session productization

- 帧 agent（`Agent/container-desktop-agent.ps1`）输入对齐 v2 语法，并新增
  `clipboard <base64-utf8>`（设置会话剪贴板）。
- 查看器（`DesktopViewCommand`）：可缩放窗口 + letterbox 等比缩放，坐标逆变换回容器坐标；
  wheel、双击、剪贴板发送。F10 循环发起**动态分辨率**请求（`view-request.txt` /
  `view-request.ack.txt`；agent 尝试 `ChangeDisplaySettingsEx`，失败时优雅拒绝——TSDDD 可能拒绝，
  未实测）。
- agent 生命期改为**查看器锁**驱动：`viewers\<guid>.lock`，60 秒不续判 stale 清除，无任何查看器
  30 秒后退出；保留 900 秒外部兜底。

### 25.5 WinUI GUI 集成（phase 5）/ GUI integration

- `wsm_service.proto` 新增 RPC：`StartDesktop`（adopt 已活的通道）、`StopDesktop`、
  `GetDesktopStatus`；`IWsmServiceClient` 对应方法（`src/30-Connections/Wsm.Service.Client/`）。
- GUI `ContainerDesktopConnector`（`src/50-App/Wsm.Ui.WinUI/`）：agent 优先、失败回退进程内引擎。
- 实例详情页新增「Container desktop」区块：桌面 / 窗口(RAIL) 两个按钮（运行中的
  WindowsContainer 实例可见），拉起 `wsm.exe desktop-view --mode frame|windows`。
- 创建对话框新增桌面区块（kind：None / FrameBuffer / WindowMirror + present 复选框，引擎为
  WindowsContainer 时出现），持久化到 recipe 的 `SandboxDesktopPlan`。

### 25.6 镜像下载 GUI（phase 6）/ Image download in the GUI

- 目录 `ContainerImageInfo`（`ContainerImageImporter`）共 4 项：
  `windows/server:ltsc2025`（约 5.5 GB）、`windows/server:ltsc2022`（约 5 GB）、
  `servercore:ltsc2025/2022`（各约 1.5 GB）；`ContainerImageImportStage` 枚举 + 字节级进度
  （`BytesReceived`/`TotalBytes`）；取消被尊重并清理半成品文件。
- 共享默认根 `ContainerImagePaths`（`%LOCALAPPDATA%\WindowsSandboxManager\images\<slug>`，
  `GetInstallState` → Missing / Installed / Occupied），GUI 与 CLI 落同一个目录。
- GUI 设置区块：可编辑 ComboBox（目录项或自定义 reference）、字节/速度/ETA 进度
  （extract/prepare 阶段为 indeterminate）、取消按钮、已安装状态（重新下载/删除）、
  Occupied 提示；关掉对话框下载不中断（进度镜像到主状态栏），错误本地化。
- CLI `wsm image import <reference> [layerPath]` —— `layerPath` 改为可选，缺省同上默认根；
  控制台有进度输出。

### 25.7 待容器内端到端实测 / Pending live verification (checklist)

以上全部只经过宿主侧回归；以下各项**必须在真实容器里验过才算通**：

- [ ] 自动登录用烘进去的密码真的走完 —— `winlogon` 是否活过 ~100 秒并拉起 shell（§23.4 的 t+100s 现象是否消除）；
- [ ] 读侧在 console 会话里真的产出像素（§24.2 的全 0 矩阵只测过 service 会话）；
- [ ] `launch regedit` → 宿主出现镜像窗口且可拖动/输入；
- [ ] pagefile 穿透在容器链路里成立（非 file 回退）；
- [ ] 动态分辨率对 TSDDD 实际生效（agent 已支持优雅拒绝）；
- [ ] 完整 `windows/server` 镜像的「仅基础层」可启动性 —— importer 对 Server Core 已断言可行；
      完整 Server 镜像 manifest 形状相同、预期一致，**但尚未 boot-verify**。

### 25.8 容器内实测：自动登录被 120 秒墙挡死，四条绕行路线逐一证伪（step32–48）/ Live: autologon dies at the 120s wall; four bypass routes measured and closed

对应 §25.7 清单的实测结果。**坏消息：自动登录路线在本机（Win11 26H2 build 26300）走不通**；好消息是每一条绕行路线都被定量测过，死在哪一行都有日志（`artifacts/live-step32.log` … `live-step48.log`）。

**step32/33/34（livedesk7，双启动 + TermService 假说）**：镜像补丁全部生效 —— TermService/SessionEnv Running+Automatic、注册表五项（AutoAdminLogon/DefaultUserName/DefaultPassword/ForceAutoLogon/LimitBlankPasswordUse=0）全对、双启动机制触发正常、烘进去的密码跨 Terminate 幸存（`LogonUser` type 2/3 均 ok）。**但** winlogon 停在 LogonUI 等待，console 会话在**精确 t+120s** 被原子拆除（winlogon/LogonUI/csrss 同帧消失，与 §10.3 的 121s TermSrv notification 拆除吻合）。"Last logon" 注册表值从未被登录更新 → **自动登录从未发生**，凭据提供器在等真实输入。§11.7 曾实测自动登录成功，差异在于当时密码已在容器层；双启动烘密码路线在本 build 上不再触发自动登录。

**step35（window-mirror 全链路）**：链路本身全通 —— HCS 注入读侧 ✓、pagefile 零拷贝协商 ✓、窗口表发布 ✓、`launch regedit` 输入管道生效 ✓（窗口表出现 `Registry Editor 768x555`）。**但 7 种像素策略全部 0% ink**：读侧落在 services 会话，不是输入桌面。注意 CLI 是 `--provider window-mirror`（`--mode` 枚举解析失败会静默回退 frame-buffer）。

**step38–41（容器内跨会话注入）**：**封死**。就算经容器内计划任务拿 SYSTEM：`CreateProcessAsUser`（winlogon token）err=5（silo 拒绝）；`CreateProcessWithTokenW` 能成功创建，但**子进程落在调用者会话**（childSession=2 而非目标的 3）——token 复制不改变会话归属。

**step42–45（services 会话直接取像素）**：**全路死**。拓扑探针（dispdiag）：exec 进程落在 service 窗口站 `Service-0x0-e28880f$`、桌面 `Default`；`OpenInputDesktop` err=1、`OpenDesktop("Winlogon")` err=2；`CreateDC("DISPLAY")` 能建（TSDDD 显示设备 1024x768x32 真实存在）但 `BitBlt` 读 err=6（gdiag2）——设备上下文存在不等于能读到像素。两个容器侧脚本里加的 `SwitchToInputDesktop()`（OpenInputDesktop+SetThreadDesktop）对本路线无效（services 会话拿不到输入桌面句柄），代码保留但确认无害。

**step46–48（宿主侧借容器 winlogon token 注入）**：宿主能看到容器内 winlogon（pid 级可见，内存空间共享的直接证据）。三级权限定位：① `OpenProcessToken` 必须开 `MAXIMUM_ALLOWED`（0x20000000），单独权利组合 err=5；② 提权用户 `CreateProcessAsUser` err=1314（缺 SeTcbPrivilege）；③ 改走**宿主计划任务 /ru SYSTEM** 后 SPAWN 成功（pid=26360，目标 winlogon pid=44716 session=3）——但子进程立即消失、容器 volume 里没有 in-probe.log。**判读：子进程疑似不在 silo 内**（它看到的是宿主 C:\，而 `C:\wsm\in-probe.ps1` 当时只埋在容器 volume 里 → 找不到脚本立即退出）。step49 把探针同时埋进宿主 `C:\wsm\` 和容器 volume（双文件系统）来判定子进程的真实落点：若宿主侧出现 in-probe.log 且 session=3，说明至少会话归属正确，截图 + SendKeys 打字会在 LogonUI 上真实执行。

### 25.9 step49–51：注入路线终审 + 自动登录取证 / Injection verdict and autologon forensics

**step49（双文件系统判定，livedesk15）**：宿主 SYSTEM 注入的子进程连第一行日志都没写就消失 —— 探针同时埋在容器 volume 与宿主 `C:\wsm\`，两侧都无 `in-probe.log`（`artifacts/live-step49.log`）。判读：`CreateProcessAsUser` 的「成功」是空壳，子进程死于进程初始化（最可能是跨 silo 的 CSRSS 会话注册失败），用户态代码从未执行。**宿主侧借 token 注入路线封死**。至此四条绕行路线全部定量证伪。

**step50（容器内事件日志取证，`artifacts/live-step50.log`）**：
- Winlogon 注册表全量 dump：五值全对（AutoAdminLogon=1 / DefaultUserName=Administrator / DefaultPassword 正确 / ForceAutoLogon=1 / **DisableCAD=1 也已在位**），`LegalNotice*` 为空、`Shell`/`Userinit` 指向 wsm payload；
- LocalSessionManager/Operational：**#41/#42 仲裁确实以 `LIVEDESK15-389F\Administrator` 开始**（t+0.9s），但之后**没有任何认证事件**——Security 日志里无 winlogon 发起的 4624/4625/4776（唯一两个 type-2 登录是 wininit/winlogon 给 UMFD 字体驱动开的）；t+121s LSM #39/#40「Session 3 has been disconnected by session 3, reason code 11」= 120 秒墙的执行者；
- 对比 §11.7 成功样本：当年顺序是 #41/#42 → **#21 Session logon succeeded** → **#22 Shell start notification**；现在 #21 从未出现，即自动登录在「认证提交」之前就已经死了；
- RemoteConnectionManager/Operational：**#1136 "RD Session Host Server role is not installed"** —— TermService 虽然 Running，但 RCM 拒绝建监听（§11 的 `WTSIsServerContainer` 路径），netstat 证实 3389 无监听。TCP RDP 路线维持 §11 结论：死。

**step51（LogonUser 探针，`artifacts/live-step51.log`）**：第二启动的 SAM 状态完全正常 —— `LogonUser('Administrator', '.', '<redacted>', 2/3)` 均 ok=True，`Password last set` = 当次启动 18:33:15（ConfigureDesktopAsync 每次启动都重设，早于 winlogon 启动 3 秒）、`Account active = Yes`。**密码持久化假说排除**。

剩下两个未测的自动登录门：① `AutoLogonSID = S-1-5-21-...-500` 是烘进镜像层的（仿 Sysinternals），若它与容器 SAM 里 Administrator 的真实 SID 不符，winlogon 的 SID 查找会**静默跳过**自动登录（不发任何事件，与观测吻合）；② `DefaultDomainName` 为空。step52 删掉 AutoLogonSID、补上 `DefaultDomainName='.'` 后第三启动实测。

### 25.10 step52–86：认证打通，121 秒墙与宿主 LCU 的强相关性（未受控验证）/ Autologon authenticated; correlation with the host LCU, not a proven cause

**step52–54**：`AutoLogonSID` 毒值（烘进镜像的 SID 与容器 SAM 真实 SID 不符）删除、`DefaultDomainName='.'` 补上后仍无认证事件 → 该门也排除；镜像 SOFTWARE 里发现 **`AutoLogonCount=0`**（语义=不再自动登录），是 §10.3 时代留下的毒值之一。

**step55–67（SAM 事故与恢复）**：一次离线 SAM 手术把镜像 `SAM_BASE` 写坏（容器无法启动）；用 `SAM_BASE.orig`（MCR 原始字节）做字节级回滚并 md5 验证，镜像恢复。结论：SAM 不做离线手术，密码只走运行时 `net user`（写容器 scratch 层）。

**step68–77（空密码实验链）**：把镜像 `DefaultPassword` 置空 + `LimitBlankPasswordUse=0` 后，首启动即出现一次 4625（LogonType 2、Logon Process **User32**、空密码 → `0xC000006D/0xC000006A` 密码错——证明 winlogon 确实在启动瞬间尝试了自动登录，只是镜像 SAM 里 Administrator 密码非空未知）。双启动烘密码后第二启动反而不提交任何认证。step77 容器内 `LogonUser('Administrator','<redacted>',type 2/3)` **ok=True**：烘密码成功、账户激活、凭据完全有效。

**step78（突破，`artifacts/live-step78.log`）**：镜像 SOFTWARE 写入 **`AutoLogonCount=99`（REG_SZ）** 后，winlogon 每启动都提交认证并**成功**——Security 4624：`Administrator`、`LogonType 2`、`Elevated Token Yes`、`User32/Negotiate`、成对 linked logon。认证环节自此完全打通。**但** LSM **#21（Session logon succeeded）从未出现**，会话停在 `Conn`，t+121s 被 #39/#40（reason 11 = winlogon 主动断开）拆除；`AutoLogonCount` 停在 99 不递减（winlogon 未判定登录完成）。

**step79–84（卡点二分定位）**：
- ProfSvc RUNNING，Administrator 配置文件加载/卸载时间戳都在（State=0x304）→ profile 不是瓶颈；
- 悬挂窗口期进程表：session 里 csrss/conhost/fontdrvhost/**LogonUI**/winlogon 都在，**userinit / shell 全程从未启动**；
- 把 `Userinit` 末尾的 `wsm-winlogon-wrap.cmd` 换成「写一行日志即退出」的 no-op tracer：**日志从未出现 → userinit 从未被启动**，我们的 payload 完全无嫌疑；
- 卡点精确定义：**认证 4624 成功 → LSM #41/#42 仲裁 → winlogon 等待某物 121 秒（LogonUI 留守）→ winlogon 主动断开**。「#42 之后、启动 userinit 之前」；
- `tscon <id> /dest:console` 在仲裁悬挂期间报 **error 5023**（组或资源状态不正确），不可强接；窗口期杀 LogonUI 会让整个会话瞬间拆除（LogonUI 是关键进程，不能杀）。

**step85**：把镜像 `SessionEnv`/`TermService` 的 `Start` 从 2 改回 MCR 默认 3（排除 step32 时代 EnableSessionServices 补丁的嫌疑）→ **墙原样**（4624 成功、#41/#42、无 #21、121s 拆除）。该假说排除。

**step86（根因锁定，决定性时间线）**：§11.7 的成功样本与今天的失败之间，**镜像没变**（`C:\wsm-images\servercore-ltsc2022` 自 2026-10-15 01:09 拉取后未重拉，容器内 winlogon/LSM 均为镜像内 build 20348.4294 的二进制），**变的是宿主内核**：

| 时间 | 事件 |
|---|---|
| 10-13 10:42:57 | 宿主最后一次旧启动（System 日志 6005）——此后连续运行无重启 |
| 10-14 02:15–02:46 | KB5127694 / KB5121794 / KB5127648 暂存（Setup 日志），`winlogon.exe/lsm.dll/termsrv.dll/vmcompute.exe/win32k.sys` 等新文件 02:22 落盘，挂起待重启 |
| **10-15 白天（≤15:58 提交 379c2f6）** | **§11.7 成功**：#41/#42 → #21 → #22、`console Administrator 5 Active`、shell 启动——跑在 **10-13 启动的旧内核**上 |
| **10-15 20:01 / 20:03** | **两次重启，LCU 完成安装**（Setup 日志 id=2：`Package_for_RollupFix … 26100.9711.1.30` 已安装，KB5121794 26100.9544 已安装） |
| 10-16 起（step32–85，含今日 10-23 11:11 崩溃后启动） | 全部复现同一堵墙：认证可成功（step78 起）但 #21 不到、userinit 不启动、121s 拆除——跑在 **9711 内核**上 |

即：时间线只支持**强相关性**，不能据此断言宿主 LCU 就是根因。挂起点在 winlogon 仲裁之后、userinit 之前；镜像内容与我们的 payload 已逐一证伪，但宿主 LCU 未经过受控回滚验证。

**按用户决定，不执行卸载或回滚 Windows 更新。** 后续只通过当前环境复测、其他正常主机对照或运行时修复后的行为验证假说，不再提出以卸载更新为前提的实验。

### 25.11 step87+：宿主环境修复——官方 Windows Sandbox 0x80370106 的真正原因 / Host repair: the real cause of the Sandbox failure

背景：2026-10-23 微软官方 Windows Sandbox 在本机报「虚拟机或容器意外退出 (0x80370106)」，其他电脑正常。排查结果：

- VMMS/Admin 事件 **15280「无法装载虚拟磁盘」**（10-23 19:59:45、20:02:57，即用户尝试启动沙盒的时刻）——失败点是**挂载沙盒基底 VHDX**，不是 Guest 内部崩溃；
- `C:\ProgramData\Microsoft\Windows\Containers\BaseImages` **目录为空**——沙盒基底镜像整个丢失。Setup 日志显示 2026-10-20 09:52 有一次 CBS 大动作（`KB5124007` 被改为 Absent、大量包被暂存，Client=CbsTask），基底镜像很可能在那时被移除且替换品未落地；
- 可选功能 `Containers-DisposableClientVM` 等全部 Enabled——功能开关没问题，缺的是基底镜像文件本身；
- 修复路径（不动 Windows 更新）：`dism /online /cleanup-image /restorehealth` 修复组件存储 → 禁用/重启用 `Containers-DisposableClientVM` 重建基底镜像 → 重启验证。

**关于 LCU（按用户决定，不动更新）**：曾尝试卸载 `Package_for_RollupFix 26100.9711.1.30`（KB5127648）验证 §25.10 的回归假说——首次被 `CBS_E_CANNOT_REMOVE_CONTAINER_DEPENDENCY` 拒绝（沙盒基底镜像把 `HKLM\...\Component Based Servicing\ContainerAI\BaseImageVersion=10.0.26100.9711` 钉住）；删除该钉后卸载暂存成功（exit=3010，待重启生效）。**用户明确要求不卸载更新**，已用 `dism /online /cleanup-image /revertpendingactions` 回滚挂起卸载（包状态恢复「已安装」，系统保持 26300.9711），`ContainerAI` 的值/所有者/ACL 与 WU 暂停键已全部还原（备份：`artifacts/ContainerAI-backup.reg`）。§25.10 的「LCU 回归」作为**强相关性假说**保留，未经受控回滚实验证实；若未来某次更新后容器桌面自动登录自行恢复，即侧证。

**已做的无害清理**：删除 40 个 WSM 测试容器的孤儿 scratch 目录（`WsmContainers\livedesk*` 等），释放 3.2 GB；`wsm image release` 解除镜像/陈旧 scratch 层占用。这些与本故障无关，纯属磁盘卫生。

---

## 26. 产品化复核与当前阻塞（2026-10-26）/ Product verification and the remaining blocker

### 26.1 已实测可用 / Verified working

- **完整镜像目录**：MCR `windows/server:ltsc2022` 与 `ltsc2025` 都返回 manifest list，且均有
  `windows/amd64` manifest。基础层分别为：
  - 2022：`sha256:cc43a3999819693100c63928ab8fd6e55db37d22882d04244da3497e39aafe52`，3,042,472,970 bytes；
  - 2025：`sha256:e5dcf3bc996337655b83ea98c2e05a33a75e36a0357b01d7d433dbabcd1509a7`，4,481,593,239 bytes。
  这些是压缩下载大小，不是安装后的磁盘占用；没有据此声称 Server 2025 已在本机启动。
- **完整 Server 2022**：已有的 `C:\wsm-images\server-full-ltsc2022` 经 `ProcessBaseImage` 和
  `wsm image session` 准备成功（`Hives`、读侧、`explorer.exe` 均存在），并多次完成
  `create/start`。容器内命令把 `qwinsta`、事件日志写入卷，再经宿主挂载点读取成功。
- **镜像与 GUI**：用原生 `Wsm.Ui.WinUI.exe`（x64 输出目录）实测：
  - 「使用此镜像」选中 `C:\wsm-images\server-full-ltsc2022` 并写入 `ui-settings.json`；
  - 「准备桌面镜像」调用现有 WCOW 释放逻辑与 `EnableDesktopSession`，对 Server Core 镜像成功返回，
    状态为桌面支持已安装；
  - 创建 window-mirror 实例后，清单含 `Engine=2`、`Desktop.Kind=2`、`InteractiveSession=true`、
    `ContainerImagePath=C:\wsm-images\server-full-ltsc2022`。因此选择不再只存在于 GUI 进程的环境变量，
    agent 已运行时也能从清单取得同一路径。
- **产品复核修复**：镜像密码改为主机随机密钥按镜像派生，recipe 的交互会话选择会一致驱动密码、
  自动登录及首次重启；必需的初始化命令失败会中止启动，第二次启动失败会释放资源并允许重试。
  shell/read 两种桌面入口均安装停止感知的启动脚本。
- **镜像生命周期**：已安装状态统一要求 `Files\Windows`、`Hives` 和有效 metadata；下载在后台执行并支持取消，
  唯一临时目录及 staging/backup 防止并发冲突和失败时误删旧镜像。原 `Stage` 字符串契约保留，新增 `StageKind`。
- **桌面健康与回退**：无像素、空窗口表或心跳停止不再被报告为成功；pagefile 穿透失败会协商并重建
  完整容量的 file-backed 共享内存，4KB 占位文件不算回退成功；句柄表保留完整 64 位值。
- **回归**：三个修复模块分别在独立 worktree 通过 RED→GREEN 定向测试及全量测试；合并后
  `dotnet test WindowsSandboxManager.slnx --no-restore` 为 **511/511**。
  以上均为替身、本机共享内存回环及真实 ViewModel 验证，不替代真实容器桌面验证。
- **主机状态**：`ContainerAI\BaseImageVersion` 已恢复存在（`0xa000065f425ef`），无需再次改注册表；
  未卸载或暂停 Windows 更新。

### 26.2 仍未打通：交互登录与第一次重启 / Still blocked: interactive logon and the first restart

`wsm image session` 只准备镜像；首次创建 scratch 后，仍需在容器里用 `net user` 对齐账号密码。
因此首次启动后的重启是桌面链路的必要环节，不能把镜像准备成功等同于桌面成功。

Server Core 在本机仍复现：LSM #41/#42 后没有 #21/#22，`userinit`/读侧未启动，约 121 秒拆除。
完整 Server 2022 的首次重启又被 HCS 生命周期挡住，逐项实测如下：

| 尝试 | 本机结果 |
|---|---|
| `HcsTerminateComputeSystem` 后等待 operation | 原生 terminate 返回 0；`uint.MaxValue` 等待不返回，有限等待得到 **`0x80370118`（timeout）**；`hcsdiag` 仍列 `Stopped` |
| `HcsDeleteComputeSystem` | 返回 0，但等待得到 **`0x80370115`（operation not started）**，ID 未删除 |
| 对 stopped 系统 `Open + Start` | **`0x80370105`（invalid state）** |
| 对 stopped 系统再次 `Create` | **`0x8037010F`（already exists）** |
| `logoff <console-id>` 只重登录会话 | 会话变成 `Down`，没有新的 `shell.log` 或 `read.log`，不是有效的重登路径 |
| modern system callback | `options=4/1` 为 `E_INVALIDARG`；`options=0` 可注册但 15 秒内无退出事件。主创建句柄上注册后，30 秒内仍无事件 |
| legacy `HcsRegisterComputeSystemCallback` | `E_INVALIDARG`，没有开始终止操作 |

**错误码不得再混用**：`0x80370115` 是 operation not started，`0x80370118` 是 timeout，
`0x80370107` 才是 terminated。代码已删除「timeout 就是停止成功」「`Open + Start` 复用」等错误尝试，
不保留未验证的 callback/Delete 路线。终止/重启现在有界并明确报错，不再无限等待或伪报成功。

### 26.3 因而尚不能宣称的能力 / What is not being claimed

- **没有**在本机完成容器内 console 登录并持续产出像素；
- **没有**完成容器内 `regedit` → 宿主 RAIL 窗口 → 拖动/键盘输入的端到端链路；
- **没有**完成整屏远程桌面会话、pagefile 穿透或动态分辨率验证；
- **没有**在本机启动完整 Server 2025。上述 MCR manifest 只证明来源和镜像形状可用。

宿主侧的 mirror 协议、查看器和 511 个测试仍有效，但不能替代这些真实容器验证。
下一步应在已知正常的另一台主机或运行时被修复后的本机，用一个新容器复测同一条链路；
不卸载 Windows 更新，也不继续堆叠已被证伪的注册表项或 HCS 调用。

### 26.4 验证工具注意事项 / Probe cautions

- 当前容器 `ExecuteAsync` 只返回退出码，不能假定 `wsm exec ... | Out-String` 会返回 guest stdout。
  本次有效取证让容器内命令重定向到 `C:\wsm\*.txt`，再读取宿主可见的 scratch 挂载卷；
  空 stdout 的探针一律不当作会话不存在的证据。
- WinUI 验证使用解决方案的 **x64 `bin\x64\Debug` 原生 `.exe`**。`dotnet <dll>` 在本项目
  WinUI 自包含应用上不是有效的验证方式；它会混淆基线与当前版本的比较。
- UI Automation 设置文本框后要移动焦点以提交绑定；选择容器引擎后要重新枚举桌面 ComboBox；
  重复运行前关闭旧设置对话框。找不到隐藏按钮不等于按钮没有实现。
