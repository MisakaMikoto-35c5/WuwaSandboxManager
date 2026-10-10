using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsSandboxManager.Application.Abstractions;
using WindowsSandboxManager.Application.Dtos;

namespace WindowsSandboxManager.App.ViewModels;

/// <summary>Root view model: the instance list plus host/backend status.</summary>
public partial class MainViewModel : ObservableObject
{
    private readonly ISandboxApplicationService _app;

    public MainViewModel(ISandboxApplicationService app) => _app = app;

    public ObservableCollection<SandboxInstanceViewModel> Instances { get; } = new();

    [ObservableProperty] private SandboxInstanceViewModel? _selected;

    [ObservableProperty] private string _status = "Ready";

    [ObservableProperty] private string _environmentSummary = "Detecting environment...";

    [ObservableProperty] private bool _isBusy;

    public async Task InitializeAsync()
    {
        await LoadEnvironmentAsync();
        await ReloadAsync(null);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync(Selected?.Id);

    [RelayCommand]
    private async Task CreateAsync()
    {
        try
        {
            var dto = await _app.CreateAsync(new CreateSandboxRequest());
            await ReloadAsync(dto.Id);
            Status = $"Created sandbox {dto.Id}.";
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    public async Task ReloadAsync(Guid? selectId)
    {
        try
        {
            IsBusy = true;
            var items = await _app.ListAsync();
            Instances.Clear();
            foreach (var dto in items)
            {
                Instances.Add(new SandboxInstanceViewModel(_app, this, dto));
            }

            Selected = selectId is { } id
                ? Instances.FirstOrDefault(i => i.Id == id) ?? Instances.FirstOrDefault()
                : Instances.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadEnvironmentAsync()
    {
        try
        {
            var caps = await _app.GetCapabilitiesAsync();
            EnvironmentSummary =
                $"Backend: {caps.BackendName}  |  Elevated: {caps.Environment.IsElevated}  |  " +
                $"Multi-instance: {caps.Backend.SupportsMultipleInstances}  |  " +
                $"CPU: {caps.Backend.SupportsCpuConfiguration}  |  " +
                $"Checkpoints: {caps.Backend.SupportsCheckpoints}  |  " +
                $"Custom storage: {caps.Backend.SupportsCustomStorageLocation}  |  " +
                $"Custom base image: {caps.Backend.SupportsCustomBaseImage}";
        }
        catch (Exception ex)
        {
            EnvironmentSummary = ex.Message;
        }
    }
}
