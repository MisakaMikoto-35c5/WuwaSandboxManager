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
