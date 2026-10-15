using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
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
    private readonly IHnsNetworkService _network;
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
        IHnsNetworkService network,
        IElevationService elevation,
        ILogger<HcsSandboxEngine> logger)
    {
        _hcs = hcs;
        _layout = layout;
        _json = json;
        _storage = storage;
        _checkpoints = checkpoints;
        _network = network;
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

        // TCP RDP rides the guest NIC: without networking there is nothing to
        // connect to. Reject the contradictory configuration before anything
        // (storage, endpoint, compute system) is created.
        if (recipe.Transport == RdpTransportKind.Tcp && recipe.Networking == NetworkingMode.Disabled)
        {
            throw new SandboxValidationException(
                "The TCP RDP transport requires networking: set Networking to Enabled/Default, " +
                "or switch the sandbox back to the named-pipe transport.");
        }

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

        // Attach an HNS endpoint at creation time so the guest boots with a NIC:
        // hot-adding a network adapter to a running utility VM is rejected by
        // HCS (0x80070057). A system that is already running (to be adopted)
        // carries its own endpoint; touching HNS for it here could detach the
        // NIC from under the live VM, so it is left alone.
        var endpointId = IsRunning(id) || recipe.Networking == NetworkingMode.Disabled
            ? null
            : CreateGuestEndpoint(id);

        var rdpAccessSids = ResolveRdpAccessSids();

        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
            DumpFilePath = EnsureDumpPlaceholder(storageDirectory, id.ToString()),
            MemoryMb = (uint)recipe.Memory.MemoryMb,
            CpuCount = recipe.Cpu.CoreCount,
            EnableNetworking = recipe.Networking != NetworkingMode.Disabled,
            NetworkEndpointId = endpointId,
            MappedFolders = recipe.MappedFolders
                .Select(f => new HcsMappedFolder(f.HostFolder, f.SandboxFolder, f.ReadOnly))
                .ToArray(),
            RdpNamedPipe = $@"\\.\pipe\{id}",
            RdpAccessSids = rdpAccessSids,

            // The platform's full HvSocket service table: the guest's interactive
            // session (shell) bootstrap is wired to these services, and with only
            // the built-in LSM/NLM pair a full-desktop RDP logon is arbitrated and
            // then dropped without ever starting the shell (the black desktop).
            AdditionalHvSocketServices = HcsSandboxConstants.PlatformAdditionalHvSocketServices(
                rdpAccessSids.FirstOrDefault()),
        };

        var json = _json.Build(definition);
        _logger.LogInformation(
            "Creating HCS compute system {SandboxId} (base={BaseLayer}, storage={StorageDirectory}, " +
            "memory={MemoryMb}MB, cpus={CpuCount})",
            id, baseLayer, storage.Directory, definition.MemoryMb, definition.CpuCount);

        var (system, adopted) = CreateOrAdopt(id, json);

        string accountUser;
        string accountPassword;

        // Set when the sandbox opted into TCP and its RDP-Tcp listener answers;
        // the runtime then reports the TCP transport instead of the named pipe.
        // Named-pipe sandboxes never probe, so this stays null for them.
        string? guestIp = null;

        if (adopted)
        {
            // The compute system was already running (typically started by another
            // process, e.g. `wsm host`, and still up). It is already configured and
            // provisioned with the deterministic credential, so adopt it as-is.
            accountUser = string.IsNullOrWhiteSpace(recipe.AccountUser) ? DefaultGuestUser : recipe.AccountUser!;
            accountPassword = string.IsNullOrWhiteSpace(recipe.AccountPassword)
                ? DefaultGuestPassword(id)
                : recipe.AccountPassword!;
            _logger.LogInformation(
                "Sandbox {SandboxId} was already running; adopted the existing compute system " +
                "(skipping start and guest provisioning).",
                id);

            // The credential is not persisted anywhere, so re-apply it: the account's
            // password may have been set by a previous process (or is simply unknown),
            // and RDP needs the current one or the logon is refused.
            CheckGuest(id.ToString(), $"net user {accountUser} {accountPassword}");
            CheckGuest(id.ToString(), $"net user {accountUser} /active:yes");

            // The adopted system was started by another process (with its own
            // endpoint attached at creation time), so only probe, and only when
            // the caller asked for TCP: the guest is long past boot, DHCP has
            // settled, and the listener must already be up — restarting
            // TermService would kick a live session.
            if (recipe.Transport == RdpTransportKind.Tcp)
            {
                guestIp = EnableTcpRdp(id, waitForDhcp: false, allowListenerRestart: false);
                if (guestIp is null)
                {
                    system.Dispose();
                    throw new SandboxBackendException(
                        $"The TCP RDP transport was requested for sandbox {id}, but the adopted " +
                        "compute system has no reachable guest listener on 3389 (it may have been " +
                        "started without TCP, e.g. by another process in named-pipe mode). Stop it " +
                        "and start the sandbox again, or switch it back to the named-pipe transport.",
                        0);
                }
            }
            else if (HasStaleTcpConfig(id.ToString()))
            {
                // Another process owns this system's lifecycle, so the revert
                // (which needs a TermService restart) is not safe to run here:
                // it would kick that process's live session. Warn instead.
                _logger.LogWarning(
                    "Sandbox {SandboxId} has a leftover TCP RDP configuration from a previous TCP run; " +
                    "a named-pipe session may stall against it. Stop and start the sandbox to revert it.",
                    id);
            }
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
                DeleteGuestEndpointQuietly(id);
                _logger.LogError(ex, "HCS rejected or failed to start compute system {SandboxId}", id);
                throw new SandboxBackendException(
                    $"HCS rejected the configuration or failed to start sandbox {id}: {ex.Message}",
                    ex.HResult,
                    ex);
            }
            catch (SandboxDomainException)
            {
                system.Dispose();
                DeleteGuestEndpointQuietly(id);
                throw;
            }
            catch
            {
                system.Dispose();
                DeleteGuestEndpointQuietly(id);
                throw;
            }

            // Mirror the platform's own guest set-up: enable the WDAGUtilityAccount
            // with a known password so the sandbox can actually be reached over RDP.
            (accountUser, accountPassword) = ProvisionGuestAccount(id, recipe);

            // Open TCP RDP on the freshly attached NIC, but only when the
            // sandbox opted into TCP: the named-pipe (EnhancedModeVideo)
            // transport arbitrates a single interactive session across ALL
            // sandboxes on the host, so parallel connects kick each other off,
            // while TCP 3389 has no such global arbitration. The pipe stays the
            // default: it is faster (VMBus, no TCP stack or ICS NAT) and needs
            // no guest changes. A requested TCP transport that never comes up
            // fails the start — silently staying on the pipe would surface
            // later as exactly the session-kicking the user switched away from.
            if (recipe.Transport == RdpTransportKind.Tcp)
            {
                try
                {
                    guestIp = endpointId is null
                        ? null
                        : EnableTcpRdp(id, waitForDhcp: true, allowListenerRestart: true);
                }
                catch
                {
                    StopFailedTcpStart(id, system);
                    throw;
                }

                if (guestIp is null)
                {
                    StopFailedTcpStart(id, system);
                    throw new SandboxBackendException(
                        $"The TCP RDP transport was requested for sandbox {id}, but the guest " +
                        "listener on 3389 never came up (see the warnings for the failing stage: " +
                        "endpoint, DHCP or listener). Retry, or switch the sandbox back to the " +
                        "named-pipe transport.",
                        0);
                }
            }
            else
            {
                // Pipe mode on a guest that once ran TCP: the leftover
                // container-mode flip (it persists on the user-data disk) lets
                // a pipe session connect but stalls it at Conn. Revert it — a
                // guest that dies mid-revert gets the same cleanup as the TCP
                // branch (a stale config stalls pipe sessions, so failing the
                // start beats booting into the stall).
                try
                {
                    RevertStaleTcpConfig(id);
                }
                catch
                {
                    StopFailedTcpStart(id, system);
                    throw;
                }
            }
        }

        var runtime = new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = guestIp is not null ? RdpTransportKind.Tcp : RdpTransportKind.NamedPipe,
            IpAddress = guestIp ?? string.Empty,
            IpAddresses = guestIp is not null ? [guestIp] : Array.Empty<string>(),
            RailMode = recipe.RailMode,
            RailProgram = recipe.ResolvedRailProgram,
            FullScreen = recipe.FullScreen,
            WindowTitle = recipe.Name.Value,

            // The credential travels with the runtime. The RDP client must supply it
            // or the WDAG guest stays at its logon screen (the black desktop): it does
            // not auto-logon on its own, and RDP is the only way in.
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

    /// <summary>
    /// The platform's <c>Constants.VMSpecializationCommands</c> (mirrored from
    /// <c>WindowsSandboxServer</c>), run as SYSTEM right after the account set-up.
    /// </summary>
    private const string GuestSpecializationCommands =
        "cmd.exe /c " +
        "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Network\\NewNetworkWindowOff\" /f & " +
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\FlyoutMenuSettings\" " +
        "/v ShowLockOption /t REG_DWORD /d 0 /f & " +
        "reg add \"HKCU\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Explorer\\Advanced\" " +
        "/v ShowTaskViewButton /t REG_DWORD /d 0 /f";

    /// <summary>
    /// The guest password used when the recipe does not specify one. It is derived
    /// <b>deterministically</b> from the sandbox id so any process can compute the
    /// same credential and actually log the RDP session on: the WDAG guest shows
    /// its logon screen (a black desktop) until a valid credential is supplied, and
    /// a random per-start password would be known only to the process that started
    /// the sandbox, so a separate <c>wsm connect</c> could never log in.
    /// </summary>
    private static string DefaultGuestPassword(SandboxInstanceId id)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(id.ToString()));
        return "Wsm!" + Convert.ToHexString(hash.AsSpan(0, 10));
    }

    /// <summary>How long to wait for the guest to accept processes after start.</summary>
    private const int GuestReadyTimeoutSeconds = 120;

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
    /// Returns the sandbox's guest addresses: the address recorded at start (the
    /// TCP transport's DHCP lease) when known, otherwise a live read of the
    /// guest NIC, and finally the HNS endpoint records. This is what makes an
    /// <c>mstsc</c> connect possible for a TCP sandbox.
    /// </summary>
    public Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        if (_systems.TryGetValue(key, out var live))
        {
            if (live.Runtime.IpAddresses.Count > 0)
            {
                return Task.FromResult<IReadOnlyList<string>>(live.Runtime.IpAddresses);
            }

            if (!string.IsNullOrWhiteSpace(live.Runtime.IpAddress))
            {
                return Task.FromResult<IReadOnlyList<string>>([live.Runtime.IpAddress]);
            }
        }

        var discovered = DiscoverGuestIp(key, waitForDhcp: false);
        if (discovered is not null)
        {
            return Task.FromResult<IReadOnlyList<string>>([discovered]);
        }

        return _network.GetIpAddressesAsync(key, cancellationToken);
    }

    /// <summary>
    /// Runs a guest command and returns its output. Unlike
    /// <see cref="ExecuteAsync"/> — which logs the streams at Information because
    /// the caller only wants the exit code — the captured output is logged at
    /// Debug, so a caller that inspects it (the session reclaim runs
    /// <c>qwinsta</c> on every connect) does not spam the log.
    /// </summary>
    public Task<GuestCommandResult> CaptureAsync(SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var result = _hcs.RunProcess(id.ToString(), command.Command);

        _logger.LogDebug(
            "Captured guest command '{Command}' in sandbox {SandboxId} (exit {ExitCode}). {Output} {Error}",
            command.Command, id, result.ExitCode, result.StandardOutput.Trim(), result.StandardError.Trim());

        return Task.FromResult(new GuestCommandResult(result.ExitCode, result.StandardOutput, result.StandardError));
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
            ? DefaultGuestPassword(id)
            : recipe.AccountPassword!;

        try
        {
            WaitForGuestReady(id.ToString());

            // The platform's own guest set-up (mirrored from WindowsSandboxServer):
            // activate the logon account with a known password, add it to the groups
            // Remote Desktop Services checks, flip the sandbox's own RDP listener
            // switch, run the platform's specialization commands, then complete the
            // image-prep handshake. This exact sequence is what makes an interactive
            // RDP session bootstrap on a custom VM: the WDAG guest otherwise shows
            // its logon screen (a black desktop) and refuses the remote logon.
            CheckGuest(id.ToString(), $"net user {user} {password}");
            CheckGuest(id.ToString(), $"net user {user} /active:yes");
            CheckGuest(id.ToString(), $"wcsetupagent.exe AddUserToUsersGroup {user}");
            CheckGuest(id.ToString(), $"wcsetupagent.exe AddUserToAdminGroup {user}");
            CheckGuest(id.ToString(), $"wcsetupagent.exe AddUserToRemoteDesktopUsers {user}");
            CheckGuest(
                id.ToString(),
                "reg add \"HKLM\\System\\CurrentControlSet\\Control\\Terminal Server\" " +
                "/v b8ba549e-fb39-441c-b71e-44e9e4818210 /t REG_DWORD /d 1 /f");
            CheckGuest(id.ToString(), GuestSpecializationCommands);
            CheckGuest(id.ToString(), "wcsetupagent.exe NotifyRestoreComplete");

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
    /// Endpoint names are deterministic, so <see cref="StopAsync"/> (and a
    /// later process cleaning up) can find the endpoint without any state.
    /// </summary>
    private static string GuestEndpointName(SandboxInstanceId id) => $"wsm-{id.Value:N}";

    /// <summary>Best-effort endpoint cleanup; a name that matches nothing is a no-op.</summary>
    private void DeleteGuestEndpointQuietly(SandboxInstanceId id)
    {
        try
        {
            _network.DeleteEndpointByNameAsync(GuestEndpointName(id)).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Deleting the HNS endpoint of {SandboxId} failed (continuing).", id);
        }
    }

    /// <summary>
    /// Cleans up a fresh system whose requested TCP transport never came up (or
    /// whose setup threw). The guest already booted, so dropping the handle
    /// alone would not reliably stop it (ShouldTerminateOnLastHandleClosed is
    /// not honoured — the same reason <see cref="StopAsync"/> does explicit
    /// shutdown), and deleting the endpoint first would detach the NIC from
    /// under the live VM. Runs the same deterministic stop, then releases the
    /// handle, then removes the endpoint. Never throws: the original failure
    /// is what the caller propagates.
    /// </summary>
    private void StopFailedTcpStart(SandboxInstanceId id, IDisposable system)
    {
        var key = id.ToString();
        try
        {
            StopComputeSystem(key, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stopping the failed TCP start of {SandboxId} failed (continuing).", key);
        }

        try
        {
            system.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Releasing the compute-system handle of {SandboxId} failed (continuing).", key);
        }

        DeleteGuestEndpointQuietly(id);
    }

    /// <summary>
    /// Creates the HNS NAT endpoint the sandbox boots with and returns its id.
    /// Prefers an existing NAT/ICS network (the Hyper-V "Default Switch"); the
    /// <c>wsm-nat</c> network is created when the host has none. A stale
    /// endpoint left behind by an instance that stopped uncleanly is removed
    /// first (names are deterministic). Best effort: on failure the sandbox
    /// boots without a NIC — acceptable for the named-pipe transport, while a
    /// TCP-transport caller turns the null into a start failure.
    /// </summary>
    private string? CreateGuestEndpoint(SandboxInstanceId id)
    {
        var name = GuestEndpointName(id);
        try
        {
            var networkId = _network.GetOrCreateNatNetworkIdAsync().GetAwaiter().GetResult();
            _network.DeleteEndpointByNameAsync(name).GetAwaiter().GetResult();
            var endpointId = _network.CreateNatEndpointAsync(networkId, name).GetAwaiter().GetResult();
            _logger.LogInformation(
                "Created HNS endpoint {EndpointName} ({EndpointId}) on network {NetworkId} for sandbox {SandboxId}.",
                name, endpointId, networkId, id);
            return endpointId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to create an HNS endpoint for sandbox {SandboxId}; it will run without a NIC (pipe-only, single-session RDP).",
                id);
            return null;
        }
    }

    /// <summary>
    /// Turns the guest's RDP-Tcp listener into a usable TCP endpoint: finds the
    /// guest IPv4 address (waiting for ICS DHCP when the VM just booted),
    /// switches the guest's RDP stack out of container mode (container mode
    /// only ever instantiates the hvsocket listener — RDP-Tcp is silently
    /// skipped), disables NLA (the WSM RDP client has no CredSSP support) and
    /// opens the firewall for 3389. The container-mode flip only takes effect
    /// on a TermService (re)start, so when the listener has not bound 3389 the
    /// service is restarted — but only on a fresh boot
    /// (<paramref name="allowListenerRestart"/>); on an adopted system that
    /// would kick the live session (there the flip still applies at the next
    /// boot; the guest registry persists on the user-data disk). Only invoked
    /// when the sandbox opted into TCP. Returns the guest IP, or null when TCP
    /// RDP is not reachable (the caller then fails the start).
    /// </summary>
    private string? EnableTcpRdp(SandboxInstanceId id, bool waitForDhcp, bool allowListenerRestart)
    {
        var key = id.ToString();
        var ip = DiscoverGuestIp(key, waitForDhcp);
        if (ip is null)
        {
            _logger.LogWarning(
                "Sandbox {SandboxId} has a NIC but no guest IPv4 address could be discovered; the requested TCP transport is unavailable and the start will fail.",
                id);
            return null;
        }

        CheckGuest(
            key,
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v ContainerMode /t REG_DWORD /d 0 /f");
        // Standard RDP security, not TLS: the mstscax connector is used without
        // CredSSP, and its early TLS negotiation against this container termsrv
        // dies before the handshake (client never reaches the username-hash
        // step). The pipe transport negotiates the same standard security.
        CheckGuest(
            key,
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp\" " +
            "/v SecurityLayer /t REG_DWORD /d 0 /f");
        CheckGuest(
            key,
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\\WinStations\\RDP-Tcp\" " +
            "/v UserAuthentication /t REG_DWORD /d 0 /f");
        CheckGuest(key, "netsh advfirewall firewall delete rule name=WsmRdp3389");
        CheckGuest(key, "netsh advfirewall firewall add rule name=WsmRdp3389 protocol=TCP dir=in localport=3389 action=allow");

        if (!IsTcpListenerUp(key) && allowListenerRestart)
        {
            _logger.LogInformation(
                "The guest RDP-Tcp listener in sandbox {SandboxId} has not bound 3389; restarting TermService (container-mode flip needs it).",
                id);
            CheckGuest(key, "net stop termservice /y");
            Thread.Sleep(TermServiceStopSettle);
            CheckGuest(key, "net start termservice");
            Thread.Sleep(TermServiceStartSettle);
        }

        if (!IsTcpListenerUp(key))
        {
            _logger.LogWarning(
                "The guest RDP-Tcp listener on 3389 is not up in sandbox {SandboxId}; the requested TCP transport is unavailable and the start will fail.",
                id);
            return null;
        }

        _logger.LogInformation("Sandbox {SandboxId} accepts TCP RDP at {GuestIp}:3389.", id, ip);
        return ip;
    }

    /// <summary>
    /// True when the guest still carries a TCP RDP configuration from a previous
    /// TCP run (ContainerMode=0): the registry persists on the user-data disk.
    /// A guest in that state lets a named-pipe session connect but never
    /// complete its logon — the hvsocket listener and the full RDP stack
    /// coexist and the session stalls at Conn. Read-only; a probe failure is
    /// treated as "no leftover" so a pipe start is never blocked by a hiccup.
    /// </summary>
    private bool HasStaleTcpConfig(string key)
    {
        try
        {
            var query = _hcs.RunProcess(
                key,
                @"reg query ""HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server"" /v ContainerMode");
            return query.ExitCode == 0 &&
                query.StandardOutput.Contains("0x0", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Checking {SandboxId} for a leftover TCP configuration failed; assuming none.", key);
            return false;
        }
    }

    /// <summary>
    /// Reverts a stale guest TCP configuration (see <see cref="HasStaleTcpConfig"/>)
    /// so the named-pipe session can initialize again: back to container mode,
    /// the 3389 firewall rule dropped, and TermService restarted (the
    /// container-mode flip only takes effect on a service restart). Called on
    /// fresh starts in pipe mode; clean guests pay a single registry query.
    /// </summary>
    private void RevertStaleTcpConfig(SandboxInstanceId id)
    {
        var key = id.ToString();
        if (!HasStaleTcpConfig(key))
        {
            return;
        }

        _logger.LogInformation(
            "Sandbox {SandboxId} has a leftover TCP RDP configuration from a previous TCP run; reverting it to container mode so the named-pipe session can initialize.",
            id);
        CheckGuest(
            key,
            "reg add \"HKLM\\SYSTEM\\CurrentControlSet\\Control\\Terminal Server\" /v ContainerMode /t REG_DWORD /d 1 /f");
        CheckGuest(key, "netsh advfirewall firewall delete rule name=WsmRdp3389");
        CheckGuest(key, "net stop termservice /y");
        Thread.Sleep(TermServiceStopSettle);
        CheckGuest(key, "net start termservice");
        Thread.Sleep(TermServiceStartSettle);
    }

    /// <summary>
    /// TCP probe for systems this process did not start (a separate
    /// <c>wsm connect</c>, the app after a restart): returns the guest IP when
    /// its RDP-Tcp listener answers, null otherwise. Read-only — it never
    /// touches the guest configuration.
    /// <para>
    /// The HNS endpoint created at start is the authoritative source: its name
    /// is deterministic and its MAC pins the guest NIC, so every process finds
    /// the same address. Scraping the first non-APIPA address off
    /// <c>ipconfig</c> (the old behaviour) is non-deterministic — adapter
    /// order varies between processes and a DHCP re-lease changes it — and
    /// sent reconnects to an address the guest did not answer on.
    /// </para>
    /// </summary>
    private string? ProbeTcpTransport(SandboxInstanceId id)
    {
        var key = id.ToString();
        try
        {
            var endpoint = _network.GetEndpointByNameAsync(GuestEndpointName(id)).GetAwaiter().GetResult();
            if (endpoint is not null)
            {
                if (IsUsableGuestIpv4(endpoint.IpAddress) && IsTcpListenerUp(key))
                {
                    return endpoint.IpAddress;
                }

                if (endpoint.MacAddress is not null)
                {
                    var ip = DiscoverGuestIpByMac(key, endpoint.MacAddress);
                    if (ip is not null && IsTcpListenerUp(key))
                    {
                        return ip;
                    }
                }
            }

            var fallback = DiscoverGuestIp(key, waitForDhcp: false);
            return fallback is not null && IsTcpListenerUp(key) ? fallback : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Probing the TCP transport of {SandboxId} failed; assuming named pipe.", id);
            return null;
        }
    }

    private static bool IsUsableGuestIpv4(string? address)
        => address is not null
           && !address.StartsWith("169.254.", StringComparison.Ordinal)
           && System.Net.IPAddress.TryParse(address, out var parsed)
           && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    /// <summary>
    /// Reads the guest IPv4 address of the NIC whose MAC matches
    /// <paramref name="macAddress"/> off <c>ipconfig /all</c>, retrying for a
    /// short window so a transient guest-command hiccup does not fail the
    /// connect. Locale-independent: the MAC line is matched by its digits, not
    /// by the "Physical Address" label.
    /// </summary>
    private string? DiscoverGuestIpByMac(string key, string macAddress)
    {
        var deadline = DateTime.UtcNow + ProbeDhcpWaitTimeout;
        var wanted = NormalizeMac(macAddress);

        while (true)
        {
            string output;
            try
            {
                output = _hcs.RunProcess(key, "cmd.exe /c ipconfig /all").StandardOutput;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the guest IP configuration of {SandboxId} failed.", key);
                output = string.Empty;
            }

            var ip = ParseGuestIpv4ByMac(output, wanted);
            if (ip is not null || DateTime.UtcNow >= deadline)
            {
                return ip;
            }

            Thread.Sleep(DhcpPollInterval);
        }
    }

    private static string NormalizeMac(string text)
    {
        var chars = text.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Finds the line carrying the (normalized) MAC, then takes the first IPv4
    /// address listed after it — within an adapter section the addresses follow
    /// the physical address. APIPA addresses are rejected.
    /// </summary>
    private static string? ParseGuestIpv4ByMac(string ipconfigOutput, string normalizedMac)
    {
        if (normalizedMac.Length == 0)
        {
            return null;
        }

        var lines = ipconfigOutput.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!NormalizeMac(lines[i]).Contains(normalizedMac, StringComparison.Ordinal))
            {
                continue;
            }

            for (var j = i + 1; j < lines.Length; j++)
            {
                // Adapter sections are separated by blank lines; do not leak
                // into the next adapter when this NIC has no IPv4 address.
                if (string.IsNullOrWhiteSpace(lines[j]))
                {
                    break;
                }

                var ip = ParseGuestIpv4(lines[j]);
                if (ip is not null)
                {
                    return ip;
                }
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// Reads the guest IPv4 address off <c>ipconfig</c>. An APIPA address
    /// (169.254.*) means DHCP has not answered yet and is not accepted; with
    /// <paramref name="waitForDhcp"/> the read is retried until
    /// <see cref="DhcpWaitTimeout"/> elapses, otherwise only for the short
    /// <see cref="ProbeDhcpWaitTimeout"/> window (a one-shot read made a
    /// transient guest-command hiccup fail the whole connect).
    /// </summary>
    private string? DiscoverGuestIp(string key, bool waitForDhcp)
    {
        var deadline = DateTime.UtcNow + (waitForDhcp ? DhcpWaitTimeout : ProbeDhcpWaitTimeout);

        while (true)
        {
            string output;
            try
            {
                output = _hcs.RunProcess(key, "cmd.exe /c ipconfig").StandardOutput;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Reading the guest IP configuration of {SandboxId} failed.", key);
                output = string.Empty;
            }

            var ip = ParseGuestIpv4(output);
            if (ip is not null || DateTime.UtcNow >= deadline)
            {
                return ip;
            }

            Thread.Sleep(DhcpPollInterval);
        }
    }

    private static string? ParseGuestIpv4(string ipconfigOutput)
    {
        foreach (var line in ipconfigOutput.Split('\n'))
        {
            if (!line.Contains("IPv4", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = line.LastIndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var candidate = line[(colon + 1)..].Trim();
            if (candidate.StartsWith("169.254.", StringComparison.Ordinal))
            {
                continue;
            }

            if (System.Net.IPAddress.TryParse(candidate, out var address) &&
                address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return candidate;
            }
        }

        return null;
    }

    private bool IsTcpListenerUp(string key)
    {
        try
        {
            var output = _hcs.RunProcess(key, "cmd.exe /c netstat -ano -p tcp").StandardOutput;
            return output.Split('\n').Any(line =>
                line.Contains(":3389") && line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the guest listeners of {SandboxId} failed.", key);
            return false;
        }
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

    // Stop/delete timing budgets. Mutable internally so tests can shrink them;
    // production uses the defaults.
    internal TimeSpan ShutdownWaitTimeout { get; set; } = TimeSpan.FromSeconds(45);

    internal TimeSpan StoppedPollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal TimeSpan TerminatedPollTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal TimeSpan StoppedPollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    internal int DeleteMaxAttempts { get; set; } = 5;

    internal TimeSpan DeleteRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);

    // Guest DHCP discovery timing budgets. Mutable internally so tests can
    // shrink them; production uses the defaults.
    internal TimeSpan DhcpWaitTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Bound for the guest-IP read on the connect path (the sandbox booted long
    /// ago, so DHCP has answered; the window only absorbs transient
    /// guest-command hiccups).
    /// </summary>
    internal TimeSpan ProbeDhcpWaitTimeout { get; set; } = TimeSpan.FromSeconds(3);

    internal TimeSpan DhcpPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Settle delays around a TermService stop/start (the container-mode flip needs it).</summary>
    internal TimeSpan TermServiceStopSettle { get; set; } = TimeSpan.FromSeconds(2);

    internal TimeSpan TermServiceStartSettle { get; set; } = TimeSpan.FromSeconds(3);

    public async Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        EnsureElevated();
        var key = id.ToString();

        // Take the held handle out of the registry up front; it is disposed only
        // AFTER the compute system is gone. Teardown is driven by the
        // shutdown/terminate calls below, not by handle counting: the JSON
        // ShouldTerminateOnLastHandleClosed flag is not honoured reliably, so
        // dropping the handle alone used to leave the VM (and its file locks)
        // alive indefinitely.
        _systems.TryRemove(key, out var live);

        try
        {
            await Task.Run(() => StopComputeSystem(key, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (live is not null)
            {
                try
                {
                    live.Handle.Dispose();
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Releasing the compute-system handle for {SandboxId} failed.", key);
                }
            }

            // A checkpoint-restored system holds its handle in the checkpoint
            // service; release it so the system can terminate.
            _checkpoints.Release(key);
        }

        // HNS endpoints outlive their compute system. Only reached once the VM
        // is actually gone (a failed stop throws above), so the endpoint is
        // detached and safe to remove.
        DeleteGuestEndpointQuietly(id);
    }

    /// <summary>
    /// Deterministically stops the compute system: graceful ACPI shutdown first,
    /// a bounded poll for the system to disappear, then an explicit terminate
    /// when the graceful path stalls. Returns only when the system is gone, so a
    /// caller that deletes the storage next does not race vmwp's file handles.
    /// Works the same for systems this process created and for adopted/foreign
    /// ones. Throws when the system survives every escalation — a survivor must
    /// never be reported as stopped.
    /// </summary>
    private void StopComputeSystem(string key, CancellationToken cancellationToken)
    {
        try
        {
            _hcs.Shutdown(key, (uint)ShutdownWaitTimeout.TotalMilliseconds);
        }
        catch (SandboxNotFoundException)
        {
            return; // Already gone.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graceful shutdown of {SandboxId} could not be requested; escalating.", key);
        }

        if (WaitUntilGone(key, StoppedPollTimeout, cancellationToken))
        {
            return;
        }

        _logger.LogWarning(
            "Sandbox {SandboxId} did not stop within {TimeoutSeconds}s of the shutdown request; terminating it.",
            key, StoppedPollTimeout.TotalSeconds);

        try
        {
            _hcs.Terminate(key);
        }
        catch (SandboxNotFoundException)
        {
            // The system vanished between the last poll and the terminate call.
            return;
        }

        if (!WaitUntilGone(key, TerminatedPollTimeout, cancellationToken))
        {
            // Returning normally here would let the caller mark (and persist)
            // the sandbox as stopped while the VM keeps running: the UI would
            // then hide Stop/Disconnect and its storage could be deleted under
            // a live vmwp. Fail loudly instead so the state stays live.
            throw new SandboxBackendException(
                $"Sandbox {key} is still present {TerminatedPollTimeout.TotalSeconds}s after Terminate; " +
                "it refuses to stop. Its state was left unchanged — try stopping it again.",
                0);
        }
    }

    /// <summary>
    /// Polls until the compute system is gone (or reports <c>Stopped</c>), the
    /// timeout elapses, or the caller cancels. A query failure is treated as
    /// "still there" so a transient HCS hiccup does not skip the terminate
    /// escalation.
    /// </summary>
    private bool WaitUntilGone(string key, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            HcsComputeSystemInfo? info;
            var queryFailed = false;
            try
            {
                info = _hcs.Get(key);
            }
            catch (SandboxNotFoundException)
            {
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Querying the state of {SandboxId} failed; assuming it is still present.", key);
                info = null;
                queryFailed = true;
            }

            if (!queryFailed &&
                (info is null || string.Equals(info.State, "Stopped", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(StoppedPollInterval);
        }
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

        // `connect` reaches the runtime only through here (it never calls
        // StartAsync in this process), so the TCP transport the starting
        // process set up must be re-probed: when the guest's RDP-Tcp listener
        // answers, prefer it over the globally arbitrated named pipe.
        var guestIp = ProbeTcpTransport(id);

        return Task.FromResult<SandboxRuntimeInfo?>(new SandboxRuntimeInfo
        {
            SandboxId = id,
            VmId = id.Value,
            Transport = guestIp is not null ? RdpTransportKind.Tcp : RdpTransportKind.NamedPipe,
            IpAddress = guestIp ?? string.Empty,
            IpAddresses = guestIp is not null ? [guestIp] : Array.Empty<string>(),

            // A connecting process that did not start the sandbox (a separate CLI
            // `connect`, the app after a restart) still needs the credential, or the
            // RDP session stops at the guest logon screen. It is deterministic from
            // the sandbox id, so it can be recomputed here.
            UserName = DefaultGuestUser,
            Password = DefaultGuestPassword(id),
        });
    }

    /// <summary>
    /// Reclaims the instance's on-disk footprint: the storage directory (the
    /// differencing child, the VMGS and the crash-dump placeholder) and its
    /// snapshots directory. The instance is expected to be stopped (the storage
    /// is in use otherwise); <see cref="StopAsync"/> only returns once the
    /// compute system is gone, so by this point vmwp has released its handles.
    /// <para>
    /// The storage delete retries on lock/IO errors (teardown is asynchronous
    /// and the last handle release can lag the state change) and, when the
    /// directory still cannot be removed, throws a
    /// <see cref="SandboxBackendException"/> so the failure reaches the caller
    /// instead of leaving a half-deleted sandbox behind silently. The snapshots
    /// directory under the platform tree stays best-effort.
    /// </para>
    /// </summary>
    public async Task DeleteDataAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var key = id.ToString();

        await Task.Run(
            () => DeleteStorageWithRetry(_layout.GetStorageDirectory(key), key, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        DeleteDirectoryQuietly(Path.Combine(_layout.SnapshotsFolder, key), key, "snapshots");
    }

    private void DeleteStorageWithRetry(string directory, string id, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!Directory.Exists(directory))
            {
                return;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                _logger.LogInformation("Deleted storage directory '{Directory}' for sandbox {SandboxId}.", directory, id);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt >= DeleteMaxAttempts)
                {
                    throw new SandboxBackendException(
                        $"Failed to delete the storage directory '{directory}' for sandbox {id} " +
                        $"after {attempt} attempts; a file in it is still in use. {ex.Message}",
                        ex.HResult,
                        ex);
                }

                _logger.LogDebug(
                    ex,
                    "Deleting storage directory '{Directory}' for sandbox {SandboxId} failed (attempt {Attempt}/{MaxAttempts}); retrying.",
                    directory, id, attempt, DeleteMaxAttempts);
                Thread.Sleep(DeleteRetryDelay);
            }
        }
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

        var rdpAccessSids = ResolveRdpAccessSids();
        var definition = new HcsSandboxDefinition
        {
            Id = id.ToString(),
            BaseLayerPath = baseLayer,
            StorageDiskPath = storage.DiskPath,
            GuestStatePath = storage.GuestStatePath,
            RdpNamedPipe = $@"\\.\pipe\{id}",
            RdpAccessSids = rdpAccessSids,
            AdditionalHvSocketServices = HcsSandboxConstants.PlatformAdditionalHvSocketServices(
                rdpAccessSids.FirstOrDefault()),
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
