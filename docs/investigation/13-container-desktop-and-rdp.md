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
| 6 | (本步 / this step) | README 章节 + 文档回填 / README section + doc backfill | 记录 record |
| 7 | (本步 / this step) | §10 桌面可达性实测（借 token / 自动登录 / ~121 秒注销 / pipe 与 3389 / 可截屏的容器桌面）+ `tools/container-desktop-agent` | 记录 + 可用 |
| 8 | (本步 / this step) | §11 RDP 的**代码级根因**（反汇编 `termsrv`）+ §12 **会话内输入代理**（`deskinput.ps1`，含验证）+ 镜像导入器改为报出 `tar` 的诊断 | 记录 + 可用 |

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