# Container Desktop Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 完成并实测现有 Windows 进程隔离容器引擎的逐窗口 GUI、可输入的整屏桌面，以及完整 Server 2022/2025 镜像管理入口。

**Architecture:** 沿用已确认的容器会话内捕获、宿主播放到交互式 console 的像素通道；逐窗口和整屏共享现有输入及共享内存协议，不改用已被实测拒绝的 RDP 监听或跨 silo 窗口注入。镜像通过现有 WCOW importer 和离线 hive customizer 准备，GUI 复用现有服务及引擎配置。

**Tech Stack:** .NET 10、C#、WinUI 3、WinForms 查看器、PowerShell 5.1 容器读侧、HCS/WCOW、xUnit。

**Spec:** `docs/investigation/13-container-desktop-and-rdp.md` §18、§19、§23.6、§25；用户明确要求保留 Windows 更新、所有改动提交且每次提交范围尽量小。

## Global Constraints

- 不卸载、回滚或暂停 Windows 更新，不破坏已恢复的 Windows Sandbox。
- 不引入新依赖；不另起像素/RDP 架构，不重做已经完成的调查。
- 复测必须使用真实进程隔离容器；单元测试通过不等于 GUI 链路通过。
- 容器/镜像的实验脚本和日志放在已被忽略的 `artifacts/`；产品改动全部提交。
- 每个提交只承担一个可独立审查的功能；提交前运行相关测试，最终全量构建测试。
- 提权进程将结果写入 `artifacts/` 日志，主进程轮询日志，不长等 UAC 子进程。

## Review Focus

- 会话若仍在约 121 秒被拆除，必须保留证据，不得宣称桌面已解锁；由真实容器复测覆盖。
- 读侧只有窗口表而没有像素时，不得把空白窗口当作成功；由镜像窗口像素与输入实测覆盖。
- 镜像下载取消、已安装目录和占用目录不能互相误删；由 `ContainerImageImportTests`、`ContainerImageScanTests` 覆盖。
- 凭据不得出现在日志、提交或 GUI 默认可编辑配置之外；沿用已有默认值，不记录密码。
- pagefile section 不可用时应回退文件映射，不能假装零复制；由共享内存测试及真实运行日志覆盖。

---

### Task 1: 保存已实现的三个产品功能

**Files:**
- Modify: `src/30-Connections/Wsm.Connection.ContainerDesktop/`、`src/50-App/Wsm.Cli/Desktop*ViewCommand.cs` 及现有测试。
- Modify: `src/10-Hcs/Wsm.Hcs.Storage/`、`src/20-Engines/Wsm.Engine.WindowsContainer/` 及现有测试。
- Modify: `src/30-Connections/Wsm.Service*/`、`src/40-Host/Wsm.Service.Host/`、`src/50-App/Wsm.Ui.WinUI/` 及现有测试。

**Interfaces:**
- Consumes: 已实现的 `IContainerDesktopProvider`、`IContainerImageImporter`、`IContainerImageCustomizer`。
- Produces: 可审查的像素通道、镜像/会话准备、GUI 集成三组独立提交；不改变公开契约。

- [ ] **Step 1: 验证继承的工作区。** 运行 `dotnet test WindowsSandboxManager.slnx --no-restore`，预期全部通过；运行 `git diff --check`，预期无空白错误。
- [ ] **Step 2: 审查并按功能暂存。** 对照 `git diff` 和 §25.1–25.6，不夹带主机修复、实验日志或无关重构。
- [ ] **Step 3: 分别提交像素通道、镜像管理/会话准备、GUI 集成。** 对无法在不破坏编译的情况下拆出的共享 CLI/契约文件，与直接使用它的功能同提交；最终 `git status --short` 中不得遗留这些改动。

### Task 2: 在修复后的主机上复现当前会话行为

**Files:**
- Test: `artifacts/post-reboot-retest.log`（本地证据，不提交）。
- Modify: `docs/investigation/13-container-desktop-and-rdp.md`（只记录实测结果）。

**Interfaces:**
- Consumes: `wsm create --engine container`、`wsm host`、`wsm exec`、`wsm stop` 和已打补丁的 Server Core 2022 镜像。
- Produces: 当前内核/可选功能状态下的 qwinsta、LSM、shell/read 日志和实际阻塞点。

- [ ] **Step 1: 启动提权复测。** 设置 `WSM_CONTAINER_DESKTOP=1`，显式选择已打会话补丁的镜像，新建唯一名称的容器，监控第二启动后的 console 会话。
- [ ] **Step 2: 判定并收集证据。** `console ... Active` 且持续超过旧墙、shell/read 日志存在才是登录成功；若在约 121 秒消失，收集 LSM #21/#22/#39/#40、进程表和读侧日志，不再混用旧 LCU 假说作为结论。
- [ ] **Step 3: 若登录成功，实测两种桌面。** 用读侧 `launch regedit.exe`，分别验证窗口像素、拖动/键盘输入和整屏像素/输入；记录共享内存是 pagefile 还是 file 回退。若不成功，只处理新证据定位的边界。
- [ ] **Step 4: 清理并提交调查结论。** 正常停止自己创建的容器，记录结果及仍未验证的项目，单独提交文档。

### Task 3: 修复复测证明的产品接线缺口

**Files:**
- Modify: `src/10-Hcs/Wsm.Hcs.Storage/Internal/ContainerImageCustomizer.cs`。
- Test: `tests/90-Tests/Wsm.Tests/Hcs/ContainerImageCustomizerTests.cs`。
- Modify: 复测确定需要改动的 GUI/provider/engine 文件及对应现有测试文件。

**Interfaces:**
- Consumes: `EnableDesktopSession(string layerPath, DesktopSessionOptions? options = null)` 与 `ContainerDesktopOptions`。
- Produces: 镜像准备结果与实际登录需求一致；GUI 可以显式准备镜像及启动/停止相应桌面通道，失败状态可见。

- [ ] **Step 1: 先写当前失败的回归测试。** 例如实测仍需要 `AutoLogonCount=99` 时，测试会话补丁命令包含 `REG_SZ` 的 `99` 且不会保留 `0`；先在未修复代码上确认失败。
- [ ] **Step 2: 实施最小单点修复。** 不添加已被 §25.8–25.10 证伪的注册表组合；不改 Windows 更新。
- [ ] **Step 3: 重跑相应测试和真实容器复测。** 保留前后结果，失败则回到根因调查，不堆叠补丁。
- [ ] **Step 4: 分功能提交修复及测试。** 每个修复独立提交，文档说明与代码保持一致。

### Task 4: 验证完整 Server 镜像目录和最终交付

**Files:**
- Modify: `src/10-Hcs/Wsm.Hcs.Storage/Internal/ContainerImageImporter.cs`（仅在实测发现目录/导入缺陷时）。
- Test: `tests/90-Tests/Wsm.Tests/Hcs/ContainerImageImportTests.cs`。
- Modify: `README.md`、`docs/investigation/13-container-desktop-and-rdp.md`。

**Interfaces:**
- Consumes: MCR `windows/server:ltsc2022`、`windows/server:ltsc2025` 与 `ContainerImagePaths.GetDefaultLayerPath`。
- Produces: 两种完整镜像的目录/manifest 验证，默认路径安装，至少一种完整 Server 镜像的真实 create/start/exec/stop 验证；另一种若未做启动验证必须明确标注。

- [ ] **Step 1: 验证 MCR manifest 和 importer 的选择。** 确认 windows/amd64、基础层和默认安装目录；失败先写回归测试再修复。
- [ ] **Step 2: 验证 GUI 的实际控件和命令。** 运行 WinUI x64 应用，检查 Windows Container 引擎/镜像选择、Server 2022/2025 full 项、进度/取消/已安装状态、桌面入口；不可用状态必须解释原因。
- [ ] **Step 3: 真实启动完整 Server 容器。** 执行 create/start/exec/stop；GUI 可用时验证查看器由用户 console 会话启动且能操作。
- [ ] **Step 4: 最终验证和提交。** 运行 `dotnet build WindowsSandboxManager.slnx`、`dotnet test WindowsSandboxManager.slnx`、`git diff --check`，更新实测状态，检查 `git status --short` 为空并汇报提交哈希、已验证项和限制。
