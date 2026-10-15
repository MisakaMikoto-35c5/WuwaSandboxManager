using Microsoft.Extensions.Logging.Abstractions;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engine.Hcs;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Checkpoint;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Sandbox;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Unit tests for <see cref="HcsSandboxEngine"/>'s compute-system handle
/// lifetime (M1) and runtime reporting (N8), using fakes for the native HCS
/// surface.
/// </summary>
public sealed class HcsSandboxEngineTests
{
    [Fact]
    public async Task Start_holds_the_system_handle_until_stop()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out var checkpoints);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        // The handle must still be open after Start returns; the engine keeps
        // the compute system reachable for the instance's whole lifetime.
        Assert.NotNull(hcs.LastHandle);
        Assert.False(hcs.LastHandle!.Disposed);
        Assert.Contains(id.ToString(), hcs.Started);

        await engine.StopAsync(id);

        // Stopping requests a graceful shutdown first; the handle is released
        // only once the compute system is gone, and a system that shuts down
        // cleanly is never terminated.
        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.True(hcs.LastHandle.Disposed);
        Assert.Contains(id.ToString(), checkpoints.Released);
        Assert.DoesNotContain(id.ToString(), hcs.Terminated);
    }

    [Fact]
    public async Task Stop_escalates_to_terminate_when_the_shutdown_stalls()
    {
        var hcs = new FakeHcsClient { ShutdownStopsSystem = false };
        var engine = CreateEngine(hcs, out _);
        engine.ShutdownWaitTimeout = TimeSpan.FromMilliseconds(50);
        engine.StoppedPollTimeout = TimeSpan.FromMilliseconds(300);
        engine.StoppedPollInterval = TimeSpan.FromMilliseconds(20);
        engine.TerminatedPollTimeout = TimeSpan.FromMilliseconds(300);

        // A system this engine did not create (no live handle) still goes
        // through the deterministic sequence: shutdown, bounded wait, then
        // terminate.
        var id = SandboxInstanceId.New();
        await engine.StopAsync(id);

        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.Contains(id.ToString(), hcs.Terminated);
    }

    [Fact]
    public async Task Stop_tolerates_the_system_vanishing_before_terminate()
    {
        var hcs = new FakeHcsClient { ShutdownStopsSystem = false, TerminateThrowsNotFound = true };
        var engine = CreateEngine(hcs, out _);
        engine.ShutdownWaitTimeout = TimeSpan.FromMilliseconds(50);
        engine.StoppedPollTimeout = TimeSpan.FromMilliseconds(300);
        engine.StoppedPollInterval = TimeSpan.FromMilliseconds(20);

        // The system disappears between the stalled shutdown poll and the
        // terminate escalation; stop must treat that as success, not surface
        // the open-handle failure.
        var id = SandboxInstanceId.New();
        await engine.StopAsync(id);

        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
    }

    [Fact]
    public async Task GetRuntime_reports_the_started_connection_settings()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id, rail: true, fullScreen: true));

        var runtime = await engine.GetRuntimeAsync(id);

        Assert.NotNull(runtime);
        Assert.True(runtime!.RailMode);
        Assert.True(runtime.FullScreen);
        Assert.Equal(id, runtime.SandboxId);
    }

    [Fact]
    public async Task Start_grants_vm_access_to_every_storage_file_including_the_base_disk()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        var storage = Path.Combine(Path.GetTempPath(), "wsm-test-" + id);

        // The parent base copy was the file that was previously never granted;
        // under the per-user Documents location its absence made Start fail
        // with 0xC03A0016.
        Assert.Contains(Path.Combine(storage, "sandbox.base.vhdx"), hcs.Granted);
        Assert.Contains(Path.Combine(storage, "sandbox.vhdx"), hcs.Granted);
        Assert.Contains(Path.Combine(storage, "sandbox.vmgs"), hcs.Granted);

        // The crash-dump placeholder is covered by directory enumeration.
        Assert.Contains(Path.Combine(storage, id + ".dmp"), hcs.Granted);

        // The directory itself is granted best-effort.
        Assert.Contains(storage, hcs.Granted);
    }

    [Fact]
    public async Task Start_surfaces_a_failed_storage_grant_and_releases_the_handle()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var baseDisk = Path.Combine(Path.GetTempPath(), "wsm-test-" + id, "sandbox.base.vhdx");
        hcs.FailGrantFor = path => string.Equals(path, baseDisk, StringComparison.OrdinalIgnoreCase);

        var ex = await Assert.ThrowsAsync<SandboxBackendException>(() => engine.StartAsync(Request(id)));

        Assert.Contains("sandbox.base.vhdx", ex.Message);
        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);
        Assert.DoesNotContain(id.ToString(), hcs.Started);
    }

    [Fact]
    public async Task Start_provisions_the_guest_account_and_reports_the_credential()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var runtime = await engine.StartAsync(Request(id));

        // The account is activated with a deterministic password (derived from the
        // sandbox id) and the credential travels with the runtime: the RDP client
        // must supply it or the WDAG guest stays at its logon screen (black desktop).
        Assert.Equal("WDAGUtilityAccount", runtime.UserName);
        Assert.False(string.IsNullOrEmpty(runtime.Password));
        Assert.Contains(hcs.Processes, command => command.StartsWith("net user WDAGUtilityAccount "));
        Assert.Contains(hcs.Processes, command => command == "net user WDAGUtilityAccount /active:yes");
    }

    [Fact]
    public async Task Start_uses_the_recipe_account_password_when_supplied()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var request = Request(id) with
        {
            Recipe = SandboxRecipe.Default with { AccountPassword = "Sup3rSecret!" },
        };

        var runtime = await engine.StartAsync(request);

        Assert.Equal("Sup3rSecret!", runtime.Password);
        Assert.Contains(hcs.Processes, command => command.Contains("Sup3rSecret!"));
    }

    [Fact]
    public async Task Start_adopts_an_already_running_system_instead_of_faulting()
    {
        var hcs = new FakeHcsClient { CreateThrows = true };
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var runtime = await engine.StartAsync(Request(id));

        // A running system (e.g. held by `wsm host` in another process) is
        // adopted rather than reported as "already exists": no start, no guest
        // re-provisioning, and the caller still gets a usable runtime with the
        // credential re-applied (it is never persisted anywhere).
        Assert.Contains(id.ToString(), hcs.Opened);
        Assert.Empty(hcs.Started);
        Assert.Equal("WDAGUtilityAccount", runtime.UserName);
        Assert.False(string.IsNullOrEmpty(runtime.Password));
        Assert.Contains(hcs.Processes, command => command.StartsWith("net user WDAGUtilityAccount "));
        Assert.NotNull(hcs.LastHandle);
        Assert.False(hcs.LastHandle!.Disposed);

        // Stopping the adopted system uses the same deterministic sequence:
        // shutdown first (the system goes away), the handle released last, and
        // no terminate when the graceful path works.
        await engine.StopAsync(id);
        Assert.True(hcs.LastHandle.Disposed);
        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.DoesNotContain(id.ToString(), hcs.Terminated);
    }

    [Fact]
    public async Task Start_attaches_an_hns_endpoint_and_stop_removes_it()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        // The endpoint name is deterministic so Stop (and later processes) can
        // find it again; a stale endpoint from an earlier run is deleted first.
        var expectedName = $"wsm-{id.Value:N}";
        Assert.Contains(expectedName, network.CreatedEndpointNames);
        Assert.Contains(expectedName, network.DeletedEndpointNames);

        await engine.StopAsync(id);

        // Stop created no new endpoint, but deleted ours once more (the create
        // path's stale cleanup and the stop cleanup both go by name).
        Assert.Equal(2, network.DeletedEndpointNames.Count(name => name == expectedName));
    }

    [Fact]
    public async Task Start_without_networking_attaches_no_endpoint()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();
        var request = Request(id) with
        {
            Recipe = SandboxRecipe.Default with { Networking = NetworkingMode.Disabled },
        };

        await engine.StartAsync(request);

        Assert.Empty(network.CreatedEndpointNames);
    }

    [Fact]
    public async Task Start_with_pipe_transport_makes_no_guest_tcp_changes()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();
        var runtime = await engine.StartAsync(Request(id));

        // The named pipe is the default transport: the guest's RDP stack is
        // left untouched (no container-mode flip, no RDP-Tcp rewrites, no
        // firewall rule, no TermService restart — not even an ipconfig probe)
        // and the runtime reports the pipe. (The account provisioning always
        // writes one marker value under Terminal Server; that is not the TCP
        // enablement.)
        Assert.Equal(RdpTransportKind.NamedPipe, runtime.Transport);
        Assert.Equal(string.Empty, runtime.IpAddress);
        Assert.DoesNotContain(hcs.Processes, command => command.Contains("ipconfig"));
        // No TCP enablement writes (a read-only ContainerMode *query* — the
        // stale-leftover detection — is expected and allowed).
        Assert.DoesNotContain(hcs.Processes,
            command => command.Contains("reg add") && command.Contains("ContainerMode"));
        Assert.DoesNotContain(hcs.Processes, command => command.Contains(@"WinStations\RDP-Tcp"));
        Assert.DoesNotContain(hcs.Processes, command => command.Contains("netsh advfirewall"));
        Assert.DoesNotContain(hcs.Processes, command => command.Contains("termservice"));

        // The NIC is still attached: guest networking (internet access) is
        // governed by the Networking setting, not by the RDP transport.
        Assert.NotEmpty(network.CreatedEndpointNames);
    }

    [Fact]
    public async Task Start_with_pipe_transport_reverts_a_stale_guest_tcp_configuration()
    {
        var hcs = new FakeHcsClient
        {
            // A guest that once ran TCP: ContainerMode=0 persists on the
            // user-data disk, and it stalls any named-pipe session at Conn
            // (the pipe listener and the full RDP stack coexist).
            StandardOutputFor = command => command.Contains("reg query") && command.Contains("ContainerMode")
                ? "    ContainerMode    REG_DWORD    0x0\r\n"
                : null,
        };
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();
        var runtime = await engine.StartAsync(Request(id));

        // The leftover is reverted: back to container mode, the firewall rule
        // dropped, and TermService restarted so the flip takes effect.
        Assert.Equal(RdpTransportKind.NamedPipe, runtime.Transport);
        Assert.Contains(hcs.Processes, command =>
            command.Contains("reg add") && command.Contains("ContainerMode") && command.Contains("/d 1"));
        Assert.Contains(hcs.Processes, command => command.Contains("firewall delete rule name=WsmRdp3389"));
        Assert.Contains(hcs.Processes, command => command.Contains("net stop termservice"));
        Assert.Contains(hcs.Processes, command => command.Contains("net start termservice"));

        // The TCP enablement (ContainerMode=0) never runs in pipe mode.
        Assert.DoesNotContain(hcs.Processes, command =>
            command.Contains("reg add") && command.Contains("ContainerMode") && command.Contains("/d 0"));
    }

    [Fact]
    public async Task Start_with_pipe_transport_cleans_up_when_the_stale_revert_throws()
    {
        var hcs = new FakeHcsClient
        {
            StandardOutputFor = command => command.Contains("reg query") && command.Contains("ContainerMode")
                ? "    ContainerMode    REG_DWORD    0x0\r\n"
                : null,
            // The guest dies mid-revert: the failure must run the same cleanup
            // as the TCP branch (stop, handle, endpoint) before escaping.
            FailRunProcessFor = command => command.Contains("net stop termservice"),
        };
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StartAsync(Request(id)));

        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);

        var expectedName = $"wsm-{id.Value:N}";
        Assert.Equal(2, network.DeletedEndpointNames.Count(name => name == expectedName));
    }

    [Fact]
    public async Task Start_with_tcp_transport_fails_when_the_guest_listener_never_comes_up()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();

        // TCP was explicitly requested but the fake guest never gets an IP
        // (no ipconfig answer): the start must fail loudly instead of silently
        // falling back to the pipe the user did not ask for.
        await Assert.ThrowsAsync<SandboxBackendException>(
            () => engine.StartAsync(Request(id, transport: RdpTransportKind.Tcp)));

        // The guest booted before TCP failed: cleanup is the deterministic stop
        // (shutdown), then the handle, then the endpoint — dropping the handle
        // alone would leave the VM running with its NIC detached.
        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);

        // The stale-endpoint delete at create plus the failure cleanup.
        var expectedName = $"wsm-{id.Value:N}";
        Assert.Equal(2, network.DeletedEndpointNames.Count(name => name == expectedName));
    }

    [Fact]
    public async Task Start_with_tcp_transport_cleans_up_when_guest_setup_throws()
    {
        var hcs = new FakeHcsClient
        {
            // Reach the registry-rewrite stage: DHCP answered…
            StandardOutputFor = command => command.Contains("ipconfig")
                ? "   IPv4 Address. . . . . . . . . . . : 172.21.95.199\r\n"
                : null,
            // …then the guest died mid-setup.
            FailRunProcessFor = command => command.Contains("ContainerMode"),
        };
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();

        // An exception out of the TCP setup (not a null return) must still run
        // the full cleanup before the error escapes.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => engine.StartAsync(Request(id, transport: RdpTransportKind.Tcp)));

        Assert.Contains(id.ToString(), hcs.ShutdownRequested);
        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);

        var expectedName = $"wsm-{id.Value:N}";
        Assert.Equal(2, network.DeletedEndpointNames.Count(name => name == expectedName));
    }

    [Fact]
    public async Task Start_with_tcp_transport_and_networking_disabled_fails_fast()
    {
        var hcs = new FakeHcsClient();
        var engine = CreateEngine(hcs, out _, out var network);

        var id = SandboxInstanceId.New();
        var request = Request(id) with
        {
            Recipe = SandboxRecipe.Default with
            {
                Networking = NetworkingMode.Disabled,
                Transport = RdpTransportKind.Tcp,
            },
        };

        // Contradictory configuration is rejected before anything is created.
        await Assert.ThrowsAsync<SandboxValidationException>(() => engine.StartAsync(request));

        Assert.Empty(network.CreatedEndpointNames);
        Assert.Empty(hcs.Started);
    }

    [Fact]
    public async Task Adopted_system_with_tcp_transport_fails_when_no_listener()
    {
        var hcs = new FakeHcsClient { CreateThrows = true };
        var engine = CreateEngine(hcs, out _);

        var id = SandboxInstanceId.New();

        // The running system was started without TCP (e.g. by another process
        // in pipe mode): adopting it cannot satisfy the requested transport.
        await Assert.ThrowsAsync<SandboxBackendException>(
            () => engine.StartAsync(Request(id, transport: RdpTransportKind.Tcp)));

        Assert.NotNull(hcs.LastHandle);
        Assert.True(hcs.LastHandle!.Disposed);
    }

    [Fact]
    public async Task Stop_throws_and_keeps_the_endpoint_when_the_system_survives_terminate()
    {
        var hcs = new FakeHcsClient { ShutdownStopsSystem = false, TerminateStopsSystem = false };
        var engine = CreateEngine(hcs, out _, out var network);
        engine.ShutdownWaitTimeout = TimeSpan.FromMilliseconds(50);
        engine.StoppedPollTimeout = TimeSpan.FromMilliseconds(100);
        engine.StoppedPollInterval = TimeSpan.FromMilliseconds(20);
        engine.TerminatedPollTimeout = TimeSpan.FromMilliseconds(100);

        var id = SandboxInstanceId.New();
        await engine.StartAsync(Request(id));

        // A VM that survives every escalation must NOT be reported as stopped:
        // the throw keeps the manager from marking (and persisting) a live VM
        // as stopped, which hid Stop/Disconnect in the UI.
        await Assert.ThrowsAsync<SandboxBackendException>(() => engine.StopAsync(id));

        Assert.Contains(id.ToString(), hcs.Terminated);

        // The endpoint belongs to the still-running VM; deleting it would
        // detach the NIC of a system that is still alive. The single recorded
        // delete is the create path's stale-endpoint cleanup.
        Assert.Equal(1, network.DeletedEndpointNames.Count(name => name == $"wsm-{id.Value:N}"));
    }

    [Fact]
    public async Task GetRuntime_probes_tcp_with_the_endpoint_pinned_guest_ip()
    {
        // Process 1 adopts the running system; process 2 (a fresh engine over
        // the same fakes) probes it. The guest has two NICs and ipconfig order
        // varies between processes, so the probe must pin the answer to the
        // HNS endpoint's MAC instead of taking the first non-APIPA address.
        var hcs = new FakeHcsClient
        {
            CreateThrows = true,
            StandardOutputFor = command => command.Contains("ipconfig")
                ? "\r\nEthernet adapter Ethernet0:\r\n\r\n" +
                  "   Physical Address. . . . . . . . . . . : 00-15-5D-99-99-99\r\n" +
                  "   IPv4 Address. . . . . . . . . . . . : 172.21.93.72\r\n" +
                  "\r\nEthernet adapter Ethernet1:\r\n\r\n" +
                  "   Physical Address. . . . . . . . . . . : 00-15-5D-01-02-03\r\n" +
                  "   IPv4 Address. . . . . . . . . . . . : 172.21.95.238\r\n"
                : command.Contains("netstat")
                    ? "  TCP    0.0.0.0:3389           0.0.0.0:0              LISTENING       1234\r\n"
                    : null,
        };

        var engine1 = CreateEngine(hcs, out _, out _);
        var id = SandboxInstanceId.New();
        await engine1.StartAsync(Request(id));

        var engine2 = CreateEngine(hcs, out _, out var network2);
        network2.EndpointToReturn = new HnsEndpoint(
            "endpoint-test", $"wsm-{id.Value:N}", "00-15-5D-01-02-03", IpAddress: null);

        var runtime = await engine2.GetRuntimeAsync(id);

        Assert.NotNull(runtime);
        Assert.Equal(RdpTransportKind.Tcp, runtime!.Transport);
        Assert.Equal("172.21.95.238", runtime.IpAddress);
    }

    [Fact]
    public async Task GetRuntime_falls_back_to_the_first_guest_ip_without_an_endpoint()
    {
        var hcs = new FakeHcsClient
        {
            CreateThrows = true,
            StandardOutputFor = command => command.Contains("ipconfig")
                ? "   IPv4 Address. . . . . . . . . . . . : 172.21.95.238\r\n"
                : command.Contains("netstat")
                    ? "  TCP    0.0.0.0:3389           0.0.0.0:0              LISTENING       1234\r\n"
                    : null,
        };

        var engine1 = CreateEngine(hcs, out _, out _);
        var id = SandboxInstanceId.New();
        await engine1.StartAsync(Request(id));

        var engine2 = CreateEngine(hcs, out _, out _);

        var runtime = await engine2.GetRuntimeAsync(id);

        Assert.NotNull(runtime);
        Assert.Equal(RdpTransportKind.Tcp, runtime!.Transport);
        Assert.Equal("172.21.95.238", runtime.IpAddress);
    }

    [Fact]
    public async Task DeleteData_removes_storage_and_snapshot_directories()
    {
        var engine = CreateEngine(new FakeHcsClient(), out _);
        var id = SandboxInstanceId.New();
        var key = id.ToString();

        var storage = Path.Combine(Path.GetTempPath(), "wsm-test-" + key);
        var snapshots = Path.Combine(Path.GetTempPath(), key);
        Directory.CreateDirectory(storage);
        Directory.CreateDirectory(snapshots);
        await File.WriteAllTextAsync(Path.Combine(storage, "sandbox.vhdx"), "data");

        await engine.DeleteDataAsync(id);

        Assert.False(Directory.Exists(storage));
        Assert.False(Directory.Exists(snapshots));
    }

    [Fact]
    public async Task DeleteData_is_best_effort_when_nothing_exists()
    {
        var engine = CreateEngine(new FakeHcsClient(), out _);

        // Must not throw when the instance never wrote anything.
        await engine.DeleteDataAsync(SandboxInstanceId.New());
    }

    [Fact]
    public async Task DeleteData_retries_and_throws_when_the_storage_stays_locked()
    {
        var engine = CreateEngine(new FakeHcsClient(), out _);
        engine.DeleteMaxAttempts = 3;
        engine.DeleteRetryDelay = TimeSpan.FromMilliseconds(10);

        var id = SandboxInstanceId.New();
        var storage = Path.Combine(Path.GetTempPath(), "wsm-test-" + id);
        Directory.CreateDirectory(storage);
        var lockedFile = Path.Combine(storage, "sandbox.vhdx");
        await File.WriteAllTextAsync(lockedFile, "data");

        // An open handle with no sharing makes Directory.Delete fail the way a
        // lingering vmwp lock does; the failure must surface after the retries
        // instead of being swallowed.
        using (new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<SandboxBackendException>(() => engine.DeleteDataAsync(id));
        }

        Assert.True(Directory.Exists(storage));

        // Once the lock is gone the delete succeeds.
        await engine.DeleteDataAsync(id);
        Assert.False(Directory.Exists(storage));
    }

    private static HcsSandboxEngine CreateEngine(FakeHcsClient hcs, out FakeCheckpoints checkpoints)
        => CreateEngine(hcs, out checkpoints, out _);

    private static HcsSandboxEngine CreateEngine(
        FakeHcsClient hcs,
        out FakeCheckpoints checkpoints,
        out FakeHnsNetworkService network)
    {
        checkpoints = new FakeCheckpoints();
        network = new FakeHnsNetworkService();
        var engine = new HcsSandboxEngine(
            hcs,
            new FakeLayout(),
            new FakeJson(),
            new FakeStorage(),
            checkpoints,
            network,
            new FakeElevation(),
            NullLogger<HcsSandboxEngine>.Instance);

        // The fake guest never answers ipconfig; without this every Start
        // would burn the production DHCP budget (30s) before falling back to
        // the named pipe. The TermService settle delays are zeroed too so the
        // stale-TCP revert does not stall the suite.
        engine.DhcpWaitTimeout = TimeSpan.Zero;
        engine.ProbeDhcpWaitTimeout = TimeSpan.Zero;
        engine.TermServiceStopSettle = TimeSpan.Zero;
        engine.TermServiceStartSettle = TimeSpan.Zero;
        return engine;
    }

    private static SandboxStartRequest Request(
        SandboxInstanceId id,
        bool rail = false,
        bool fullScreen = false,
        RdpTransportKind transport = RdpTransportKind.NamedPipe)
        => new()
        {
            Id = id,
            Recipe = SandboxRecipe.Default with { RailMode = rail, FullScreen = fullScreen, Transport = transport },
        };

    private sealed class TrackingHandle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHcsClient : IHcsClient
    {
        public List<string> Started { get; } = new();

        public List<string> Terminated { get; } = new();

        /// <summary>Shutdown calls, in order. Also drives <see cref="Get"/>.</summary>
        public List<string> ShutdownRequested { get; } = new();

        /// <summary>
        /// When false, a shutdown does not make the system disappear, so the
        /// engine's poll times out and escalates to terminate.
        /// </summary>
        public bool ShutdownStopsSystem { get; set; } = true;

        /// <summary>
        /// When false, even a terminate does not make the system disappear:
        /// the VM survives every escalation and Stop must fail loudly.
        /// </summary>
        public bool TerminateStopsSystem { get; set; } = true;

        public List<string> Granted { get; } = new();

        /// <summary>When set, a grant whose path matches throws.</summary>
        public Func<string, bool>? FailGrantFor { get; set; }

        public TrackingHandle? LastHandle { get; private set; }

        /// <summary>When set, <see cref="Create"/> throws "already exists".</summary>
        public bool CreateThrows { get; set; }

        public List<string> Opened { get; } = new();

        public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null)
            => Array.Empty<HcsComputeSystemInfo>();

        public HcsComputeSystemInfo? Get(string id)
        {
            // Unknown systems (never created/opened here, and never shut down
            // or terminated either) do not exist; the real client reports
            // "not found" for those.
            if (!_existing.Contains(id) && !ShutdownRequested.Contains(id) && !Terminated.Contains(id))
            {
                return null;
            }

            // Simulate the compute system being gone once it was shut down (or
            // terminated); otherwise it reports itself as running.
            if (TerminateStopsSystem && Terminated.Contains(id))
            {
                return null;
            }

            if (ShutdownStopsSystem && ShutdownRequested.Contains(id))
            {
                return null;
            }

            return new HcsComputeSystemInfo { Id = id, State = "Running" };
        }

        private readonly HashSet<string> _existing = new(StringComparer.OrdinalIgnoreCase);

        public IDisposable Create(string id, string configuration)
        {
            if (CreateThrows)
            {
                // "Already exists" implies the system exists.
                _existing.Add(id);
                throw new SandboxDomainException("A compute system with that id already exists (HcsCreateComputeSystem).");
            }

            _existing.Add(id);
            LastHandle = new TrackingHandle();
            return LastHandle;
        }

        public IDisposable Open(string id)
        {
            _existing.Add(id);
            Opened.Add(id);
            LastHandle = new TrackingHandle();
            return LastHandle;
        }

        public void Start(string id) => Started.Add(id);

        public void Shutdown(string id, uint timeoutMs = 45_000) => ShutdownRequested.Add(id);

        public void Terminate(string id)
        {
            if (TerminateThrowsNotFound)
            {
                throw new SandboxNotFoundException("The compute system was not found (HcsOpenComputeSystem).");
            }

            Terminated.Add(id);
        }

        /// <summary>
        /// When true, <see cref="Terminate"/> throws "not found": the system
        /// vanished between the shutdown poll and the terminate escalation.
        /// </summary>
        public bool TerminateThrowsNotFound { get; set; }

        public string? Save(string id, string options) => null;

        public void CreateEmptyGuestStateFile(string path)
        {
        }

        public void CreateEmptyRuntimeStateFile(string path)
        {
        }

        public void GrantVmAccess(string id, string path)
        {
            if (FailGrantFor?.Invoke(path) == true)
            {
                throw new InvalidOperationException($"Access denied for '{path}'.");
            }

            Granted.Add(path);
        }

        /// <summary>When set, supplies the stdout for a matching command (e.g. a DHCP answer).</summary>
        public Func<string, string?>? StandardOutputFor { get; set; }

        /// <summary>When set, a matching command throws (simulating a guest that died mid-setup).</summary>
        public Func<string, bool>? FailRunProcessFor { get; set; }

        public HcsProcessResult RunProcess(string id, string commandLine, string? user = null, uint timeoutMs = 120_000)
        {
            Processes.Add(commandLine);
            if (FailRunProcessFor?.Invoke(commandLine) == true)
            {
                throw new InvalidOperationException($"The guest rejected '{commandLine}'.");
            }

            return new HcsProcessResult(0, StandardOutputFor?.Invoke(commandLine) ?? string.Empty, string.Empty);
        }

        public List<string> Processes { get; } = new();
    }

    private sealed class FakeLayout : IHcsHostLayout
    {
        public string PlatformContainersFolder => Path.GetTempPath();

        public string LayersFolder => Path.GetTempPath();

        public string BaseImagesFolder => Path.GetTempPath();

        public string PlatformContainerStoragesFolder => Path.GetTempPath();

        public string SnapshotsFolder => Path.GetTempPath();

        public string SandboxDataFolder => Path.GetTempPath();

        public string GetStorageDirectory(string id) => Path.Combine(Path.GetTempPath(), "wsm-test-" + id);

        public IReadOnlyList<HcsLayerInfo> ListLayers() => Array.Empty<HcsLayerInfo>();

        public string? ResolveBaseLayerPath() => Path.GetTempPath();

        public HcsLayerInfo? ResolveBaseImageLayer() => null;
    }

    private sealed class FakeStorage : ISandboxStorageProvisioner
    {
        public StorageLayout Provision(string storageDirectory, uint diskSizeMb = 0)
        {
            Directory.CreateDirectory(storageDirectory);
            var disk = Path.Combine(storageDirectory, "sandbox.vhdx");
            var guestState = Path.Combine(storageDirectory, "sandbox.vmgs");
            var baseDisk = Path.Combine(storageDirectory, "sandbox.base.vhdx");
            File.WriteAllText(disk, "disk");
            File.WriteAllText(guestState, "vmgs");
            File.WriteAllText(baseDisk, "base");
            return new StorageLayout(storageDirectory, disk, guestState, baseDisk);
        }
    }

    private sealed class FakeJson : ISandboxVmJsonBuilder
    {
        public string Build(HcsSandboxDefinition definition) => "{}";
    }

    private sealed class FakeCheckpoints : ICheckpointService
    {
        public List<string> Released { get; } = new();

        public SandboxCheckpoint Save(string systemId, string name, SandboxCheckpointType type)
            => throw new NotSupportedException();

        public IReadOnlyList<SandboxCheckpoint> List(string systemId)
            => Array.Empty<SandboxCheckpoint>();

        public string Restore(HcsSandboxDefinition definition, SandboxCheckpoint checkpoint)
            => throw new NotSupportedException();

        public void Release(string systemId) => Released.Add(systemId);
    }

    private sealed class FakeElevation : IElevationService
    {
        public bool IsElevated => true;

        public bool TryRelaunchElevated(string? arguments = null) => false;
    }

    private sealed class FakeHnsNetworkService : IHnsNetworkService
    {
        public List<string> CreatedEndpointNames { get; } = new();

        public List<string> DeletedEndpointNames { get; } = new();

        public Task<string> GetOrCreateNatNetworkIdAsync(CancellationToken ct = default)
            => Task.FromResult("network-test");

        public Task<string> GetOrCreateContainerNetworkIdAsync(CancellationToken ct = default)
            => Task.FromResult("network-test");

        public Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default)
            => CreateNatEndpointAsync(networkId, "unnamed", ct);

        public Task<string> CreateNatEndpointAsync(string networkId, string name, CancellationToken ct = default)
        {
            CreatedEndpointNames.Add(name);
            return Task.FromResult("endpoint-test");
        }

        public Task DeleteEndpointAsync(string endpointId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteEndpointByNameAsync(string name, CancellationToken ct = default)
        {
            DeletedEndpointNames.Add(name);
            return Task.CompletedTask;
        }

        public Task<string> CreateNamespaceAsync(CancellationToken ct = default)
            => Task.FromResult("namespace-test");

        public Task AddEndpointToNamespaceAsync(string namespaceId, string endpointId, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task DeleteNamespaceAsync(string namespaceId, CancellationToken ct = default)
            => Task.CompletedTask;

        /// <summary>The endpoint <see cref="GetEndpointByNameAsync"/> returns; null when unset.</summary>
        public HnsEndpoint? EndpointToReturn { get; set; }

        public Task<HnsEndpoint?> GetEndpointByNameAsync(string name, CancellationToken ct = default)
            => Task.FromResult(EndpointToReturn);

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(string computeSystemId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
