# Windows Sandbox 内部机制调查 / Windows Sandbox Internals Investigation

> 本目录是一套**中英双语、按模块拆分**的调查文档，记录 Windows Sandbox、其系统镜像、
> WDAG、HCS、容器层及其相关内容的内部原理与实证证据。
>
> This directory is a set of **bilingual (Chinese / English), module-by-module** investigation
> documents covering Windows Sandbox, its system image, WDAG, HCS, container layers and
> related internals, with empirical evidence.

---

## 阅读约定 / Reading conventions

- 每个文件是一个独立模块 / Each file is one standalone module.
- 章节标题为 `中文 / English` / Section headings are `中文 / English`.
- 正文按 `**中文**` 与 `**English**` 分段给出 / Body text is split into `**中文**` and `**English**` blocks.
- 表格为三列：`Item | 中文 | English` / Tables use three columns: `Item | 中文 | English`.
- 所有结论均来自本机（Windows 11 Build 26200）实测、二进制字符串与 IL 反编译；
  证据在每篇末尾的「证据 / Evidence」列出 / All conclusions come from on-machine
  (Windows 11 Build 26200) tests, binary strings and IL decompilation; evidence is listed at the
  end of each document.

---

## 模块索引 / Module index

| # | 文件 File | 主题 Topic |
|---|---|---|
| 01 | [01-windows-sandbox.md](01-windows-sandbox.md) | Windows Sandbox 总览：架构、组件、控制面、配置、生命周期 / Overview: architecture, components, control planes, configuration, lifecycle |
| 02 | [02-sandbox-image.md](02-sandbox-image.md) | Sandbox 系统镜像：层布局、Files/Hives/Specialization、HCS 消费方式 / Sandbox system image: layer layout, Files/Hives/Specialization, how HCS consumes it |
| 03 | [03-wdag.md](03-wdag.md) | Windows Defender Application Guard（Madrid）：架构、组件、与 Sandbox 的关系 / WDAG (Madrid): architecture, components, relation to Sandbox |
| 04 | [04-hcs.md](04-hcs.md) | Host Compute Service：组件、生命周期、操作模型、提权 / HCS: components, lifecycle, operation model, elevation |
| 05 | [05-container-layers.md](05-container-layers.md) | 容器层与 wcifs：分层、reparse、注册表并集 / Container layers and wcifs: layering, reparse points, registry union |
| 06 | [06-cmservice-and-cms-api.md](06-cmservice-and-cms-api.md) | CmService 与 `Cms*` 客户端 API：存储/层/容器管理 / CmService and the `Cms*` client API |
| 07 | [07-storage-and-checkpoints.md](07-storage-and-checkpoints.md) | 存储、检查点、合子、持久化 / Storage, checkpoints, zygote, persistence |
| 08 | [08-connection-rdp-hvsi.md](08-connection-rdp-hvsi.md) | 连接：RDP over HvSI/NamedPipe、RDPBASE、RAIL/RemoteApp / Connection: RDP over HvSI/NamedPipe, RDPBASE, RAIL/RemoteApp |
| 09 | [09-networking-and-vsmb.md](09-networking-and-vsmb.md) | 网络（HNS）与 vSMB/HvSocket / Networking (HNS) and vSMB/HvSocket |
| 10 | [10-hcs-schema-and-native-api.md](10-hcs-schema-and-native-api.md) | HCS JSON Schema 与原生 API 参考 / HCS JSON schema and native API reference |
| 11 | [11-project-mapping.md](11-project-mapping.md) | 本项目（WindowsSandboxManager）映射与能力矩阵 / How this project maps to the above |
| 12 | [12-modular-architecture.md](12-modular-architecture.md) | ABP 模块化架构：程序集分层、约定规约、多引擎抽象、本地化 / ABP modular architecture |
| 13 | [13-container-desktop-and-rdp.md](13-container-desktop-and-rdp.md) | 进程隔离容器里的桌面/会话与 RDP：`NumberOfInitialSessions` 让 console 会话起来、`termsrv` 容器门禁让 RDP 无法建立 / Container desktop, sessions and RDP in a process-isolated container |
| 14 | [14-hcs-0x80370106.md](14-hcs-0x80370106.md) | HCS `0x80370106`：强制停止规则、触发记录、证据边界与人工恢复 / HCS hard-stop rules, incident evidence and manual recovery |

> 早前的单文件汇总版仍保留在 [`../investigation.md`](../investigation.md)。
>
> The earlier single-file summary is kept at [`../investigation.md`](../investigation.md).

---

## 一句话总览 / One-paragraph summary

**中文**：Windows Sandbox 是一个 **Hyper-V 容器（DisposableVM）**，由 `CmService` 经 **HCS**
（`vmcompute`）创建；它的「系统镜像」是一个 **wcifs 容器层**（`Layers\<id>\Files` + `Hives` +
`Specialization`），通过 **vSMB** 只读提供给 guest，并与可写的 `sandbox.vhdx` 做文件系统/注册表并集；
连接是 **RDP over HvSI/NamedPipe**；它与已废弃的 **WDAG（代号 Madrid）** 同源。

**English**: Windows Sandbox is a **Hyper-V container (DisposableVM)** created by `CmService`
through **HCS** (`vmcompute`). Its "system image" is a **wcifs container layer**
(`Layers\<id>\Files` + `Hives` + `Specialization`) served read-only to the guest over **vSMB** and
unioned with the writable `sandbox.vhdx`; the session is **RDP over HvSI/NamedPipe**; it shares its
lineage with the deprecated **WDAG (codename Madrid)**.
