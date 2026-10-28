# Direct HCS VM RDP and RAIL Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the direct HCS VM engine boot from the shared Windows Sandbox base, persist its writable state beside its manifest, and provide working full-desktop RDP and WDAG-style RAIL windows.

**Architecture:** Keep `Wsm.Engine.Hcs`, the existing differencing-VHD provisioner, enhanced-session/HvSocket configuration, and the RDP/RAIL clients merged from `main`. Replace only the unsupported inheritance of another compute system's guest state with a fresh per-VM guest state. Resolve VM storage from the manifest recipe for both start and cleanup; do not copy the platform base or change process-isolation code.

**Tech Stack:** .NET/C#, Host Compute System API, Hyper-V differencing VHDX, HvSocket/enhanced-session RDP, mstscax RemoteApp, xUnit.

**Spec:** User requirements in the current conversation and evidence in `docs/investigation/13-container-desktop-and-rdp.md` sections 29–30; prior RDP/RAIL evidence in `docs/investigation/08-connection-rdp-hvsi.md`.

## Global Constraints

- Only direct HCS VM code is in scope. Do not modify `Wsm.Engine.WindowsContainer` or process-isolation desktop code.
- Stop all HCS experiments immediately if a new `0x80370106` occurs, record the operation and possible cause, and wait for manual recovery.
- Do not use Windows Sandbox, UDK, ManagedWindowsVM, or gRPC as the product engine or acceptance substitute.
- Do not run DISM, feature resets, service restarts, Windows Update operations, registry deletion, or deletion of platform base/template/prepared storage.
- Reuse the installed Windows Sandbox prepared base in place. Never copy the full base image.
- Do not commit or merge a product implementation that fails real acceptance; revert unverified product experiments.
- VM existence or a successful `Start` return alone is not acceptance. Require at least 140 seconds Running, guest exec, real RDP pixels/input, a movable RAIL `regedit` window, persistence across restart, and clean stop/restart.

## Review Focus

- A missing platform parent must not silently delete a user's differencing disk; test this in Task 2.
- An existing writable VM disk must retain its own guest state rather than being reset on every start; test this in Tasks 1 and 2.
- Manifest deletion must not delete files outside the manifest-owned VM storage directory; test path selection in Task 2.
- RAIL must prove the requested guest process exists and its window accepts host input; a connected RAIL channel alone is insufficient (Task 3).
- Base-layer replacement must fail clearly without destroying prior user data; test this in Task 2 and record any real-platform limitation in Task 3.

---

### Task 1: Fresh guest state for direct HCS VM cold start

**Files:**
- Modify: `src/10-Hcs/Wsm.Hcs.Storage/Internal/SandboxStorageProvisioner.cs`
- Test: `tests/90-Tests/Wsm.Tests/Hcs/StorageProvisioningTests.cs`

**Interfaces:**
- Consumes: existing `IHcsClient.CreateEmptyGuestStateFile`, prepared-base discovery, and `SandboxVmJsonBuilder.GuestState.GuestStateFilePath`.
- Produces: a newly provisioned VM whose `sandbox.vmgs` is created empty rather than copied from the platform prepared base; existing VM state remains reusable.

- [ ] **Step 1: Write failing provisioning tests.** Assert that a new differencing disk calls `CreateEmptyGuestStateFile` and never copies donor `sandbox.vmgs`; assert that an existing disk and guest state do not recreate either file.
- [ ] **Step 2: Run the focused tests and confirm the new cold-start assertion fails.** Use the repository's normal `dotnet test` filter for `StorageProvisioningTests`.
- [ ] **Step 3: Implement the minimal guest-state change.** For a new VM, create an empty guest-state file. Keep the platform base exclusively as the VHD parent and read-only OS layer; preserve existing guest state on restart. Do not add `RestoreState`, VMRS, template IDs, or new JSON fields.
- [ ] **Step 4: Run focused HCS storage and JSON tests.** Expect all selected tests to pass and the emitted ordinary-start JSON to retain the current `GuestConnection` contract without `RestoreState`.
- [ ] **Step 5: Perform one direct-HCS start using the current code plus this single change.** Preserve logs before running scripts in `artifacts/hcs-vm-fix-20261028/`. Require HCS Running and `vmwp` for at least 140 seconds and a successful guest command. On `0x80370106`, stop immediately. On another error, diagnose that result before any further change.
- [ ] **Step 6: Commit only if the runtime gate passes.** Commit the focused code/tests and evidence with `fix(hcs): cold start with per-vm guest state`; otherwise revert the product/test experiment and record evidence without claiming success.

### Task 2: Manifest-local persistent VM storage

**Files:**
- Modify: `src/20-Engines/Wsm.Engine.Hcs/HcsSandboxEngine.cs`
- Modify: `src/00-Core/Wsm.Application/Internal/SandboxManager.cs` only if needed to pass the already-resolved recipe storage path into cleanup
- Modify: `src/00-Core/Wsm.Engines.Abstractions/ISandboxEngine.cs` only if needed for that cleanup contract
- Modify: `src/10-Hcs/Wsm.Hcs.Storage/Internal/SandboxStorageProvisioner.cs`
- Test: `tests/90-Tests/Wsm.Tests/Hcs/HcsSandboxEngineTests.cs`
- Test: `tests/90-Tests/Wsm.Tests/Hcs/StorageProvisioningTests.cs`
- Test: existing persistence tests if the cleanup interface changes

**Interfaces:**
- Consumes: `SandboxRecipe.Storage.StoragePath` resolved by `FileSandboxRepository.FromPortable` and the Task 1 provisioner.
- Produces: start and delete operations that select the same manifest-owned storage directory; an unreadable/orphaned old differencing disk is preserved rather than silently deleted.

- [ ] **Step 1: Write failing tests.** Cover custom manifest storage used by both start and cleanup, restart reusing the same `sandbox.vhdx`/`sandbox.vmgs`, and a missing parent leaving the old user disk untouched with a clear error.
- [ ] **Step 2: Run focused tests and confirm the new assertions fail.**
- [ ] **Step 3: Implement one shared storage-path resolution rule.** Keep `DataPath="."` compatibility so files remain beside the `.wsm`; do not overwrite the manifest and do not relocate existing user data automatically. Bound cleanup to the selected VM storage directory. Replace silent deletion of an orphaned user disk with preservation and an actionable error.
- [ ] **Step 4: Run HCS engine, storage, application, and persistence tests.** Expect all selected tests to pass.
- [ ] **Step 5: Verify persistence on the Task 1 VM.** Write a unique guest marker, stop, start the same manifest, read the marker back, and verify that the VHDX and guest-state files are in the manifest directory and the parent remains the platform prepared base.
- [ ] **Step 6: Commit only after the real persistence check passes.** Use `fix(hcs): persist vm state beside manifest`; otherwise keep the evidence and do not claim persistence is fixed.

### Task 3: Full-desktop RDP and WDAG-style RAIL acceptance

**Files:**
- Modify only with new root-cause evidence: direct HCS VM guest provisioning or existing `Wsm.Connection.RailClient` / `Wsm.Connection.Session` files
- Test: corresponding existing HCS/application/connection test files
- Document: `docs/investigation/13-container-desktop-and-rdp.md`

**Interfaces:**
- Consumes: the running VM and guest-exec path from Task 1, manifest persistence from Task 2, and the selectable RDP/mstscax RAIL clients merged from `main`.
- Produces: reproducible full-desktop and RemoteApp launches from the existing HCS VM UI/CLI path.

- [ ] **Step 1: Add a failing regression test only for a reproduced defect.** Do not preemptively rewrite the already-connected RDP/RAIL clients.
- [ ] **Step 2: Launch full-desktop RDP through the product HCS VM path.** Verify a separate host window shows real guest desktop pixels, accepts keyboard/pointer input, remains connected beyond 140 seconds, and survives disconnect/reconnect as designed.
- [ ] **Step 3: Launch `regedit.exe` through the product RAIL path.** Verify the guest process starts, a native host-sized application window appears without a full desktop, and it can be moved, receive input, and close cleanly. Prefer the transport already proven for RAIL in existing evidence; diagnose rather than hiding failures.
- [ ] **Step 4: Stop and restart the same VM.** Recheck guest exec and the persisted marker, then ensure no test VM or client process is left running.
- [ ] **Step 5: Run the standard full build and complete test suite.** Investigate any failure, including the previously intermittent `WindowMirrorHealthTests.Attach_adopts_an_advancing_live_writer`; do not use unrelated process-isolation changes to make it pass.
- [ ] **Step 6: Record exact evidence and limitations, request independent review, then commit and merge `main`.** Merge only after all requested real acceptance checks pass. If a gate remains blocked, leave `main` unchanged and report it plainly.
