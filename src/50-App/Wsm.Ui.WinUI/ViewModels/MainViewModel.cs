using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Ui.WinUI.Localization;

namespace Wsm.Ui.WinUI.ViewModels;

/// <summary>
/// Root view model: the instance list, the engine selector, the language choice
/// and the host status line. Everything is driven by the new
/// <see cref="ISandboxManager"/> API.
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

        Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));
        BuildEngines();
        Languages = LanguageOption.CreateDefaults();
        SelectedLanguage = LanguageOption.Resolve(_l.CurrentCulture.Name);
        UpdateEnvironmentSummary();
    }

    public ObservableCollection<SandboxInstanceViewModel> Instances { get; } = [];

    public ObservableCollection<EngineOptionViewModel> Engines { get; } = [];

    public IReadOnlyList<LanguageOption> Languages { get; }

    [ObservableProperty] private SandboxInstanceViewModel? _selected;

    [ObservableProperty] private EngineOptionViewModel? _selectedEngine;

    [ObservableProperty] private LanguageOption? _selectedLanguage;

    [ObservableProperty] private string _status;

    [ObservableProperty] private bool _isBusy;

    [ObservableProperty] private string _environmentSummary = string.Empty;

    [ObservableProperty] private bool _isElevated;

    [ObservableProperty] private bool _multiInstanceAllowed;

    [ObservableProperty] private string _buildBranch;

    public bool HasSelection => Selected is not null;

    public bool HasInstances => Instances.Count > 0;

    partial void OnSelectedChanged(SandboxInstanceViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    public Task InitializeAsync(Guid? selectId = null) => ReloadAsync(selectId);

    [RelayCommand]
    private async Task CreateAsync()
    {
        try
        {
            IsBusy = true;
            var dto = await _manager.CreateAsync(null, SandboxRecipe.Default);
            await ReloadAsync(dto.Id);
            Status = _l["Status.Created.Text"];
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
