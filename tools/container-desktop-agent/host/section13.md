
---

## 13. 用共享内存把容器画面呈现在宿主上 / Presenting the container desktop via shared memory

> 目标：不用 RDP（§11 已证明不可能），把**容器桌面画面**呈现在宿主上，并且用**内存共享**而不是 TCP。
> 做法：先测前提，再按测出的前提设计。

### 13.1 先测前提 / Measure the prerequisites first

| 探测 Probe | 容器内 Container | 宿主 Host |
|---|---|---|
| `DwmIsCompositionEnabled` | **False**（镜像里也没有 `dwm.exe`） | True |
| `D3D11CreateDevice(HARDWARE)` | **`0x887A0004` DXGI_ERROR_UNSUPPORTED** | `0x0` 成功 |
| `D3D11CreateDevice(WARP)` | **`0x887A0004`**（连软件光栅都没有） | — |
| `EnumDisplayDevices` 枚举到的适配器 | **0 个** | 有 |
| `CreateFileMappingW`（命名/匿名） | **成功** | 成功 |
| 容器建节后宿主按名 `OpenFileMapping` | — | **err=2 FILE_NOT_FOUND** ⇒ **命名对象不跨 silo** |
| 宿主能否看到容器 PID | 容器 `pid=98836` | **`Get-Process -Id 98836` → `powershell session=4`** ⇒ PID 空间共享 |
| 宿主 `OpenProcess(PROCESS_DUP_HANDLE, 容器pid)` | — | **err=5**（宿主未提权，`SeDebugPrivilege` err=1300）⇒ 句柄复制**需要提权宿主** |
| 宿主读容器 `C:\` | — | `\\?\Volume{guid}\...` **可读**；**写需要提权** |

**结论：** ① 容器没有 DWM、没有任何 D3D 设备 → 没有 `ID3D11Texture2D`/`CreateSharedHandle`，
`DwmGetDxSharedSurface` 也无处可问，**只能共享像素**；② 不能按名字会合（各 silo 有各自的
`BaseNamedObjects`）→ 只能按**文件/句柄**会合；③ 容器的 `C:\` **就是宿主可映射的一个卷**
（`\\?\Volume{guid}\`，无盘符）→ **一个普通文件就是共享内存**。

### 13.2 设计 / Design

```
容器 (Server Silo)                                    宿主 (Root Silo)
  deskshare.ps1                                         deskread.ps1 / deskview.ps1
  +- CreateFileW/CreateFileMappingW/MapViewOfFile <---  CreateFileW/.../MapViewOfFile
  |   C:\wsm\frame.shm          同一个文件         \\?\Volume{guid}\wsm\frame.shm
  |   [64B 头][1024x768xBGRX]                        读 seq/busy → 快照成 Bitmap
  +- 轮询 inbox-<ts>.txt ---- 每命令一个新文件 ----<  deskpush.ps1 / deskview 的鼠标键盘
      → deskinput.ps1 注入                             （写进镜像层，宿主无需提权）
```

帧文件头（小端）：`0 magic 'FSM1'` / `4 version` / `8 width` / `12 height` / `16 stride` / `20 format` /
`24 seq(int64)` / `32 busy(int32)` / `36 writerPid` / `40 fps` / `48 totalFrames(int64)` / `64…` 像素。

输入方向**不共用同一个文件**：宿主对裸卷没有写权限（除非提权），但可以往**镜像层**写普通文件，
容器的合并视图立刻能看到；而容器读过某个层文件后其文件系统会**持有句柄**，覆盖写会
`used by another process` → 所以**每条命令一个新文件** `inbox-<时间戳>.txt`。

### 13.3 实测结果 / Measured result

```
host: header: magic=0x314D5346 1024x768 stride=4096 busy=0 writerPid=10732 fps=5 frames=95
host: seq: first=95 then=96 busy=0 moved=True
host: saved snapshot: shared-b.png (35189 bytes)
```

宿主从共享内存读出的帧是一张**真实的 1024×768 桌面**：`Registry Editor` + 它的 `Find` 对话框
（`Find what:`、`Look at` 的 `Keys/Values/Data`、`Find Next`/`Cancel`）+ `Untitled - Notepad`，
颜色与方向正常、无镜像/撕裂（见 `docs/investigation/assets/container-desktop-shared-memory.png`）。
大块黑色是**没有 wallpaper/shell**（镜像无 explorer），不是渲染缺陷。

输入方向在同一套机制上也通：注入 `F3` 后同一进程内前台窗口由 `Registry Editor` 变为 `Find`（§12.2）。

### 13.4 踩到的坑 / Gotchas

| 坑 | 现象与解法 |
|---|---|
| PS 5.1 把 `0xC0000000` 当**负 Int32** | `CreateFileW` 的 `access` 报 “cannot convert to UInt32” → 用 `[uint32]3221225472` 这类十进制 |
| `Bitmap(w,h,stride,fmt,scan0)` 直接映射指针 | `CopyFromScreen` **静默失效**，整帧全黑 → 先抓进普通 Bitmap，再 `RtlMoveMemory` 拷进共享内存 |
| **隐藏/最小化**的控制台 | TSDDD 虚拟显示给的是**全黑**帧（`GetForegroundWindow` 仍看得到真窗口）→ 抓屏代理必须**可见**启动 |
| `MemoryMappedFile.CreateFromFile` 的重载 | PS 会挑错重载（把 FileStream 当 path）、`$null` 名报 “Map name cannot be an empty string”、共享模式受限 → **直接用 Win32** |
| 层文件被容器读过即锁 | 覆盖写报 `used by another process` → 每条命令一个新文件，两侧都用 `FileShare.ReadWrite` |
| `OpenProcess(DUP_HANDLE)` 被拒 | 非提权宿主 `err=5`、`SeDebugPrivilege err=1300` → 「句柄直通」变体必须**提权**（未复测） |

### 13.5 现状与后续 / Status and next steps

- **可用**：`deskshare.ps1`（容器：抓屏→共享内存 + 轮询输入）、`host\deskread.ps1`（宿主：读帧→PNG/校验）、
  `host\deskpush.ps1`（宿主：发输入）、`host\deskview.ps1`（宿主窗口：贴帧 + 转发鼠标/键盘；`-Capture` 已验证）。
- **待做**：交互窗口的长跑实测；脏矩形 + 压缩（3 MB/帧 × 5 fps ≈ 15 MB/s 写页缓存）；
  若走「句柄直通」变体：容器建页文件节 → 把 `pid`+`handle` 写进共享文件 → 宿主（**提权** + `SeDebugPrivilege`）
  `OpenProcess` + `DuplicateHandle` + `MapViewOfFile`（§13.1 已确认 PID 共享、命名对象不共享）。
