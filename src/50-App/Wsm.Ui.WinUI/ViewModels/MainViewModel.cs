using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// Root view model: the instance list, the creation dialog fields, the engine
/// routing choice, the language choice and the host status line. Everything is
/// driven by the new <see cref="ISandboxManager"/> API.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly ISandboxManager _manager;
    private readonly ISandboxEngineRegistry _registry;
    private readonly IMultiInstancePolicy _policy;
    private readonly IAppLocalizer _l;

    public MainViewModel(
        ISandboxManager manager,
        ISandboxEngineRegistry registry,
        IMultiInstancePolicy policy,
        IElevationService elevation,
        IAppLocalizer localizer)
    {
        _manager = manager;
        _registry = registry;
        _policy = policy;
        _l = localizer;

        IsElevated = elevation.IsElevated;
        BuildBranch = _policy.CurrentBuildBranch ?? "—";
        MultiInstanceAllowed = _policy.AllowsMultipleInstances;
        Status = _l["Status.Ready.Text"];
        DefaultStoragePath = _l["StoragePathBox.PlaceholderText"];

        Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));
        BuildEngines();
        Languages = LanguageOption.CreateDefaults();
        SelectedLanguage = LanguageOption.Resolve(_l.CurrentCulture.Name);
        UpdateEnvironmentSummary();
        ResetNewInstance();
    }

    public ObservableCollection<SandboxInstanceViewModel> Instances { get; } = [];

    public ObservableCollection<EngineOptionViewModel> Engines { get; } = [];

    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>Placeholder shown for the platform-default user-data location.</summary>
    public string DefaultStoragePath { get; }

    [ObservableProperty] private SandboxInstanceViewModel? _selected;

    [ObservableProperty] private EngineOptionViewModel? _selectedEngine;

    [ObservableProperty] private LanguageOption? _selectedLanguage;

    [ObservableProperty] private string _status;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _environmentSummary = string.Empty;

    [ObservableProperty] private bool _isElevated;

    [ObservableProperty] private bool _multiInstanceAllowed;

    [ObservableProperty] private string _buildBranch;

    // --- New sandbox (creation dialog) ---

    [ObservableProperty] private string _newName = string.Empty;

    /// <summary>0 = RemoteApp (RAIL), 1 = full desktop.</summary>
    [ObservableProperty] private int _newTypeIndex = 1;

    [ObservableProperty] private string _newStoragePath = string.Empty;

    [ObservableProperty] private double _newMemoryMb = SandboxMemoryConfiguration.DefaultMemoryMb;

    [ObservableProperty] private double _newCpuCores = 4;

    public bool HasSelection => Selected is not null;

    public bool HasInstances => Instances.Count > 0;

    partial void OnSelectedChanged(SandboxInstanceViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    public Task InitializeAsync(Guid? selectId = null) => ReloadAsync(selectId);

    /// <summary>Resets the creation dialog fields to their defaults.</summary>
    public void ResetNewInstance()
    {
        NewName = string.Empty;
        NewTypeIndex = 1;
        NewStoragePath = string.Empty;
        NewMemoryMb = SandboxMemoryConfiguration.DefaultMemoryMb;
        NewCpuCores = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 64));
    }

    /// <summary>
    /// Creates an instance from the creation-dialog fields. Returns false (and
    /// surfaces the message in <see cref="Status"/>) when creation fails so the
    /// dialog can stay open.
    /// </summary>
    public async Task<bool> CreateFromDialogAsync()
    {
        try
        {
            IsBusy = true;

            var recipe = new SandboxRecipe
            {
                Memory = new SandboxMemoryConfiguration(
                    double.IsFinite(NewMemoryMb) ? (int)Math.Round(NewMemoryMb) : SandboxMemoryConfiguration.DefaultMemoryMb),
                Cpu = new SandboxCpuConfiguration(
                    double.IsFinite(NewCpuCores) ? (int)Math.Round(NewCpuCores) : 4),
                RailMode = NewTypeIndex == 0,
                Storage = string.IsNullOrWhiteSpace(NewStoragePath)
                    ? SandboxStorageOptions.Default
                    : SandboxStorageOptions.Default.WithPath(NewStoragePath),
            };

            var name = string.IsNullOrWhiteSpace(NewName) ? null : NewName.Trim();
            var dto = await _manager.CreateAsync(name, recipe);
            await ReloadAsync(dto.Id);
            Status = _l["Status.Created.Text"];
            return true;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync(Selected?.Id);

    public async Task ReloadAsync(Guid? selectId)
    {
        try
        {
            IsBusy = true;
            var items = await _manager.ListAsync();
            Instances.Clear();
            foreach (var dto in items)
            {
                Instances.Add(new SandboxInstanceViewModel(_manager, this, dto, _l));
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

    private void BuildEngines()
    {
        Engines.Clear();
        var ordered = _registry.Engines.OrderBy(engine => (int)engine.Kind).ToArray();

        foreach (var engine in ordered)
        {
            var option = new EngineOptionViewModel(engine, _l);
            option.PropertyChanged += OnEnginePropertyChanged;
            Engines.Add(option);
        }

        // Default selection: the engine the selector would pick, i.e. the
        // available engine with the lowest SandboxEngineKind (HCS when elevated).
        var preferred = ordered.FirstOrDefault(engine => engine.IsAvailable);
        if (preferred is not null)
        {
            var option = Engines.First(o => o.Kind == preferred.Kind);
            option.IsSelected = true;
            SelectedEngine = option;
        }
    }

    private void OnEnginePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(EngineOptionViewModel.IsSelected))
        {
            return;
        }

        if (sender is EngineOptionViewModel { IsSelected: true } option)
        {
            SelectedEngine = option;
            UpdateEnvironmentSummary();
        }
    }

    private void UpdateEnvironmentSummary()
    {
        var elevated = IsElevated ? _l["Common.Yes.Text"] : _l["Common.No.Text"];
        var multi = MultiInstanceAllowed ? _l["Common.Yes.Text"] : _l["Common.No.Text"];
        var engine = SelectedEngine?.Name ?? "—";
        EnvironmentSummary =
            $"{engine}   ·   {_l["Env.Elevated.Text"]}: {elevated}" +
            $"   ·   {_l["Env.MultiInstance.Text"]}: {multi}" +
            $"   ·   {_l["Env.Build.Text"]}: {BuildBranch}";
    }
}
