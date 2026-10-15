# container-desktop-agent

Tooling for reaching and *seeing* a desktop inside a **process-isolated** (HCS
Argon) Windows container. It exists because none of the normal channels work
there — see
[`docs/investigation/13-container-desktop-and-rdp.md`](../../docs/investigation/13-container-desktop-and-rdp.md)
for the measurements.

> **Status: investigation tooling.** These scripts are how the findings were
> produced. They are small, dependency-free PowerShell (Server Core has no
> PowerShell ISE but does have 5.1 + the in-box `csc.exe`), and run *inside* the
> container.

## Why each piece is needed

| Problem | What the container does | Consequence |
|---|---|---|
| `winlogon`/`LogonUI` never start | The image ships `Session Manager\NumberOfInitialSessions = 1`, so `smss` creates only the services session | No console session, no desktop. Fixed by `wsm image desktop` (sets it to `2`) |
| RDP is unusable | `termsrv` refuses to create an RDP listener in a container (`WTSIsServerContainer`) | Cannot view the session over 3389 (or any RDP transport) |
| You cannot create a process in the console session | `CreateProcessAsUser` into another session returns `ACCESS_DENIED` inside the container silo (measured with a SYSTEM@session-5 token and `SeAssignPrimaryToken`/`SeTcb` enabled); `CreateProcessWithTokenW` only ever creates in the *caller's* session | Borrowing winlogon's token gets you a process in **your own** session, not the console one |
| Interactive logon does not complete | The image has `AutoAdminLogon=1` + `DefaultUserName=Administrator` but no `DefaultPassword`; with one set, winlogon *does* arbitrate a logon for that user in the console session, but `LogonUI` stays up and the session is torn down ~121 s after boot | The console session is short-lived and never runs a shell |

## Scripts

| File | Runs | What it does |
|---|---|---|
| `session-run.ps1` | in the container (services session) | Borrows the token of a process that lives in another session (winlogon/LogonUI/csrss) and starts a command line there on a chosen desktop (`winsta0\default`, `winsta0\Winlogon`). Tries impersonation-token `CreateProcessAsUser` first, then the `CreateProcessWithTokenW` fallbacks, and reports which one worked *and which session the child actually landed in*. Caches the compiled helper in `SessionLauncher.dll` (the in-box compiler takes 20-40 s cold). Also accepts `-ArgumentFile` (`key=value`) so a scheduled task can drive it quote-free. |
| `session-run-as-system.ps1` | in the container | Runs `session-run.ps1` **as SYSTEM** through a one-shot scheduled task (`schtasks /ru SYSTEM`), because a container administrator's token lacks `SeAssignPrimaryTokenPrivilege`/`SeTcbPrivilege`. (Still hits the silo's cross-session denial, but it is the correct shape for a host that allows it.) |
| `deskcap.ps1` | in the container, on the target desktop | Captures the desktop with `Graphics.CopyFromScreen` and either serves PNGs over TCP (`[4-byte length][PNG]` per connection) or writes one PNG with `-Once`. Reports the window station, desktop, input desktop and screen size in its log/banner. |
| `deskinput.ps1` | in the container, on the target desktop | **Input injection** (the other half of a usable desktop). Actions: `info` (window station / desktop / input desktop / cursor / foreground window), `move`, `click`, `keys` (`F3`, `CTRL+S`, `ALT+F4`), `text`, `serve -Port` (a line protocol), and `script -TextFile` (one command per line - use this to avoid nested quoting). Verified: injected `F3` turned the Registry Editor's foreground window into its `Find` dialog. |
| `agent.cmd` | as the console session's shell (`Winlogon\Shell`) | Writes a proof file, starts `regedt32.exe` + a console, and starts `deskcap.ps1`. Must keep running (winlogon logs the session off if the shell exits). |
| `agent.ps1` | as the console session's shell (alternative) | Same, but as a single PowerShell process. |

## Findings that shape the code

* **Only the *active* desktop can be captured.** `CopyFromScreen` returns
  *"The handle is invalid"* on a desktop that is not the session's input desktop,
  so draw your GUI app on `winsta0\Winlogon` (the input desktop in a container
  session) and capture that — a screenshot of the inactive `Default` desktop
  comes out empty.
* **`Winlogon\Shell` must be a real executable command line.** Pointing it at a
  `.cmd` makes winlogon fail to start the shell and log the session straight off;
  use `cmd.exe /k C:\wsm\agent.cmd` or a PowerShell command line.
* **Container registry writes are container-local.** They persist across
  restarts of the *same* scratch layer, but they are not the image's
  `Files\...\config\*` hives (verified: the image's SAM hash/date does not
  change).
* Screenshot of a container desktop (Registry Editor drawn on the session's
  active desktop, captured with `deskcap.ps1 -Once`): see the investigation doc.
* **Input reaches the desktop too.** With the agent running on the session's
  *active* desktop, `deskinput.ps1 -Action keys -Keys F3` changed the foreground
  window from `Registry Editor` to its `Find` dialog
  (`foreground=0x6006C pid=33136 'Find'`), and the dialog is visible in the next
  `deskcap.ps1` frame.
* **Scripts must be PowerShell 5.1.** Server Core has 5.1 only; PS7-only syntax
  (e.g. `$b = if (…) {…} else {…}`) is a *parse* error and a hidden
  `powershell -File` child then exits **silently** - no output, no log. Validate
  with `[Parser]::ParseFile` inside the container before blaming the transport.
* `Add-Type -Path` locks the assembly for the process lifetime, so a second
  process cannot rebuild the same `Ui.dll`; fall back to an in-memory compile.

## host/

The host-side harness that drove the measurements above (elevated runner that
owns a held container and services a command queue, the container push/pull
helpers, the token/privilege/logon probes, and the RDP knob scripts). `50`
uncurated one-off scripts, kept for reproducibility like
`tools/container-probe/scripts`; they contain machine-specific paths
(`C:\wsm-images\servercore-ltsc2022`, `C:\wsmprobe\...`), a throwaway lab
password, and hardcoded compute-system ids. **Not** polished tooling.

Key ones: `host.ps1` (elevated runner: start container, `@copy`/`@wait`/`@stop`
queue), `q.ps1` (enqueue + print new output), `push.ps1` / `pull.ps1` (move files
in / out of the container: gzip+base64 in, base64 out), `ps.ps1` (run a local
`.ps1` inside the container via `-EncodedCommand`), `crossprobe.ps1` (the
cross-session `CreateProcessAsUser` matrix), `logontest.ps1` / `systest*.ps1`
(SYSTEM scheduled task + `LogonUser` probes), `rdpfix.ps1` (the RDP knob sweep).
