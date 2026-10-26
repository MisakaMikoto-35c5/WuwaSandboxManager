using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Connection.ContainerDesktop;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// View model for a single sandbox instance: the lifecycle/connection state
/// machine, the editable definition fields (surfaced in the settings dialog) and
/// the folder-mapping, checkpoint and runtime commands. All driven by the new
/// <see cref="ISandboxManager"/> API.
/// </summary>
public sealed partial class SandboxInstanceViewModel : ObservableObject
{
    private static readonly Color RunningColor = Color.FromArgb(255, 0x3D, 0xD6, 0x8C);
    private static readonly Color WorkingColor = Color.FromArgb(255, 0xE0, 0xA9, 0x3B);
    private static readonly Color FaultColor = Color.FromArgb(255, 0xE0, 0x5B, 0x5B);
    private static readonly Color IdleColor = Color.FromArgb(255, 0x9A, 0xA0, 0xA6);
    private static readonly Color CreatedColor = Color.FromArgb(255, 0x4C, 0xC2, 0xFF);

    private readonly ISandboxManager _manager;
    private readonly MainViewModel _owner;
    private readonly IAppLocalizer _l;
    private SandboxRecipe _recipe = SandboxRecipe.Default;

    /// <summary>
    /// Set when the user explicitly disconnects: suppresses auto-connect and keeps
    /// the primary action on Connect until they ask for it again.
    /// </summary>
    private bool _userDisconnected;

    public SandboxInstanceViewModel(
        ISandboxManager manager,
        MainViewModel owner,
        SandboxInstanceDto dto,
        IAppLocalizer localizer)
    {
        _manager = manager;
        _owner = owner;
        _l = localizer;

        NetworkingOptions = Enum.GetValues<NetworkingMode>()
            .Select(mode => new EnumOption<NetworkingMode>(mode, LocalizeNetworking(mode)))
            .ToArray();
        VgpuOptions = Enum.GetValues<VirtualGpuMode>()
            .Select(mode => new EnumOption<VirtualGpuMode>(mode, LocalizeVgpu(mode)))
            .ToArray();
        TransportOptions = new[] { RdpTransportKind.NamedPipe, RdpTransportKind.Tcp }
            .Select(kind => new EnumOption<RdpTransportKind>(kind, LocalizeTransport(kind)))
            .ToArray();
        QuickLaunchPrograms = BuildQuickLaunchPrograms();

        Apply(dto);
    }

    public ObservableCollection<MappedFolderItem> MappedFolders { get; } = [];

    public ObservableCollection<LogonCommand> LogonCommands { get; } = [];

    public ObservableCollection<SandboxCheckpoint> Checkpoints { get; } = [];

    public IReadOnlyList<EnumOption<NetworkingMode>> NetworkingOptions { get; }

    public IReadOnlyList<EnumOption<VirtualGpuMode>> VgpuOptions { get; }

    /// <summary>The selectable RDP transports (the pipe is the default; TCP is opt-in).</summary>
    public IReadOnlyList<EnumOption<RdpTransportKind>> TransportOptions { get; }

    /// <summary>
    /// Common programs offered as one-click RAIL launches (File Explorer,
    /// Registry Editor, Command Prompt, …).
    /// </summary>
    public IReadOnlyList<QuickLaunchProgram> QuickLaunchPrograms { get; }

    [ObservableProperty] private Guid _id;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private double _memoryMb = 4096;

    [ObservableProperty] private double _cpuCores = 4;

    [ObservableProperty] private EnumOption<NetworkingMode>? _selectedNetworking;

    [ObservableProperty] private EnumOption<VirtualGpuMode>? _selectedVgpu;

    [ObservableProperty] private EnumOption<RdpTransportKind>? _selectedTransport;

    [ObservableProperty] private bool _railMode;

    /// <summary>
    /// The program a RAIL session launches, e.g. <c>C:\Windows\explorer.exe</c>.
    /// Only meaningful when <see cref="RailMode"/> is set.
    /// </summary>
    [ObservableProperty] private string _railProgram = SandboxRecipe.DefaultRailProgram;

    [ObservableProperty] private bool _fullScreen;

    [ObservableProperty] private bool _protectedClient;

    [ObservableProperty] private bool _rebootSupport;

    [ObservableProperty] private double _diskSizeMb;

    [ObservableProperty] private SandboxState _state;

    [ObservableProperty] private string _stateLabel = string.Empty;

    /// <summary>The engine pinned to this instance, or <c>null</c> for automatic selection.</summary>
    [ObservableProperty] private SandboxEngineKind? _engine;

    /// <summary>Human-readable engine label for the detail/settings surfaces.</summary>
    [ObservableProperty] private string _engineLabel = string.Empty;

    [ObservableProperty] private string _runtimeSummary = string.Empty;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _newFolderHost = string.Empty;

    [ObservableProperty] private bool _newFolderReadOnly = true;

    [ObservableProperty] private string _newLogonCommand = string.Empty;

    [ObservableProperty] private string _execCommandText = string.Empty;

    [ObservableProperty] private bool _checkpointAsTemplate;

    [ObservableProperty] private SandboxCheckpoint? _selectedCheckpoint;

    /// <summary>
    /// Path to this sandbox's manifest file. Left unset here; a follow-up pass
    /// defines the per-sandbox manifest and supplies the real path.
    /// </summary>
    [ObservableProperty] private string? _manifestPath;

    public string ShortId => Id.ToString()[..8];

    public bool IsLive => State.IsLive();

    public bool IsRunning => State == SandboxState.Running;

    public bool CanStart => !State.IsLive();

    public bool ShowStop => State.IsLive();

    /// <summary>Primary action is Start when idle, Connect when running.</summary>
    public bool IsPrimaryEnabled => State is not (SandboxState.Starting or SandboxState.Rebooting);

    /// <summary>True when this instance runs on the process-isolation (container) engine.</summary>
    public bool IsContainerEngine => Engine == SandboxEngineKind.WindowsContainer;

    /// <summary>
    /// Container desktop actions are offered for a running container: a container
    /// has no RDP listener, so its desktop only exists once the channel is started.
    /// </summary>
    public bool ShowContainerDesktop => IsContainerEngine && IsRunning;

    public string PrimaryActionLabel => IsRunning
        ? _l["PrimaryConnectButton.Content"]
        : _l["PrimaryStartButton.Content"];

    /// <summary>The instance's type: RAIL/RemoteApp vs full desktop.</summary>
    public string TypeLabel => RailMode ? _l["Type.RemoteApp.Text"] : _l["Type.Desktop.Text"];

    public SandboxConnectionMode ConnectionMode
        => RailMode ? SandboxConnectionMode.RemoteApp : SandboxConnectionMode.FullDesktop;

    /// <summary>
    /// The sandbox's own folder (manifest, recipe, instance record and user-data
    /// disks). Derived from the manifest path; read-only.
    /// </summary>
    public string SandboxFolder => string.IsNullOrWhiteSpace(ManifestPath)
        ? string.Empty
        : Path.GetDirectoryName(ManifestPath) ?? ManifestPath;

    public bool HasManifest => !string.IsNullOrWhiteSpace(ManifestPath);

    public string ManifestDisplay => HasManifest ? ManifestPath! : _l["Manifest.Missing.Text"];

    public bool CanMapAtRuntime => _owner.SelectedEngine?.Capabilities.SupportsRuntimeFolderMapping == true;

    public Brush StateBrush => State switch
    {
        SandboxState.Running => new SolidColorBrush(RunningColor),
        SandboxState.Starting or SandboxState.Rebooting => new SolidColorBrush(WorkingColor),
        SandboxState.Faulted => new SolidColorBrush(FaultColor),
        SandboxState.Created => new SolidColorBrush(CreatedColor),
        _ => new SolidColorBrush(IdleColor),
    };

    public void Apply(SandboxInstanceDto dto)
    {
        _recipe = dto.Recipe;
        Id = dto.Id;
        Name = dto.Name;
        MemoryMb = dto.Recipe.Memory.MemoryMb;
        CpuCores = dto.Recipe.Cpu.CoreCount;
        SelectedNetworking = NetworkingOptions.First(o => o.Value == dto.Recipe.Networking);
        SelectedVgpu = VgpuOptions.First(o => o.Value == dto.Recipe.VirtualGpu);
        // Manifests written before the selector existed may carry Local (=1);
        // it normalizes to the pipe (both are local, network-free transports).
        SelectedTransport = TransportOptions.FirstOrDefault(o => o.Value == dto.Recipe.Transport)
            ?? TransportOptions[0];
        RailMode = dto.Recipe.RailMode;
        RailProgram = dto.Recipe.ResolvedRailProgram;
        FullScreen = dto.Recipe.FullScreen;
        ProtectedClient = dto.Recipe.ProtectedClient;
        RebootSupport = dto.Recipe.RebootSupport;
        DiskSizeMb = dto.Recipe.Storage.DiskSizeMb;
        State = dto.State;
        StateLabel = LocalizeState(dto.State);
        Engine = dto.Engine;
        EngineLabel = LocalizeEngine(dto.Engine);
        ManifestPath = dto.ManifestPath;

        MappedFolders.Clear();
        foreach (var folder in dto.Recipe.MappedFolders)
        {
            MappedFolders.Add(new MappedFolderItem(folder, _l));
        }

        LogonCommands.Clear();
        foreach (var command in dto.Recipe.LogonCommands)
        {
            LogonCommands.Add(command);
        }

        RuntimeSummary = dto.Runtime is null
            ? _l["RuntimeNotRunning.Text"]
            : $"vm={dto.Runtime.VmId}  transport={dto.Runtime.Transport}  rail={dto.Runtime.RailMode}  pipe={dto.Runtime.NamedPipeEndpoint}";

        OnPropertyChanged(nameof(ShortId));
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(IsPrimaryEnabled));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(CanMapAtRuntime));
        OnPropertyChanged(nameof(IsContainerEngine));
        OnPropertyChanged(nameof(ShowContainerDesktop));
    }

    partial void OnStateChanged(SandboxState value)
    {
        OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(ShowStop));
        OnPropertyChanged(nameof(IsPrimaryEnabled));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(ShowContainerDesktop));
    }

    partial void OnRailModeChanged(bool value)
    {
        OnPropertyChanged(nameof(TypeLabel));
        OnPropertyChanged(nameof(ConnectionMode));
    }

    partial void OnManifestPathChanged(string? value)
    {
        OnPropertyChanged(nameof(HasManifest));
        OnPropertyChanged(nameof(ManifestDisplay));
        OnPropertyChanged(nameof(SandboxFolder));
    }

    public SandboxRecipe BuildRecipe() => _recipe with
    {
        Name = new SandboxName(string.IsNullOrWhiteSpace(Name) ? SandboxName.DefaultValue : Name),
        Memory = new SandboxMemoryConfiguration((int)MemoryMb),
        Cpu = new SandboxCpuConfiguration((int)CpuCores),
        Networking = SelectedNetworking?.Value ?? NetworkingMode.Default,
        VirtualGpu = SelectedVgpu?.Value ?? VirtualGpuMode.Default,
        Transport = SelectedTransport?.Value ?? RdpTransportKind.NamedPipe,
        RailMode = RailMode,
        RailProgram = string.IsNullOrWhiteSpace(RailProgram) ? SandboxRecipe.DefaultRailProgram : RailProgram.Trim(),
        FullScreen = FullScreen,
        ProtectedClient = ProtectedClient,
        RebootSupport = RebootSupport,
        Storage = BuildStorage(),
        MappedFolders = MappedFolders.Select(f => f.Folder).ToArray(),
        LogonCommands = LogonCommands.ToArray(),
    };

    private SandboxStorageOptions BuildStorage()
    {
        // The user-data location is derived from the app-level config & data
        // folder and the instance id (see IHcsHostLayout.GetStorageDirectory);
        // only the disk size remains per-sandbox.
        var storage = SandboxStorageOptions.Default;
        if (DiskSizeMb > 0)
        {
            storage = storage with { DiskSizeMb = (uint)DiskSizeMb };
        }

        return storage;
    }

    // --------------------------- Lifecycle ---------------------------

    /// <summary>
    /// The single primary action: Start while idle, Connect while running.
    /// </summary>
    [RelayCommand]
    private async Task PrimaryActionAsync()
    {
        if (State == SandboxState.Running)
        {
            await ConnectCoreAsync();
        }
        else if (CanStart)
        {
            await StartCoreAsync();
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        await GuardAsync(async () =>
        {
            // End the RDP session first: the client auto-reconnects, and a
            // client that is still attached can hold (or immediately
            // re-establish) the guest session while the engine is trying to
            // shut the compute system down. The session window lives in its own
            // child process (see SessionProcessManager).
            await SessionProcessManager.EndAsync(Id);

            await _manager.StopAsync(Id);
            _userDisconnected = false;
            var dto = await _manager.GetAsync(Id);
            Apply(dto);
            Status = _l["Status.Stopped.Text"];
        });
    }

    /// <summary>Explicitly ends the session; no automatic reconnect afterwards.</summary>
    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await SessionProcessManager.EndAsync(Id);
        _userDisconnected = true;
        Status = _l["Status.Disconnected.Text"];
        _owner.Status = Status;
    }

    private async Task StartCoreAsync()
    {
        await GuardAsync(async () =>
        {
            Status = _l["Status.Starting.Text"];
            var dto = await _manager.StartAsync(Id, ConnectionMode);
            Apply(dto);
            Status = _l["Status.Running.Text"];

            // Auto-connect after a successful start, unless the user explicitly
            // disconnected this session. The session runs in its own process, so
            // this returns as soon as it is spawned.
            if (!_userDisconnected && LaunchSession(ConnectionMode) == SessionLaunch.Started)
            {
                Status = _l["Status.Connected.Text"];
            }
        });
    }

    private async Task ConnectCoreAsync()
    {
        await GuardAsync(() =>
        {
            switch (LaunchSession(ConnectionMode))
            {
                case SessionLaunch.Started:
                    _userDisconnected = false;
                    Status = _l["Status.Connected.Text"];
                    break;
                case SessionLaunch.AlreadyRunning:
                    // SessionProcessManager refused the launch (a window is
                    // already open); say so instead of leaving the status
                    // untouched, which reads as "Connect does nothing".
                    Status = _l["Status.SessionAlreadyOpen.Text"];
                    break;
            }

            return Task.CompletedTask;
        });
    }

    private enum SessionLaunch
    {
        Started,
        AlreadyRunning,
        CliMissing,
    }

    /// <summary>
    /// Presents the session in a separate <c>wsm connect</c> child process (see
    /// <see cref="SessionProcessManager"/>). Returns <see cref="SessionLaunch.CliMissing"/>
    /// — with <see cref="Status"/> set — when the companion CLI cannot be located, and
    /// <see cref="SessionLaunch.AlreadyRunning"/> when a session window for this
    /// sandbox is already open.
    /// </summary>
    private SessionLaunch LaunchSession(SandboxConnectionMode mode, string? program = null)
    {
        var exe = WsmCliLocator.Find();
        if (exe is null)
        {
            Status = _l["Status.CliMissing.Text"];
            return SessionLaunch.CliMissing;
        }

        return SessionProcessManager.Start(exe, Id, mode == SandboxConnectionMode.RemoteApp, program)
            ? SessionLaunch.Started
            : SessionLaunch.AlreadyRunning;
    }

    /// <summary>
    /// Called on the UI thread when this sandbox's session process exits on its
    /// own (the user closed the window, or the connect failed). Surfaces a
    /// failed connect; otherwise the status line returns to the running state.
    /// </summary>
    internal void OnSessionProcessEnded(int exitCode, string output)
    {
        if (exitCode != 0 && !string.IsNullOrWhiteSpace(output))
        {
            Status = output;
            _owner.Status = output;
        }
        else if (IsRunning)
        {
            Status = _l["Status.Running.Text"];
            _owner.Status = Status;
        }
    }

    /// <summary>
    /// Opens the sandbox as a RAIL session that launches <paramref name="program"/>.
    /// The quick-launch buttons use this so a specific tool (Explorer, Registry
    /// Editor, Command Prompt, …) starts without editing the definition.
    /// </summary>
    [RelayCommand]
    private async Task LaunchProgramAsync(QuickLaunchProgram? program)
    {
        if (program is null)
        {
            return;
        }

        await GuardAsync(async () =>
        {
            // A RAIL connect replaces the guest's current session (the
            // single-session guest refuses it otherwise): end the existing
            // window gracefully first so it does not linger as a disconnected
            // overlay.
            await SessionProcessManager.EndAsync(Id);
            if (LaunchSession(SandboxConnectionMode.RemoteApp, program.Path) == SessionLaunch.Started)
            {
                _userDisconnected = false;
                Status = _l.Get("Status.LaunchedProgram.Text", program.Path);
            }
        });
    }

    // --------------------- Container desktop ---------------------

    /// <summary>Opens the container's whole desktop in a single viewer window (frame buffer).</summary>
    [RelayCommand]
    private async Task OpenContainerDesktopAsync()
        => await OpenContainerDesktopCoreAsync(ContainerDesktopProviderIds.FrameBuffer);

    /// <summary>Mirrors every container window as a real host window (RAIL-like).</summary>
    [RelayCommand]
    private async Task OpenContainerWindowsAsync()
        => await OpenContainerDesktopCoreAsync(ContainerDesktopProviderIds.WindowMirror);

    /// <summary>
    /// Starts (or adopts) the container desktop channel, then presents it with a
    /// <c>wsm desktop-view</c> child process. The channel runs wherever the
    /// container does — the agent when one answered, this process otherwise (see
    /// <see cref="ContainerDesktopConnector"/>); only the viewer is launched here.
    /// </summary>
    private async Task OpenContainerDesktopCoreAsync(string providerId)
    {
        await GuardAsync(async () =>
        {
            Status = _l["Status.DesktopStarting.Text"];
            _owner.Status = Status;

            var status = await _owner.ContainerDesktop.StartDesktopAsync(Id, BuildRecipe(), providerId);

            var exe = WsmCliLocator.Find();
            if (exe is null)
            {
                Status = _l["Status.CliMissing.Text"];
                return;
            }

            // The live channel's provider decides the viewer mode: an adopted
            // channel may run the other provider than the button asked for.
            var mode = status.ProviderId == ContainerDesktopProviderIds.WindowMirror ? "windows" : "frame";
            DesktopViewerLauncher.Launch(exe, status.FramePath, mode);
            Status = _l["Status.DesktopLaunched.Text"];
        });
    }

    private IReadOnlyList<QuickLaunchProgram> BuildQuickLaunchPrograms() =>
    [
        new(_l["QuickLaunch.Explorer.Text"], @"C:\Windows\explorer.exe", "\uE8B7"),
        new(_l["QuickLaunch.Registry.Text"], @"C:\Windows\regedit.exe", "\uE713"),
        new(_l["QuickLaunch.CommandPrompt.Text"], @"C:\Windows\System32\cmd.exe", "\uE756"),
        new(_l["QuickLaunch.PowerShell.Text"], @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "\uE756"),
        new(_l["QuickLaunch.Notepad.Text"], @"C:\Windows\notepad.exe", "\uE70B"),
        new(_l["QuickLaunch.TaskManager.Text"], @"C:\Windows\System32\taskmgr.exe", "\uE9D9"),
    ];

    /// <summary>
    /// Persists the edited definition. <see cref="ISandboxManager"/> has no
    /// in-place update, so the definition is replaced (delete + create) while it
    /// is not running.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        await GuardAsync(async () =>
        {
            if (State.IsLive())
            {
                Status = _l["Status.StopBeforeSave.Text"];
                return;
            }

            var recipe = BuildRecipe();
            var name = Name;
            await _manager.DeleteAsync(Id);
            var dto = await _manager.CreateAsync(name, recipe, Engine);
            Apply(dto);
            await _owner.ReloadAsync(Id);
            _owner.Status = _l["Status.Saved.Text"];
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await GuardAsync(async () =>
        {
            await _manager.DeleteAsync(Id);
            _owner.Status = _l["Status.Deleted.Text"];
            await _owner.ReloadAsync(null);
        });
    }

    // --------------------------- Checkpoints ---------------------------

    [RelayCommand]
    private async Task SaveCheckpointAsync()
    {
        await GuardAsync(async () =>
        {
            var type = CheckpointAsTemplate ? SandboxCheckpointType.Template : SandboxCheckpointType.FullState;
            var checkpoint = await _manager.SaveCheckpointAsync(Id, $"cp-{DateTime.Now:HHmmss}", type);
            Checkpoints.Insert(0, checkpoint);
            Status = _l["Status.CheckpointSaved.Text"];
        });
    }

    [RelayCommand]
    private async Task RefreshCheckpointsAsync()
    {
        await GuardAsync(async () =>
        {
            var list = await _manager.ListCheckpointsAsync(Id);
            Checkpoints.Clear();
            foreach (var checkpoint in list)
            {
                Checkpoints.Add(checkpoint);
            }

            Status = _l.Get("Status.CheckpointsRefreshed.Text", Checkpoints.Count);
        });
    }

    [RelayCommand]
    private async Task RestoreCheckpointAsync()
    {
        if (SelectedCheckpoint is null)
        {
            Status = _l["Status.SelectCheckpoint.Text"];
            return;
        }

        await GuardAsync(async () =>
        {
            var newId = await _manager.RestoreCheckpointAsync(Id, SelectedCheckpoint.Id);
            Status = _l.Get("Status.CheckpointRestored.Text", newId);
        });
    }

    // --------------------------- Folders ---------------------------

    [RelayCommand]
    private void AddFolder()
    {
        if (string.IsNullOrWhiteSpace(NewFolderHost))
        {
            return;
        }

        try
        {
            MappedFolders.Add(new MappedFolderItem(new MappedFolder(NewFolderHost, string.Empty, NewFolderReadOnly), _l));
            NewFolderHost = string.Empty;
            Status = _l["Status.FolderAdded.Text"];
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveFolder(MappedFolderItem? folder)
    {
        if (folder is null)
        {
            return;
        }

        MappedFolders.Remove(folder);
        Status = _l["Status.FolderRemoved.Text"];
    }

    [RelayCommand]
    private async Task MapNowAsync()
    {
        if (string.IsNullOrWhiteSpace(NewFolderHost))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            var folder = new MappedFolder(NewFolderHost, string.Empty, NewFolderReadOnly);
            await _manager.MapFolderAsync(Id, folder);
            MappedFolders.Add(new MappedFolderItem(folder, _l));
            NewFolderHost = string.Empty;
            Status = _l["Status.RuntimeMapped.Text"];
        });
    }

    // --------------------------- Logon commands ---------------------------

    [RelayCommand]
    private void AddLogon()
    {
        if (string.IsNullOrWhiteSpace(NewLogonCommand))
        {
            return;
        }

        LogonCommands.Add(new LogonCommand(NewLogonCommand));
        NewLogonCommand = string.Empty;
        Status = _l["Status.LogonAdded.Text"];
    }

    [RelayCommand]
    private void RemoveLogon(LogonCommand? command)
    {
        if (command is null)
        {
            return;
        }

        LogonCommands.Remove(command);
        Status = _l["Status.LogonRemoved.Text"];
    }

    // --------------------------- Runtime ---------------------------

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (string.IsNullOrWhiteSpace(ExecCommandText))
        {
            return;
        }

        await GuardAsync(async () =>
        {
            var exitCode = await _manager.ExecuteAsync(Id, ExecCommandText);
            Status = _l.Get("Status.Executed.Text", exitCode);
        });
    }

    [RelayCommand]
    private async Task GetIpsAsync()
    {
        await GuardAsync(async () =>
        {
            var addresses = await _manager.GetIpAddressesAsync(Id);
            Status = _l.Get("Status.IpAddresses.Text", string.Join(", ", addresses));
        });
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
            _owner.Status = Status;
        }
        catch (Exception ex)
        {
            GuiErrorLog.Write(ex, nameof(GuardAsync));
            Status = ex.Message;
            _owner.Status = ex.Message;
        }
    }

    private string LocalizeState(SandboxState state) => state switch
    {
        SandboxState.Created => _l["State.Created.Text"],
        SandboxState.Starting => _l["State.Starting.Text"],
        SandboxState.Running => _l["State.Running.Text"],
        SandboxState.Rebooting => _l["State.Rebooting.Text"],
        SandboxState.Stopped => _l["State.Stopped.Text"],
        SandboxState.Terminated => _l["State.Terminated.Text"],
        SandboxState.Faulted => _l["State.Faulted.Text"],
        _ => state.ToString(),
    };

    private string LocalizeNetworking(NetworkingMode mode) => mode switch
    {
        NetworkingMode.Enabled => _l["Networking.Enabled.Text"],
        NetworkingMode.Disabled => _l["Networking.Disabled.Text"],
        _ => _l["Networking.Default.Text"],
    };

    private string LocalizeVgpu(VirtualGpuMode mode) => mode switch
    {
        VirtualGpuMode.Enabled => _l["Vgpu.Enabled.Text"],
        VirtualGpuMode.Disabled => _l["Vgpu.Disabled.Text"],
        _ => _l["Vgpu.Default.Text"],
    };

    private string LocalizeTransport(RdpTransportKind kind) => kind switch
    {
        RdpTransportKind.Tcp => _l["Transport.Tcp.Text"],
        _ => _l["Transport.NamedPipe.Text"],
    };

    private string LocalizeEngine(SandboxEngineKind? kind) => kind switch
    {
        null => _l["Engine.Auto.Text"],
        SandboxEngineKind.HcsSandbox => "HCS",
        SandboxEngineKind.WindowsSandbox => "Windows Sandbox",
        SandboxEngineKind.WindowsContainer => _l["Engine.Container.Text"],
        _ => kind.ToString()!,
    };
}
