using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Persistence;
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
    private readonly DispatcherQueue _dispatcher =
        DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("The main view model must be created on the UI thread.");

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
        ConfigDataFolder = UiSettingsStore.Load().ResolveConfigDataFolder();

        Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));
        BuildEngines();
        NewEngineOptions = BuildEngineOptions();
        Languages = LanguageOption.CreateDefaults();
        SelectedLanguage = LanguageOption.Resolve(_l.CurrentCulture.Name);
        UpdateEnvironmentSummary();
        ResetNewInstance();

        SessionProcessManager.SessionEnded += OnSessionEnded;
    }

    public ObservableCollection<SandboxInstanceViewModel> Instances { get; } = [];

    public ObservableCollection<EngineOptionViewModel> Engines { get; } = [];

    /// <summary>Engine choices for the creation dialog ("Auto" first, then engines).</summary>
    public IReadOnlyList<EngineSelectOption> NewEngineOptions { get; }

    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>
    /// The user-selectable config &amp; data folder. Resolved once at startup from
    /// the persisted settings; changing it relaunches the app.
    /// </summary>
    [ObservableProperty] private string _configDataFolder;

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

    /// <summary>
    /// The program a RAIL sandbox launches by default, e.g.
    /// <c>C:\Windows\explorer.exe</c>. Only meaningful for the RAIL type.
    /// </summary>
    [ObservableProperty] private string _newRailProgram = SandboxRecipe.DefaultRailProgram;

    [ObservableProperty] private double _newMemoryMb = SandboxMemoryConfiguration.DefaultMemoryMb;

    [ObservableProperty] private double _newCpuCores = 4;

    /// <summary>The engine chosen for a new sandbox; the "Auto" entry has a null kind.</summary>
    [ObservableProperty] private EngineSelectOption? _newEngineOption;

    /// <summary>The engine kind pinned for the new sandbox, or <c>null</c> for auto.</summary>
    public SandboxEngineKind? NewEngine => NewEngineOption?.Kind;

    public bool HasSelection => Selected is not null;

    public bool HasInstances => Instances.Count > 0;

    /// <summary>True when the creation dialog is set to the RemoteApp (RAIL) type.</summary>
    public bool IsNewRailMode => NewTypeIndex == 0;

    partial void OnSelectedChanged(SandboxInstanceViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnNewTypeIndexChanged(int value) => OnPropertyChanged(nameof(IsNewRailMode));

    public Task InitializeAsync(Guid? selectId = null) => ReloadAsync(selectId);

    /// <summary>
    /// Opens a portable <c>.wsm</c> from an arbitrary path (association / open
    /// dialog / cli argument) and starts it with zero configuration.
    /// </summary>
    public async Task OpenManifestAsync(string manifestPath)
    {
        try
        {
            IsBusy = true;
            var dto = await _manager.ImportAsync(manifestPath);
            await ReloadAsync(dto.Id);
            Status = _l["Status.Opened.Text"];

            if (Selected is { } vm)
            {
                await vm.PrimaryActionCommand.ExecuteAsync(null);
            }
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

    /// <summary>Resets the creation dialog fields to their defaults.</summary>
    public void ResetNewInstance()
    {
        NewName = string.Empty;
        NewTypeIndex = 1;
        NewMemoryMb = SandboxMemoryConfiguration.DefaultMemoryMb;
        NewCpuCores = Math.Max(1, Math.Min(Environment.ProcessorCount / 2, 64));
        NewRailProgram = SandboxRecipe.DefaultRailProgram;
        NewEngineOption = NewEngineOptions.FirstOrDefault();
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
                RailProgram = string.IsNullOrWhiteSpace(NewRailProgram) ? SandboxRecipe.DefaultRailProgram : NewRailProgram.Trim(),
                Storage = SandboxStorageOptions.Default,
            };

            var name = string.IsNullOrWhiteSpace(NewName) ? null : NewName.Trim();
            var dto = await _manager.CreateAsync(name, recipe, NewEngine);
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

    private IReadOnlyList<EngineSelectOption> BuildEngineOptions()
    {
        var options = new List<EngineSelectOption>
        {
            new(null, _l["EngineAutoOption.Text"], isEnabled: true),
        };

        foreach (var engine in _registry.Engines.OrderBy(engine => (int)engine.Kind))
        {
            var reserved = engine.Kind == SandboxEngineKind.WindowsContainer;
            var display = reserved
                ? $"{engine.Name} — {_l["EngineReserved.Text"]}"
                : engine.IsAvailable
                    ? engine.Name
                    : $"{engine.Name} — {_l["EngineUnavailable.Text"]}";

            options.Add(new EngineSelectOption(engine.Kind, display, isEnabled: engine.IsAvailable));
        }

        return options;
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

    /// <summary>
    /// Routes a session-process exit (raised on a thread-pool thread) to the
    /// current view model for that sandbox, on the UI thread. View models are
    /// recreated on every refresh, so the single subscription lives here.
    /// </summary>
    private void OnSessionEnded(Guid id, int exitCode, string output)
    {
        _dispatcher.TryEnqueue(() =>
            Instances.FirstOrDefault(instance => instance.Id == id)
                ?.OnSessionProcessEnded(exitCode, output));
    }
}
