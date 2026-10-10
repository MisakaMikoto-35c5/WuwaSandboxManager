using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// View model for a single sandbox instance: editable definition fields plus the
/// lifecycle, connection, folder-mapping and checkpoint commands, all driven by
/// the new <see cref="ISandboxManager"/> API.
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

        Apply(dto);
    }

    public ObservableCollection<MappedFolderItem> MappedFolders { get; } = [];

    public ObservableCollection<LogonCommand> LogonCommands { get; } = [];

    public ObservableCollection<SandboxCheckpoint> Checkpoints { get; } = [];

    public IReadOnlyList<EnumOption<NetworkingMode>> NetworkingOptions { get; }

    public IReadOnlyList<EnumOption<VirtualGpuMode>> VgpuOptions { get; }

    [ObservableProperty] private Guid _id;

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private double _memoryMb = 4096;

    [ObservableProperty] private double _cpuCores = 4;

    [ObservableProperty] private EnumOption<NetworkingMode>? _selectedNetworking;

    [ObservableProperty] private EnumOption<VirtualGpuMode>? _selectedVgpu;

    [ObservableProperty] private bool _railMode;

    [ObservableProperty] private bool _fullScreen;

    [ObservableProperty] private bool _protectedClient;

    [ObservableProperty] private bool _rebootSupport;

    [ObservableProperty] private string _storagePath = string.Empty;

    [ObservableProperty] private double _diskSizeMb;

    [ObservableProperty] private SandboxState _state;

    [ObservableProperty] private string _stateLabel = string.Empty;

    [ObservableProperty] private string _runtimeSummary = string.Empty;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _newFolderHost = string.Empty;

    [ObservableProperty] private bool _newFolderReadOnly = true;

    [ObservableProperty] private string _newLogonCommand = string.Empty;

    [ObservableProperty] private string _execCommandText = string.Empty;

    [ObservableProperty] private bool _checkpointAsTemplate;

    [ObservableProperty] private SandboxCheckpoint? _selectedCheckpoint;

    public string ShortId => Id.ToString()[..8];

    public bool IsLive => State.IsLive();

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
        Id = dto.Id;
        Name = dto.Name;
        MemoryMb = dto.Recipe.Memory.MemoryMb;
        CpuCores = dto.Recipe.Cpu.CoreCount;
        SelectedNetworking = NetworkingOptions.First(o => o.Value == dto.Recipe.Networking);
        SelectedVgpu = VgpuOptions.First(o => o.Value == dto.Recipe.VirtualGpu);
        RailMode = dto.Recipe.RailMode;
        FullScreen = dto.Recipe.FullScreen;
        ProtectedClient = dto.Recipe.ProtectedClient;
        RebootSupport = dto.Recipe.RebootSupport;
        StoragePath = dto.Recipe.Storage.StoragePath ?? string.Empty;
        DiskSizeMb = dto.Recipe.Storage.DiskSizeMb;
        State = dto.State;
        StateLabel = LocalizeState(dto.State);

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
        OnPropertyChanged(nameof(StateBrush));
        OnPropertyChanged(nameof(CanMapAtRuntime));
    }

    public SandboxRecipe BuildRecipe() => new()
    {
        Name = new SandboxName(string.IsNullOrWhiteSpace(Name) ? SandboxName.DefaultValue : Name),
        Memory = new SandboxMemoryConfiguration((int)MemoryMb),
        Cpu = new SandboxCpuConfiguration((int)CpuCores),
        Networking = SelectedNetworking?.Value ?? NetworkingMode.Default,
        VirtualGpu = SelectedVgpu?.Value ?? VirtualGpuMode.Default,
        RailMode = RailMode,
        FullScreen = FullScreen,
        ProtectedClient = ProtectedClient,
        RebootSupport = RebootSupport,
        Storage = BuildStorage(),
        MappedFolders = MappedFolders.Select(f => f.Folder).ToArray(),
        LogonCommands = LogonCommands.ToArray(),
    };

    private SandboxStorageOptions BuildStorage()
    {
        var storage = SandboxStorageOptions.Default;
        if (!string.IsNullOrWhiteSpace(StoragePath))
        {
            storage = storage.WithPath(StoragePath);
        }

        if (DiskSizeMb > 0)
        {
            storage = storage with { DiskSizeMb = (uint)DiskSizeMb };
        }

        return storage;
    }

    [RelayCommand]
    private Task StartDesktopAsync() => StartAsync(SandboxConnectionMode.FullDesktop);

    [RelayCommand]
    private Task StartRemoteAppAsync() => StartAsync(SandboxConnectionMode.RemoteApp);

    [RelayCommand]
    private Task ConnectDesktopAsync() => ConnectAsync(SandboxConnectionMode.FullDesktop);

    [RelayCommand]
    private Task ConnectRemoteAppAsync() => ConnectAsync(SandboxConnectionMode.RemoteApp);

    [RelayCommand]
    private async Task StopAsync()
    {
        await GuardAsync(async () =>
        {
            await _manager.StopAsync(Id);
            Status = _l["Status.Stopped.Text"];
        });
    }

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
            var dto = await _manager.CreateAsync(name, recipe);
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

    private async Task StartAsync(SandboxConnectionMode mode)
    {
        await GuardAsync(async () =>
        {
            Status = _l["Status.Starting.Text"];
            var dto = await _manager.StartAsync(Id, mode);
            Apply(dto);
            Status = _l["Status.Running.Text"];
        });
    }

    private async Task ConnectAsync(SandboxConnectionMode mode)
    {
        await GuardAsync(async () =>
        {
            await _manager.ConnectAsync(Id, mode);
            Status = _l["Status.Connected.Text"];
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
}
