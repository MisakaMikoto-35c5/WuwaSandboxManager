
### 13.6 「提权宿主能否跨 silo 拿句柄 / 注入窗口」实测 / Handle pass-through and window injection, tested

两个命题分开测：宿主侧 `host\elevtest.ps1`（**提权**运行，先 `SeDebugPrivilege`，再 `OpenProcess`）；
容器侧 `host\shmprobe.ps1` 建三个节并把 `pid`+句柄写进 `shm-handles.txt`。

**A. 句柄直通：成立 ✔**

```
container published: pid=94424 | handle_frame=2304 | frame_capacity=3145792
Get-Process -Id 94424: name=powershell session=4          ← PID 空间共享
OpenProcess(PROCESS_DUP_HANDLE) -> 0x91C                  ← 提权宿主可以打开容器进程
DuplicateHandle(frame) ok=True dup=0x968
MAP OK (3145792 bytes): head='WSM-FRAME-OK' tail='WSM-FRAME-OK'
```

**提权宿主 `OpenProcess` + `DuplicateHandle` + `MapViewOfFile` 能把容器里一个 3 MB 节完整映射出来**
（首尾标记都读到）。非提权宿主在同一处是 `err=5`（§13.1）⇒ 只要**宿主侧组件提权**即可（WSM 本来就提权运行）。

⇒ 设计升级：容器改用 `CreateFileMappingW(INVALID_HANDLE_VALUE, …)`（**页文件支持的节，纯内存、不落盘**），
把 `pid`+`handle` 写进一个几字节的共享文件；宿主提权后复制句柄并映射 → **去掉 NTFS 脏页写回**
（现在 3 MB/帧 × 5 fps ≈ 15 MB/s 打到页缓存）。输入/控制则可走 HCS `MappedPipes`（官方跨 silo 通道）。

**B. 窗口注入：不成立 ✘**

```
FindWindow(null, 'Registry Editor') = 0x0        ← 容器里明明有这个窗口
host input desktop handle=0x954 name='Default'   ← 宿主自己的输入桌面（容器是 Winlogon）
EnumWindows on this desktop: [Visual Studio] [TabTip] ...   ← 只有宿主自己的窗口
```

窗口是**会话/桌面作用域**的对象，silo 有**自己的 `WinSta0`/桌面**：宿主的 `FindWindow`/`EnumWindows`
只看得到**自己桌面**上的窗口，容器窗口一个都看不到；跨桌面/跨会话的 `SetParent`、`SetThreadDesktop`
同样不通。⇒ **「把窗口注入用户控制台」在进程隔离里做不到**，能过去的只有**像素/内容**（§13.2 的方案）；
想要真正的窗口只能走 **Hyper-V 隔离 + 增强会话**。

**结论（定下的产品形态）**：画面 = `pid`+`handle` 直通的**页文件节**（提权宿主 → 纯内存、零落盘）；
输入/控制 = HCS `MappedPipes` 或「每命令一个文件」。
