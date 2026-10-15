# container-probe — HCS / WCOW investigation lab

> **状态 / Status: 归档，非受支持工具。** 这里是调查 Windows Sandbox / Windows 容器
> 内部机制时用的探针与一次性的 PowerShell 脚本，保留下来是为了**可复现**，不是产品代码。
>
> **Archival, unsupported.** These are the throwaway probes and scripts used while
> investigating Windows Sandbox / Windows container internals. They are kept for
> reproducibility, not as product code.

---

## HcsProbe

一个直接 P/Invoke `computecore.dll`（HCS）、`vmcompute.dll`（WCOW 层 API）、
`vmcompute!HNSCall`（HNS）的小命令行探针，用来绕开上层封装做原始实验。

```powershell
cd tools/container-probe/HcsProbe
dotnet build -c Release
# 需要管理员（HCS/HNS 都要求提权）
$probe = "bin/Release/net10.0-windows/hcsprobe.exe"
```

主要动词 / Verbs:

| Verb | 作用 What |
|---|---|
| `list` / `props <id>` / `stop <id>` | 枚举 / 查询 / 终止 compute system |
| `create <id> <json>` / `start <id>` | 用原始 HCS JSON 建并启动 |
| `sh <id> <cmd>` / `shf <id> <file>` / `exec <id> <cmd>` | 容器内执行并回读 stdout/stderr/exit code（`shf` 从文件读命令行，避免引号地狱） |
| `argon <id> <scratch> <parents...>` | WCOW 层：`CreateSandboxLayer`→`Activate`→`Prepare`→HCS create/start |
| `argonnet3 <id> <scratch> <image> <natId>` | 同上 + HNS **namespace/endpoint**（本文档的网络场景；容器句柄保持打开） |
| `lname` / `lmount` / `lactivate` / `ldeactivate` / `lprepare` / `lunprepare` / `lsandbox` / `lprocessbase` / `limport` / `lcreate` / `ldestroy` / `lexists` | 逐个暴露 WCOW 层 API（调试 `CreateSandboxLayer`/`PrepareLayer` 语义用） |
| `hns <VERB> <path> <body>` / `hnsnat` | 直接调 HNS REST（建 NAT 网络等） |
| `rprobe <host> <port>` | 最小 RDP 握手探针（X.224 + RDP Negotiation Request） |
| `deep` / `variants` | 批量试 HCS schema 变体（层 id/路径组合） |

**环境变量 / Env**: `WSMPROBE_HOLD=<秒>` 让容器在探针进程里保持存活（容器的
`ShouldTerminateOnLastHandleClosed=true`，**持有句柄的进程一退出容器即销毁**）。

---

## scripts/

一次性的调查脚本，**未经整理**：里面的 compute-system id（`aaaa…0d0x`）、
路径（`C:\wsm-images\…`、`C:\wsmprobe\…`）、以及个别**实验用**口令都是本机专用的，
不能直接照搬到别的机器。`scripts/rdp1-pw.cmd` 里有一个一次性容器口令。

**Uncurated**: hardcoded compute-system ids, machine-specific paths and a throwaway lab
container password. Do not reuse as-is.

按用途分组 / Groups:

| 前缀 Prefix | 主题 Topic |
|---|---|
| `p1`–`p5`, `payload`, `hive`, `hive2`, `edi`, `evt`, `status` | 容器内**只读**体检：进程/会话/登录会话、WMI 显示设备、窗口站与 `GetDeviceCaps`、镜像 hive 离线读取 |
| `dsk*`, `round2`–`round4`, `rdp3`–`rdp7` | **容器桌面 / RDP** 主线：起 console 会话、`qwinsta`/`tasklist` 观察、宿主→容器入站对照实验、TermService 与监听器排查 |
| `exp`, `knob*`, `diag`, `final`, `clean2` | 对着镜像 hive 逐个试「容器内允许监听器」开关（`ContainerMode`、`AllowListenersInsideArgonContainer`、`EditionID`、环境变量…）以及离线改 hive 的加锁/解锁流程 |
| `imgs`, `download`, `dl-server`, `image-full` | 从 MCR 解析 manifest 尺寸、下载基础层 / Desktop-Experience（`windows/server`）层 |
| `sdbx*`, `validate*`, `ws22*`, `prov`, `refresh`, `restore`, `spoof`, `wcifs`, `trace*`, `n2`–`n7`, `e1`/`e2` | **更早会话**的工作：沙箱实例、镜像层（wcifs）、SKU/构建号伪装与还原、服务化 manager 等 |

> 结论与证据见 [`docs/investigation/13-container-desktop-and-rdp.md`](../../docs/investigation/13-container-desktop-and-rdp.md)。
