using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Domain;
using WindowsSandboxManager.Domain.ValueObjects;

namespace WindowsSandboxManager.App.ViewModels;

/// <summary>
/// View model for a single sandbox instance: editable recipe fields plus the
/// lifecycle and connection commands.
/// </summary>
public partial class SandboxInstanceViewModel : ObservableObject
{
    private readonly ISandboxApplicationService _app;
    private readonly MainViewModel _owner;

    public SandboxInstanceViewModel(ISandboxApplicationService app, MainViewModel owner, SandboxInstanceDto dto)
    {
        _app = app;
        _owner = owner;
        Id = dto.Id;
        Apply(dto);
    }

    public Guid Id { get; }

    public ObservableCollection<MappedFolder> MappedFolders { get; } = new();

    public ObservableCollection<LogonCommand> LogonCommands { get; } = new();

    public ObservableCollection<SandboxCheckpoint> Checkpoints { get; } = new();

    public IReadOnlyList<NetworkingMode> NetworkingOptions { get; } = Enum.GetValues<NetworkingMode>();

    public IReadOnlyList<VirtualGpuMode> VgpuOptions { get; } = Enum.GetValues<VirtualGpuMode>();

    [ObservableProperty] private string _name = string.Empty;

    [ObservableProperty] private double _memoryMb = 4096;

    [ObservableProperty] private double _cpuCores = 4;

    [ObservableProperty] private NetworkingMode _networking;

    [ObservableProperty] private VirtualGpuMode _virtualGpu;

    [ObservableProperty] private bool _railMode;

    [ObservableProperty] private bool _fullScreen;

    [ObservableProperty] private bool _protectedClient;

    [ObservableProperty] private bool _rebootSupport;

    [ObservableProperty] private string _storagePath = string.Empty;

    [ObservableProperty] private double _diskSizeMb;

    [ObservableProperty] private bool _checkpointAsTemplate;

    [ObservableProperty] private SandboxCheckpoint? _selectedCheckpoint;

    [ObservableProperty] private SandboxState _state;

    [ObservableProperty] private string _runtimeSummary = "Not running";

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _newFolderHost = string.Empty;

    [ObservableProperty] private bool _newFolderReadOnly = true;

    [ObservableProperty] private string _newLogonCommand = string.Empty;

    public string ShortId => Id.ToString()[..8];

    public void Apply(SandboxInstanceDto dto)
    {
        Name = dto.Name;
        MemoryMb = dto.Recipe.Memory.MemoryMb;
        CpuCores = dto.Recipe.Cpu.CoreCount;
        Networking = dto.Recipe.Networking;
        VirtualGpu = dto.Recipe.VirtualGpu;
        RailMode = dto.Recipe.RailMode;
        FullScreen = dto.Recipe.FullScreen;
        ProtectedClient = dto.Recipe.ProtectedClient;
        RebootSupport = dto.Recipe.RebootSupport;
        StoragePath = dto.Recipe.Storage.StoragePath ?? string.Empty;
        DiskSizeMb = dto.Recipe.Storage.DiskSizeMb;
        State = dto.State;

        MappedFolders.Clear();
        foreach (var folder in dto.Recipe.MappedFolders)
        {
            MappedFolders.Add(folder);
        }

        LogonCommands.Clear();
        foreach (var command in dto.Recipe.LogonCommands)
        {
            LogonCommands.Add(command);
        }

        RuntimeSummary = dto.Runtime is null
            ? "Not running"
            : $"vm={dto.Runtime.VmId}  transport={dto.Runtime.Transport}  rail={dto.Runtime.RailMode}  pipe={dto.Runtime.NamedPipeEndpoint}";
    }

    public SandboxRecipe BuildRecipe() => new()
    {
        Name = new SandboxName(string.IsNullOrWhiteSpace(Name) ? SandboxName.DefaultValue : Name),
        Memory = new SandboxMemoryConfiguration((int)MemoryMb),
        Cpu = new SandboxCpuConfiguration((int)CpuCores),
        Networking = Networking,
        VirtualGpu = VirtualGpu,
        RailMode = RailMode,
        FullScreen = FullScreen,
        ProtectedClient = ProtectedClient,
        RebootSupport = RebootSupport,
        Storage = BuildStorage(),
        MappedFolders = MappedFolders.ToArray(),
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
            await _app.StopAsync(Id);
            Status = "Stopped.";
        });
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        await GuardAsync(async () =>
        {
            var dto = await _app.UpdateAsync(Id, new UpdateSandboxRequest(BuildRecipe()));
            Apply(dto);
            Status = "Saved.";
        });
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        await GuardAsync(async () =>
        {
            await _app.DeleteAsync(Id);
            await _owner.ReloadAsync(null);
        });
    }

    [RelayCommand]
    private async Task SaveCheckpointAsync()
    {
        await GuardAsync(async () =>
        {
            var type = CheckpointAsTemplate ? SandboxCheckpointType.Template : SandboxCheckpointType.FullState;
            var checkpoint = await _app.SaveCheckpointAsync(Id, $"cp-{DateTime.Now:HHmmss}", type);
            Checkpoints.Insert(0, checkpoint);
            Status = $"Checkpoint {checkpoint.Id} saved ({type}).";
        });
    }

    [RelayCommand]
    private async Task RefreshCheckpointsAsync()
    {
        await GuardAsync(async () =>
        {
            var list = await _app.ListCheckpointsAsync(Id);
            Checkpoints.Clear();
            foreach (var checkpoint in list)
            {
                Checkpoints.Add(checkpoint);
            }

            Status = $"{Checkpoints.Count} checkpoint(s).";
        });
    }

    [RelayCommand]
    private async Task RestoreCheckpointAsync()
    {
        if (SelectedCheckpoint is null)
        {
            Status = "Select a checkpoint to restore.";
            return;
        }

        await GuardAsync(async () =>
        {
            var newId = await _app.RestoreCheckpointAsync(Id, SelectedCheckpoint.Id);
            Status = $"Restored checkpoint as {newId}.";
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
            MappedFolders.Add(new MappedFolder(NewFolderHost, string.Empty, NewFolderReadOnly));
            NewFolderHost = string.Empty;
            Status = "Folder added (remember to Save).";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveFolder(MappedFolder folder)
    {
        MappedFolders.Remove(folder);
        Status = "Folder removed (remember to Save).";
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
        Status = "Logon command added (remember to Save).";
    }

    [RelayCommand]
    private void RemoveLogon(LogonCommand command)
    {
        LogonCommands.Remove(command);
        Status = "Logon command removed (remember to Save).";
    }

    private async Task StartAsync(SandboxConnectionMode mode)
    {
        await GuardAsync(async () =>
        {
            Status = $"Starting ({mode})...";
            var dto = await _app.StartAsync(Id, mode);
            Apply(dto);
            Status = $"Running ({mode}).";
        });
    }

    private async Task ConnectAsync(SandboxConnectionMode mode)
    {
        await GuardAsync(async () =>
        {
            await _app.ConnectAsync(Id, mode);
            Status = $"Session launched ({mode}).";
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
}
