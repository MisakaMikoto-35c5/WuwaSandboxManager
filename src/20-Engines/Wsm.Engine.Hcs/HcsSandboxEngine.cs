using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;

namespace Wsm.Engine.Hcs;

/// <summary>
/// Sandbox engine driven directly by the Host Compute Service
/// (<c>computecore.dll</c>). Unlike the gRPC control plane, this engine:
/// <list type="bullet">
///   <item>can run <b>multiple instances concurrently</b> (no licensing gate);</item>
///   <item>can set the <b>virtual processor count</b>;</item>
///   <item>can point the <b>base image layer</b> and <b>user-data disk</b> at custom paths;</item>
///   <item>can create and restore <b>checkpoints</b>.</item>
/// </list>
/// It requires an elevated process (HCS returns <c>0x8037011B</c> otherwise).
/// Runtime folder mapping and in-guest process execution are not supported and
/// fall back to the <see cref="ISandboxEngine"/> default-throwing members.
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class HcsSandboxEngine : ISandboxEngine, ISingletonDependency
{
    private const string ElevationMessage =
        "The Host Compute Service (HCS) requires an elevated process. " +
        "Run WindowsSandboxManager as Administrator (or as a member of the Hyper-V Administrators group).";

    private readonly IHcsClient _hcs;
    private readonly IHcsHostLayout _layout;
    private readonly ISandboxVmJsonBuilder _json;
    private readonly ISandboxStorageProvisioner _storage;
    private readonly ICheckpointService _checkpoints;
    private readonly IElevationService _elevation;
    private readonly ILogger<HcsSandboxEngine> _logger;

    /// <summary>
    /// Live compute systems created by this engine, keyed by compute-system id.
    /// The native <c>HCS_SYSTEM</c> handle <b>must</b> be held for the instance's
    /// whole lifetime: the configuration sets
    /// <c>ShouldTerminateOnLastHandleClosed=true</c>, so dropping the handle (as
    /// a <c>using</c> in <see cref="StartAsync"/> would) terminates the sandbox
    /// the instant the start call returns. The handle is released in
    /// <see cref="StopAsync"/>, which is what terminates the compute system.
    /// The runtime info is cached alongside so <see cref="GetRuntimeAsync"/> can
    /// report the real connection settings (RAIL mode, full-screen, title, …)
    /// rather than a synthesized default.
    /// </summary>
    private readonly ConcurrentDictionary<string, LiveSystem> _systems =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record LiveSystem(IDisposable Handle, SandboxRuntimeInfo Runtime, bool Adopted);

    public HcsSandboxEngine(
        IHcsClient hcs,
        IHcsHostLayout layout,
        ISandboxVmJsonBuilder json,
        ISandboxStorageProvisioner storage,
        ICheckpointService checkpoints,
        IElevationService elevation,
        ILogger<HcsSandboxEngine> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
        _storage = storage;
        _checkpoints = checkpoints;
        _elevation = elevation;
        _logger = logger;
    }

    public string Name => "HCS (computecore)";

    public SandboxEngineKind Kind => SandboxEngineKind.HcsSandbox;

    public bool IsAvailable => _elevation.IsElevated;

    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = true,
        SupportsCustomBaseImage = true,
        SupportsCheckpoints = true,
        SupportsRuntimeFolderMapping = false,
        SupportsProcessExecution = true,
        SupportsEnumeration = true,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var ids = _hcs.List()
            .Select(s => Guid.TryParse(s.Id, out var id) ? new SandboxInstanceId(id) : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();

        return Task.FromResult<IReadOnlyList<SandboxInstanceId>>(ids);
    }

    public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var id = request.Id;
        var recipe = request.Recipe;

        var baseLayer = _layout.ResolveBaseLayerPath()
            ?? throw new SandboxNotSupportedException(
                "No usable base OS image layer was found under " +
                $"{_layout.LayersFolder}. Start Windows Sandbox once so the base layer is provisioned.");

        var storageDirectory = recipe.Storage.HasCustomLocation
            ? recipe.Storage.StoragePath!
            : _layout.GetStorageDirectory(id.ToString());

        StorageLayout storage;
        try
        {
            storage = _storage.Provision(storageDirectory, recipe.Storage.DiskSizeMb);
        }
        catch (SandboxBackendException ex)
        {
            throw new SandboxBackendException(
                $"Failed to provision the user-data storage for sandbox {id} at " +
                $"'{storageDirectory}': {ex.Message}",
                ex.HResult,
                ex);
        }

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
            DumpFilePath = EnsureDumpPlaceholder(storageDirectory, id.ToString()),
            MemoryMb = (uint)recipe.Memory.MemoryMb,
            CpuCount = recipe.Cpu.CoreCount,
            EnableVirtualGpu = recipe.VirtualGpu != VirtualGpuMode.Disabled,
            EnableNetworking = recipe.Networking != NetworkingMode.Disabled,
            MappedFolders = recipe.MappedFolders
                .Select(f => new HcsMappedFolder(f.HostFolder, f.SandboxFolder, f.ReadOnly))
                .ToArray(),
            RdpNamedPipe = $@"\\.\pipe\{id}",
            RdpAccessSids = ResolveRdpAccessSids(),
        };

        var json = _json.Build(definition);
        _logger.LogInformation(
            "Creating HCS compute system {SandboxId} (base={BaseLayer}, storage={StorageDirectory}, " +
            "memory={MemoryMb}MB, cpus={CpuCount})",
            id, baseLayer, storage.Directory, definition.MemoryMb, definition.CpuCount);

        var (system, adopted) = CreateOrAdopt(id, json);

        string accountUser;
        string accountPassword;

        if (adopted)
        {
            // The compute system was already running (typically started by another
            // process, e.g. `wsm host`, and still up). It is already configured and
            // provisioned, so adopt it as-is. Its password is unknown here, but the
            // sandbox guest accepts the connection without an explicit credential.
            accountUser = string.IsNullOrWhiteSpace(recipe.AccountUser) ? DefaultGuestUser : recipe.AccountUser!;
            accountPassword = recipe.AccountPassword ?? string.Empty;
            _logger.LogInformation(
                "Sandbox {SandboxId} was already running; adopted the existing compute system " +
                "(skipping start and guest provisioning).",
                id);
        }
        else
        {
            try
            {
                // The compute system runs as a different identity than this process,
                // so it needs explicit access to every file backing the sandbox.
                // Storage under ProgramData inherits a broad ACL, but the default
                // per-user Documents location does not: before this the parent base
                // copy (sandbox.base.vhdx) was never granted, so Start failed with
                // 0xC03A0016 ("the process lacks permission"). The disk/guest-state
                // files are mandatory, so a failed grant is surfaced (actionable)
                // rather than swallowed; every other file in the directory — the
                // crash-dump placeholder, and anything a future provisioner adds —
                // is granted best-effort, as is the directory itself.
                GrantDiskFileAccess(id.ToString(), storage.DiskPath);
                GrantDiskFileAccess(id.ToString(), storage.GuestStatePath);

                // The parent base disk is, by default, the platform's own OS image
                // that lives outside the sandbox folder (it is referenced, never
                // copied), so it already carries a broad ACL and needs no grant. Only
                // a base disk that lives inside the folder (an explicitly configured
                // image) is granted, and then mandatorily.
                if (!string.IsNullOrWhiteSpace(storage.BaseDiskPath)
                    && IsWithinDirectory(storage.BaseDiskPath, storage.Directory))
                {
                    GrantDiskFileAccess(id.ToString(), storage.BaseDiskPath);
                }

                GrantRemainingStorageFilesQuietly(id.ToString(), storage);
                GrantVmAccessQuietly(id.ToString(), storage.Directory);

                _hcs.Start(id.ToString());
            }
            catch (SandboxBackendException ex)
            {
                system.Dispose();
                _logger.LogError(ex, "HCS rejected or failed to start compute system {SandboxId}", id);
                throw new SandboxBackendException(
                    $"HCS rejected the configuration or failed to start sandbox {id}: {ex.Message}",
                    ex.HResult,
                    ex);
            }
            catch (SandboxDomainException)
            {
                system.Dispose();
                throw;
            }
            catch
            {
                system.Dispose();
                throw;
            }

            // Mirror the platform's own guest set-up: enable the WDAGUtilityAccount
            // with a known password so the sandbox can actually be reached over RDP.
            // Without it the account has no usable credential and any RDP connect is
            // refused at logon.
            (accountUser, accountPassword) = ProvisionGuestAccount(id, recipe);
        }

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
            RailMode = recipe.RailMode,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,
            UserName = accountUser,
            Password = accountPassword,
            Redirections = recipe.Redirections,
        };

        // Keep the handle (and the runtime info) for the instance's lifetime;
        // releasing it is what stops the sandbox.
        RememberSystem(id.ToString(), system, runtime, adopted);

        return Task.FromResult(runtime);
    }

    /// <summary>
    /// Creates the compute system, or <b>adopts</b> the existing one when a system
    /// with this id is already running. Starting a sandbox is therefore
    /// idempotent: an already-running sandbox (for example one held by
    /// <c>wsm host</c> in another process) no longer fails with "a compute system
    /// with that id already exists" and no longer faults the instance, so the
    /// caller can go straight on to connect.
    /// </summary>
    private (IDisposable Handle, bool Adopted) CreateOrAdopt(
        SandboxInstanceId id,
        string json)
    {
        try
        {
            return (_hcs.Create(id.ToString(), json), false);
        }
        catch (Exception ex) when (IsRunning(id))
        {
            _logger.LogInformation(
                ex,
                "A compute system with id {SandboxId} already exists and is running; adopting it.",
                id);

            try
            {
                return (_hcs.Open(id.ToString()), true);
            }
            catch (Exception openEx)
            {
                _logger.LogWarning(
                    openEx,
                    "Unable to adopt the running compute system {SandboxId}.",
                    id);
                throw;
            }
        }
    }

    /// <summary>True when a compute system with this id currently exists.</summary>
    private bool IsRunning(SandboxInstanceId id)
    {
        try
        {
            return _hcs.Get(id.ToString()) is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Default guest logon account used by the Windows Sandbox image.</summary>
    private const string DefaultGuestUser = "WDAGUtilityAccount";

    /// <summary>How long to wait for the guest to accept processes after start.</summary>
    private const int GuestReadyTimeoutSeconds = 120;

    /// <summary>
    /// The platform's guest "specialization" tweaks (mirrored from
    /// <c>WindowsSandboxServer</c>). They are part of completing the guest set-up,
    /// which is what brings its services — including the RDP endpoint the sandbox
    /// is reached through — up.
    /// </summary>
    private static readonly string[] SpecializationCommands =
    {
        "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Network\\NewNetworkWindowOff\" /f",
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FlyoutMenuSettings\" /v ShowLockOption /t REG_DWORD /d 0 /f",
        "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" /v ShowTaskViewButton /t REG_DWORD /d 0 /f",
    };

    public Task<int> ExecuteAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var result = _hcs.RunProcess(id.ToString(), command.Command);
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogInformation("Guest output for '{Command}': {Output}", command.Command, result.StandardOutput.Trim());
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            _logger.LogWarning("Guest error for '{Command}': {Error}", command.Command, result.StandardError.Trim());
        }

        return Task.FromResult(result.ExitCode);
    }

    /// <summary>
    /// Provisions the guest logon account exactly like the platform's own
    /// <c>WindowsSandboxServer</c>: wait for the guest, set the account password,
    /// activate it and add it to the Users/Administrators groups. Returns the
    /// user/password the RDP session must use. Best effort: a failure is logged
    /// loudly and the sandbox still starts, but an RDP connect would then be
    /// refused, so the failure is never silent.
    /// </summary>
    private (string User, string Password) ProvisionGuestAccount(SandboxInstanceId id, SandboxRecipe recipe)
    {
        var user = string.IsNullOrWhiteSpace(recipe.AccountUser) ? DefaultGuestUser : recipe.AccountUser!;
        var password = string.IsNullOrWhiteSpace(recipe.AccountPassword)
            ? Guid.NewGuid().ToString()
            : recipe.AccountPassword!;

        try
        {
            WaitForGuestReady(id.ToString());

            // Mirror the platform's guest "specialization": activate the logon
            // account with a known password, apply the specialization tweaks and
            // signal that restore is complete. Without the latter the guest never
            // finishes its set-up and the RDP endpoint is never brought up.
            CheckGuest(id.ToString(), $"net user {user} {password}");
            CheckGuest(id.ToString(), $"net user {user} /active:yes");
            CheckGuest(id.ToString(), $"wcsetupagent.exe AddUserToUsersGroup {user}");
            CheckGuest(id.ToString(), $"wcsetupagent.exe AddUserToAdminGroup {user}");

            foreach (var specialization in SpecializationCommands)
            {
                CheckGuest(id.ToString(), specialization);
            }

            CheckGuest(id.ToString(), "wcsetupagent.exe NotifyRestoreComplete");

            // The sandbox is reached over the RDP named-pipe endpoint, so make
            // sure the guest's RDP listener is enabled (and record its state).
            CheckGuest(id.ToString(), "reg query \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v fDenyTSConnections");
            CheckGuest(id.ToString(), "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v fDenyTSConnections /t REG_DWORD /d 0 /f");
            CheckGuest(id.ToString(), "net start TermService");

            _logger.LogInformation("Provisioned guest account {User} for sandbox {SandboxId}.", user, id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to provision the guest account {User} for sandbox {SandboxId}; an RDP connect will likely be refused.",
                user, id);
        }

        return (user, password);
    }

    /// <summary>
    /// Waits until the guest accepts process creation. The compute system reports
    /// "started" before the guest OS is ready to run commands, so the platform's
    /// <c>net user</c> set-up is retried until it succeeds.
    /// </summary>
    private void WaitForGuestReady(string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(GuestReadyTimeoutSeconds);
        Exception? last = null;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (RunGuest(id, "cmd.exe /c exit 0") == 0)
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                last = ex;
            }

            Thread.Sleep(1000);
        }

        throw last ?? new SandboxBackendException("The sandbox guest did not become ready in time.", 0);
    }

    /// <summary>Runs a guest command and logs (but tolerates) a non-zero exit.</summary>
    private void CheckGuest(string id, string commandLine)
    {
        var result = _hcs.RunProcess(id, commandLine);
        if (result.ExitCode != 0)
        {
            _logger.LogWarning(
                "Guest command '{Command}' in sandbox {SandboxId} exited {ExitCode}. {Error}{Output}",
                commandLine, id, result.ExitCode, result.StandardError.Trim(), result.StandardOutput.Trim());
        }
        else
        {
            _logger.LogDebug(
                "Guest command '{Command}' in sandbox {SandboxId} exited 0. {Output}",
                commandLine, id, result.StandardOutput.Trim());
        }
    }

    private int RunGuest(string id, string commandLine)
    {
        var result = _hcs.RunProcess(id, commandLine);
        _logger.LogDebug(
            "Guest command '{Command}' in sandbox {SandboxId} exited {ExitCode}. {Output} {Error}",
            commandLine, id, result.ExitCode, result.StandardOutput.Trim(), result.StandardError.Trim());
        return result.ExitCode;
    }

    /// <summary>
    /// The SIDs allowed to open the EnhancedModeVideo RDP named pipe. The
    /// connecting process (the app / CLI) runs as the interactive user, so that
    /// user's SID plus SYSTEM and Administrators are granted; without this the
    /// RDPBASE connector fails with E_ACCESSDENIED (0x80070005).
    /// </summary>
    private static IReadOnlyList<string> ResolveRdpAccessSids()
    {
        var sids = new List<string>();

        try
        {
            var current = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (current is not null)
            {
                sids.Add(current.Value);
            }
        }
        catch
        {
            // Fall through to the well-known SIDs below.
        }

        sids.Add("S-1-5-18");       // LOCAL SYSTEM
        sids.Add("S-1-5-32-544");   // BUILTIN\Administrators
        return sids;
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="directory"/>.</summary>
    private static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        // Disposing the held HCS_SYSTEM handle closes the last handle to the
        // compute system; with ShouldTerminateOnLastHandleClosed=true that
        // terminates it. Fall back to an explicit terminate for systems this
        // engine did not create in this process.
        if (_systems.TryRemove(key, out var live))
        {
            live.Handle.Dispose();

            // An adopted system is also held by whoever started it, so releasing
            // our handle alone does not terminate it: stop it explicitly.
            if (live.Adopted)
            {
                _hcs.Terminate(key);
            }
        }
        else
        {
            _hcs.Terminate(key);
        }

        // A checkpoint-restored system holds its handle in the checkpoint
        // service; release it so the system can terminate.
        _checkpoints.Release(key);

        return Task.CompletedTask;
    }

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        // Prefer the runtime captured at start: it carries the real RAIL mode,
        // full-screen flag, title and redirections. A system we did not start in
        // this process can only be described with HCS defaults.
        if (_systems.TryGetValue(id.ToString(), out var live))
        {
            return Task.FromResult<SandboxRuntimeInfo?>(live.Runtime);
        }

        var info = _hcs.Get(id.ToString());
        if (info is null)
        {
            return Task.FromResult<SandboxRuntimeInfo?>(null);
        }

        return Task.FromResult<SandboxRuntimeInfo?>(new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = RdpTransportKind.NamedPipe,
        });
    }

    /// <summary>
    /// Reclaims the instance's on-disk footprint: the storage directory (the
    /// ~874 MB base copy, the differencing child, the VMGS and the crash-dump
    /// placeholder) and its snapshots directory. Both are best-effort: a partial
    /// failure is logged, never thrown, so the caller's delete still completes.
    /// The instance is expected to be stopped (the storage is in use otherwise).
    /// </summary>
    public Task DeleteDataAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var key = id.ToString();
        DeleteDirectoryQuietly(_layout.GetStorageDirectory(key), key, "storage");
        DeleteDirectoryQuietly(Path.Combine(_layout.SnapshotsFolder, key), key, "snapshots");
        return Task.CompletedTask;
    }

    private void DeleteDirectoryQuietly(string directory, string id, string kind)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            Directory.Delete(directory, recursive: true);
            _logger.LogInformation(
                "Deleted {Kind} directory '{Directory}' for sandbox {SandboxId}.",
                kind, directory, id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to delete {Kind} directory '{Directory}' for sandbox {SandboxId} (continuing).",
                kind, directory, id);
        }
    }

    public Task<SandboxCheckpoint> SaveCheckpointAsync(
        SandboxInstanceId id,
        string name,
        SandboxCheckpointType type,
        CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        return Task.FromResult(_checkpoints.Save(id.ToString(), name, type));
    }

    public Task<IReadOnlyList<SandboxCheckpoint>> ListCheckpointsAsync(
        SandboxInstanceId id,
        CancellationToken cancellationToken = default)
        => Task.FromResult(_checkpoints.List(id.ToString()));

    public Task<Guid> RestoreCheckpointAsync(
        SandboxInstanceId id,
        SandboxCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        EnsureElevated();

        var baseLayer = _layout.ResolveBaseLayerPath()
            ?? throw new SandboxNotSupportedException("No usable base OS image layer was found.");

        var storageDirectory = _layout.GetStorageDirectory(id.ToString());
        var storage = _storage.Provision(storageDirectory);

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
        };

        var newId = _checkpoints.Restore(definition, checkpoint);
        return Task.FromResult(Guid.Parse(newId));
    }

    private void EnsureElevated()
    {
        if (!_elevation.IsElevated)
        {
            throw new SandboxNotSupportedException(ElevationMessage);
        }
    }

    /// <summary>
    /// Creates the zero-byte guest crash dump placeholder HCS expects. The
    /// "Microsoft Guest Crash Dump" device opens this file at Start and fails
    /// with <c>0x80070002</c> when it is absent, so the file (and its directory)
    /// must already exist. Returns the resolved path.
    /// </summary>
    private static string EnsureDumpPlaceholder(string storageDirectory, string id)
    {
        var dumpPath = Path.Combine(storageDirectory, $"{id}.dmp");
        var directory = Path.GetDirectoryName(dumpPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (!File.Exists(dumpPath))
        {
            using var _ = File.Create(dumpPath);
        }

        return dumpPath;
    }

    private void RememberSystem(string key, IDisposable handle, SandboxRuntimeInfo runtime, bool adopted = false)
    {
        var live = new LiveSystem(handle, runtime, adopted);

        // A duplicate id should not occur (HCS rejects it), but never leak the
        // previous handle if it does.
        if (_systems.TryRemove(key, out var stale))
        {
            try
            {
                stale.Handle.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Replacing the handle for {SandboxId} failed.", key);
            }
        }

        _systems[key] = live;
    }

    private void GrantVmAccessQuietly(string id, string path)
    {
        try
        {
            _hcs.GrantVmAccess(id, path);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Granting VM access to '{Path}' for {SandboxId} failed (continuing).", path, id);
        }
    }

    /// <summary>
    /// Grants the compute system access to a storage file that the sandbox
    /// <b>must</b> be able to read. A failure is surfaced as a
    /// <see cref="SandboxBackendException"/> naming the path: HCS runs the
    /// sandbox under a different identity, so without the grant <c>Start</c>
    /// fails opaquely with <c>0xC03A0016</c>; a genuine ACL problem should be
    /// actionable. A path that does not exist (e.g. the base copy when a
    /// standalone/blank disk was used) is skipped — there is nothing to grant.
    /// </summary>
    private void GrantDiskFileAccess(string id, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            _hcs.GrantVmAccess(id, path);
        }
        catch (Exception ex)
        {
            throw new SandboxBackendException(
                $"Failed to grant the sandbox compute system access to storage file '{path}'. " +
                "HCS runs the sandbox under a different identity and needs explicit access " +
                "to every backing file; without it Start fails with 0xC03A0016 " +
                $"(\"the process lacks permission\"). {ex.Message}",
                ex.HResult,
                ex);
        }
    }

    /// <summary>
    /// Best-effort grant of every <i>other</i> file in the storage directory
    /// (the crash-dump placeholder, and any file a future provisioner adds).
    /// Enumerating the directory — rather than hard-coding file names — keeps
    /// the sandbox startable when the storage layout grows; a failure is logged,
    /// never thrown, because these files are optional to the disk access path.
    /// </summary>
    private void GrantRemainingStorageFilesQuietly(string id, StorageLayout storage)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(storage.Directory, "*", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Enumerating storage directory '{Directory}' for {SandboxId} failed (continuing).",
                storage.Directory, id);
            return;
        }

        foreach (var file in files)
        {
            if (string.Equals(file, storage.DiskPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, storage.BaseDiskPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, storage.GuestStatePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            GrantVmAccessQuietly(id, file);
        }
    }
}
