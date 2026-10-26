using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engine.WindowsContainer;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Json.Container;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Network;
using Wsm.Hcs.Process;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Xunit;

namespace Wsm.Tests.Engines;

[Collection("Desktop key environment")]
public sealed class WindowsContainerEngineLifecycleTests
{
    [Fact]
    public async Task Recipe_interactive_session_derives_the_image_password_and_restarts_once()
    {
        using var fixture = new EngineFixture(new ContainerDesktopOptions());
        var expected = ContainerDesktopSessionDefaults.GetPassword(fixture.LayerPath);
        EngineFixture.AllowCurrentUser(fixture.KeyPath);

        await fixture.Engine.StartAsync(fixture.Request);

        var expectedPasswordCommand = $"net.exe user \"Administrator\" \"{expected}\"";
        var expectedRegistryCommand = WindowsContainerEngine.RegAdd(
            @"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon",
            "DefaultPassword",
            expected);
        Assert.True(
            fixture.Process.Commands.Exists(command => command == expectedPasswordCommand),
            "A recipe-requested interactive session must set the derived image password.");
        Assert.True(
            fixture.Process.Commands.Exists(command => command == expectedRegistryCommand),
            "The same derived password must be written for auto-logon.");
        Assert.Equal(2, fixture.Hcs.CreateCalls);
        Assert.Equal(2, fixture.Hcs.StartCalls);
        Assert.True(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task Recipe_interactive_session_keeps_an_explicit_password()
    {
        const string explicitPassword = "test-only-engine-password";
        using var fixture = new EngineFixture(new ContainerDesktopOptions { Password = explicitPassword });

        await fixture.Engine.StartAsync(fixture.Request);

        var expected = $"net.exe user \"Administrator\" \"{explicitPassword}\"";
        Assert.True(fixture.Process.Commands.Exists(command => command == expected));
        Assert.False(File.Exists(fixture.KeyPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Second_boot_failure_clears_the_marker_and_allows_a_full_retry(bool failStart)
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" });
        if (failStart)
        {
            fixture.Hcs.FailStartAt = 2;
        }
        else
        {
            fixture.Hcs.FailCreateAt = 2;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.StartAsync(fixture.Request));
        var markerWasWritten = File.Exists(fixture.MarkerPath);
        fixture.Hcs.FailCreateAt = null;
        fixture.Hcs.FailStartAt = null;

        await fixture.Engine.StartAsync(fixture.Request);

        Assert.False(markerWasWritten);
        Assert.Equal(4, fixture.Hcs.CreateCalls);
        Assert.Equal(failStart ? 4 : 3, fixture.Hcs.StartCalls);
        Assert.True(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task Second_start_failure_releases_the_handle_scratch_and_network()
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" });
        fixture.Hcs.FailStartAt = 2;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.StartAsync(fixture.Request));

        Assert.Equal(2, fixture.Hcs.Handles.Count);
        Assert.True(fixture.Hcs.Handles.All(handle => handle.Disposed));
        Assert.True(fixture.Layers.Unprepared.Count >= 2);
        Assert.True(fixture.Layers.Deactivated.Count >= 2);
        Assert.Contains("endpoint", fixture.Network.DeletedEndpoints);
        Assert.Contains("namespace", fixture.Network.DeletedNamespaces);
        Assert.Null(await fixture.Engine.GetRuntimeAsync(fixture.Id));
    }

    [Fact]
    public async Task Initial_start_failure_releases_the_first_handle_and_cleans_up()
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" });
        fixture.Hcs.FailStartAt = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Engine.StartAsync(fixture.Request));

        Assert.Single(fixture.Hcs.Handles);
        Assert.True(fixture.Hcs.Handles[0].Disposed);
        Assert.Contains("endpoint", fixture.Network.DeletedEndpoints);
        Assert.Contains("namespace", fixture.Network.DeletedNamespaces);
        Assert.Null(await fixture.Engine.GetRuntimeAsync(fixture.Id));
    }

    [Fact]
    public async Task Failed_desktop_initialization_clears_the_marker_before_releasing_scratch()
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" });
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.MarkerPath)!);
        await File.WriteAllTextAsync(fixture.MarkerPath, "pending");
        fixture.Process.ExitCodeFor = command => command.Contains("/v Shell ") ? 1 : null;
        fixture.Layers.MarkerExists = () => File.Exists(fixture.MarkerPath);

        await Assert.ThrowsAsync<SandboxBackendException>(() => fixture.Engine.StartAsync(fixture.Request));

        Assert.False(fixture.Layers.MarkerStates[^1]);
        Assert.False(File.Exists(fixture.MarkerPath));
    }

    [Fact]
    public async Task Experimental_rdp_probe_failures_remain_best_effort()
    {
        using var fixture = new EngineFixture(new ContainerDesktopOptions
        {
            Password = "test-only-desktop-password",
            AttemptTcpRdp = true,
        });
        fixture.Network.ThrowOnGetIpAddresses = true;

        var runtime = await fixture.Engine.StartAsync(fixture.Request);

        Assert.Equal(fixture.Id, runtime.SandboxId);
        Assert.Equal(2, fixture.Hcs.StartCalls);
    }

    [Fact]
    public async Task Marker_is_written_after_second_boot_and_its_initialization()
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" },
            ["RpcSs"]);
        fixture.Hcs.MarkerExists = () => File.Exists(fixture.MarkerPath);
        fixture.Process.MarkerExists = () => File.Exists(fixture.MarkerPath);

        await fixture.Engine.StartAsync(fixture.Request);

        Assert.Equal(2, fixture.Hcs.MarkerStates.Count);
        Assert.False(fixture.Hcs.MarkerStates[^1]);
        Assert.False(fixture.Process.MarkerStates[^1]);
        Assert.True(File.Exists(fixture.MarkerPath));
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("active", false)]
    [InlineData("shell", false)]
    [InlineData("autologon", false)]
    [InlineData("password", true)]
    [InlineData("shell", true)]
    public async Task Required_desktop_command_failure_fails_start_and_cleans_up(
        string commandKind, bool throws)
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" });
        Func<string, bool> matches = commandKind switch
        {
            "password" => command => command.StartsWith("net.exe user ", StringComparison.OrdinalIgnoreCase)
                && !command.Contains("/ACTIVE:", StringComparison.OrdinalIgnoreCase),
            "active" => command => command.Contains("/ACTIVE:YES", StringComparison.OrdinalIgnoreCase),
            "shell" => command => command.Contains("/v Shell ", StringComparison.OrdinalIgnoreCase),
            "autologon" => command => command.Contains("/v AutoAdminLogon ", StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentOutOfRangeException(nameof(commandKind)),
        };
        if (throws)
        {
            fixture.Process.ThrowFor = matches;
        }
        else
        {
            fixture.Process.ExitCodeFor = command => matches(command) ? 23 : null;
        }

        await Assert.ThrowsAsync<SandboxBackendException>(() => fixture.Engine.StartAsync(fixture.Request));

        Assert.False(File.Exists(fixture.MarkerPath));
        Assert.True(fixture.Hcs.Handles.All(handle => handle.Disposed));
        Assert.Contains("endpoint", fixture.Network.DeletedEndpoints);
        Assert.Contains("namespace", fixture.Network.DeletedNamespaces);
        Assert.Null(await fixture.Engine.GetRuntimeAsync(fixture.Id));
    }

    [Fact]
    public async Task Optional_service_failures_remain_best_effort()
    {
        using var fixture = new EngineFixture(
            new ContainerDesktopOptions { Password = "test-only-desktop-password" },
            ["RpcSs", "ProfSvc"]);
        fixture.Process.ExitCodeFor = command => command == "sc.exe start RpcSs" ? 1 : null;
        fixture.Process.ThrowFor = command => command == "sc.exe start ProfSvc";

        var runtime = await fixture.Engine.StartAsync(fixture.Request);

        Assert.Equal(fixture.Id, runtime.SandboxId);
        Assert.Equal(2, fixture.Hcs.StartCalls);
    }

    [Fact]
    public async Task Experimental_rdp_failures_remain_best_effort()
    {
        using var fixture = new EngineFixture(new ContainerDesktopOptions
        {
            Password = "test-only-desktop-password",
            AttemptTcpRdp = true,
        });
        fixture.Process.ThrowFor = command =>
            command.Contains(@"Control\Terminal Server", StringComparison.OrdinalIgnoreCase)
            || command.Contains("TermService", StringComparison.OrdinalIgnoreCase);

        var runtime = await fixture.Engine.StartAsync(fixture.Request);

        Assert.Equal(fixture.Id, runtime.SandboxId);
        Assert.Equal(2, fixture.Hcs.StartCalls);
    }

    private sealed class EngineFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "wsm-container-engine-" + Guid.NewGuid().ToString("N"));
        private readonly string? _originalKeyOverride;

        public EngineFixture(
            ContainerDesktopOptions desktop, IReadOnlyList<string>? requiredServices = null)
        {
            LayerPath = Path.Combine(_root, "image");
            Directory.CreateDirectory(Path.Combine(LayerPath, "Files", "Windows"));
            Directory.CreateDirectory(Path.Combine(LayerPath, "Hives"));
            new ContainerImageMetadata { Build = 26_100, IsServer = true }.Write(LayerPath);
            KeyPath = Path.Combine(_root, "secrets", "container-desktop.key");
            _originalKeyOverride = Environment.GetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY");
            Environment.SetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY", KeyPath);

            Hcs = new FakeHcsClient();
            Process = new FakeProcessExecutor();
            Layers = new FakeLayerService();
            Network = new FakeNetworkService();
            var options = Options.Create(new WindowsContainerEngineOptions
            {
                Desktop = desktop,
                RequiredServices = requiredServices?.ToList() ?? [],
            });
            Engine = new WindowsContainerEngine(
                Hcs,
                new FakeLayout(_root),
                new FakeJsonBuilder(),
                Process,
                Layers,
                Network,
                new FakeElevation(),
                new FakeIsolationPolicy(),
                options,
                NullLogger<WindowsContainerEngine>.Instance);

            Id = SandboxInstanceId.New();
            Request = new SandboxStartRequest
            {
                Id = Id,
                Recipe = SandboxRecipe.Default with
                {
                    ContainerImagePath = LayerPath,
                    Desktop = SandboxDesktopPlan.Default with { InteractiveSession = true },
                },
            };
            MarkerPath = Path.Combine(_root, "storage", "scratch", "wsm", ".desktop-password-armed");
        }

        public WindowsContainerEngine Engine { get; }

        public FakeHcsClient Hcs { get; }

        public FakeProcessExecutor Process { get; }

        public FakeLayerService Layers { get; }

        public FakeNetworkService Network { get; }

        public SandboxInstanceId Id { get; }

        public SandboxStartRequest Request { get; }

        public string LayerPath { get; }

        public string KeyPath { get; }

        public string MarkerPath { get; }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("WSM_CONTAINER_DESKTOP_KEY", _originalKeyOverride);
            try
            {
                AllowCurrentUser(KeyPath);
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
            catch
            {
            }
        }

        internal static void AllowCurrentUser(string keyPath)
        {
            var directory = Path.GetDirectoryName(keyPath)!;
            var current = WindowsIdentity.GetCurrent().User!;
            if (Directory.Exists(directory))
            {
                var security = new DirectorySecurity();
                security.AddAccessRule(new FileSystemAccessRule(
                    current,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                new DirectoryInfo(directory).SetAccessControl(security);
            }

            if (File.Exists(keyPath))
            {
                var security = new FileSecurity();
                security.AddAccessRule(new FileSystemAccessRule(
                    current, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(keyPath).SetAccessControl(security);
            }
        }
    }

    private sealed class TrackingHandle : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeHcsClient : IHcsClient
    {
        private bool _exists;

        public int CreateCalls { get; private set; }

        public int StartCalls { get; private set; }

        public int? FailCreateAt { get; set; }

        public int? FailStartAt { get; set; }

        public List<TrackingHandle> Handles { get; } = [];

        public Func<bool>? MarkerExists { get; set; }

        public List<bool> MarkerStates { get; } = [];

        public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null) =>
            _exists
                ? [new HcsComputeSystemInfo { Id = "container", SystemType = "Container", State = "Running" }]
                : [];

        public HcsComputeSystemInfo? Get(string id) =>
            _exists ? new HcsComputeSystemInfo { Id = id, SystemType = "Container", State = "Running" } : null;

        public IDisposable Create(string id, string configuration)
        {
            CreateCalls++;
            if (FailCreateAt == CreateCalls)
            {
                throw new InvalidOperationException("Second-boot create failure requested by the test.");
            }

            _exists = true;
            var handle = new TrackingHandle();
            Handles.Add(handle);
            return handle;
        }

        public IDisposable Open(string id) => throw new NotSupportedException();

        public void Start(string id)
        {
            StartCalls++;
            MarkerStates.Add(MarkerExists?.Invoke() ?? false);
            if (FailStartAt == StartCalls)
            {
                throw new InvalidOperationException("Second-boot start failure requested by the test.");
            }
        }

        public void Shutdown(string id, uint timeoutMs = 45_000)
        {
        }

        public void Terminate(string id) => _exists = false;

        public string? Save(string id, string options) => null;

        public void CreateEmptyGuestStateFile(string path)
        {
        }

        public void CreateEmptyRuntimeStateFile(string path)
        {
        }

        public void GrantVmAccess(string id, string path)
        {
        }

        public HcsProcessResult RunProcess(
            string id, string commandLine, string? user = null, uint timeoutMs = 120_000) =>
            throw new NotSupportedException();
    }

    private sealed class FakeProcessExecutor : IComputeProcessExecutor
    {
        public List<string> Commands { get; } = [];

        public Func<string, int?>? ExitCodeFor { get; set; }

        public Func<string, bool>? ThrowFor { get; set; }

        public Func<bool>? MarkerExists { get; set; }

        public List<bool> MarkerStates { get; } = [];

        public Task<int> ExecuteAsync(
            string computeSystemId,
            string commandLine,
            string? workingDirectory = null,
            CancellationToken ct = default)
        {
            Commands.Add(commandLine);
            MarkerStates.Add(MarkerExists?.Invoke() ?? false);
            if (ThrowFor?.Invoke(commandLine) == true)
            {
                throw new InvalidOperationException("Process failure requested by the test.");
            }

            return Task.FromResult(ExitCodeFor?.Invoke(commandLine) ?? 0);
        }
    }

    private sealed class FakeLayerService : IWcowLayerService
    {
        public List<string> Unprepared { get; } = [];

        public List<string> Deactivated { get; } = [];

        public Func<bool>? MarkerExists { get; set; }

        public List<bool> MarkerStates { get; } = [];

        public Guid GetLayerId(string layerPath) => Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        public void CreateScratchLayer(string scratchPath, IReadOnlyList<string> parentLayerPaths)
        {
        }

        public void Activate(string layerPath)
        {
        }

        public void Prepare(string layerPath, IReadOnlyList<string> parentLayerPaths) =>
            Directory.CreateDirectory(Path.Combine(layerPath, "wsm"));

        public void Unprepare(string layerPath)
        {
            Unprepared.Add(layerPath);
            MarkerStates.Add(MarkerExists?.Invoke() ?? false);
        }

        public void Deactivate(string layerPath) => Deactivated.Add(layerPath);

        public string GetMountPath(string layerPath) => layerPath;

        public void Destroy(string layerPath)
        {
        }

        public void ImportLayer(string layerPath, string importFolderPath, IReadOnlyList<string> parentLayerPaths)
        {
        }

        public void ProcessBaseImage(string layerPath)
        {
        }
    }

    private sealed class FakeNetworkService : IHnsNetworkService
    {
        public List<string> DeletedEndpoints { get; } = [];

        public List<string> DeletedNamespaces { get; } = [];

        public bool ThrowOnGetIpAddresses { get; set; }

        public Task<string> GetOrCreateNatNetworkIdAsync(CancellationToken ct = default) =>
            Task.FromResult("network");

        public Task<string> GetOrCreateContainerNetworkIdAsync(CancellationToken ct = default) =>
            Task.FromResult("network");

        public Task<string> CreateNatEndpointAsync(string networkId, CancellationToken ct = default) =>
            Task.FromResult("endpoint");

        public Task<string> CreateNatEndpointAsync(
            string networkId, string name, CancellationToken ct = default) => Task.FromResult("endpoint");

        public Task DeleteEndpointAsync(string endpointId, CancellationToken ct = default)
        {
            DeletedEndpoints.Add(endpointId);
            return Task.CompletedTask;
        }

        public Task DeleteEndpointByNameAsync(string name, CancellationToken ct = default) => Task.CompletedTask;

        public Task<HnsEndpoint?> GetEndpointByNameAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<HnsEndpoint?>(null);

        public Task<string> CreateNamespaceAsync(CancellationToken ct = default) => Task.FromResult("namespace");

        public Task AddEndpointToNamespaceAsync(
            string namespaceId, string endpointId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteNamespaceAsync(string namespaceId, CancellationToken ct = default)
        {
            DeletedNamespaces.Add(namespaceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetIpAddressesAsync(
            string computeSystemId, CancellationToken ct = default)
        {
            if (ThrowOnGetIpAddresses)
            {
                throw new InvalidOperationException("IP probe failure requested by the test.");
            }

            return Task.FromResult<IReadOnlyList<string>>([]);
        }
    }

    private sealed class FakeLayout(string root) : IHcsHostLayout
    {
        public string PlatformContainersFolder => root;

        public string LayersFolder => root;

        public string BaseImagesFolder => root;

        public string PlatformContainerStoragesFolder => root;

        public string SnapshotsFolder => root;

        public string SandboxDataFolder => root;

        public string GetStorageDirectory(string id) => Path.Combine(root, "storage");

        public IReadOnlyList<HcsLayerInfo> ListLayers() => [];

        public string? ResolveBaseLayerPath() => null;

        public HcsLayerInfo? ResolveBaseImageLayer() => null;
    }

    private sealed class FakeJsonBuilder : IContainerJsonBuilder
    {
        public string Build(ContainerDefinition definition) => "{}";
    }

    private sealed class FakeElevation : IElevationService
    {
        public bool IsElevated => true;

        public bool TryRelaunchElevated(string? arguments = null) => false;
    }

    private sealed class FakeIsolationPolicy : IContainerIsolationPolicy
    {
        public ContainerIsolationSupport Evaluate() => Evaluate(null);

        public ContainerIsolationSupport Evaluate(string? imagePath) => new()
        {
            IsSupported = true,
            ReasonKey = "Supported",
            HostBuild = 26_100,
            HostIsWindows11Client = true,
            ImagePath = imagePath,
        };

        public void Invalidate()
        {
        }
    }
}
