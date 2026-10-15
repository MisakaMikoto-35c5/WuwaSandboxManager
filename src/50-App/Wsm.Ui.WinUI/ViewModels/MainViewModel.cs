using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engine.WindowsContainer;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Storage;
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
    private readonly IContainerImageImporter _imageImporter;
    private readonly IContainerIsolationPolicy _isolation;
    private readonly DispatcherQueue _dispatcher =
        DispatcherQueue.GetForCurrentThread()
        ?? throw new InvalidOperationException("The main view model must be created on the UI thread.");

    public MainViewModel(
        ISandboxManager manager,
        ISandboxEngineRegistry registry,
        IMultiInstancePolicy policy,
        IElevationService elevation,
        IAppLocalizer localizer,
        IContainerImageImporter imageImporter,
        IContainerIsolationPolicy isolation)
    {
        _manager = manager;
        _registry = registry;
        _policy = policy;
        _l = localizer;
        _imageImporter = imageImporter;
        _isolation = isolation;

        IsElevated = elevation.IsElevated;
        BuildBranch = _policy.CurrentBuildBranch ?? "—";
        MultiInstanceAllowed = _policy.AllowsMultipleInstances;
        Status = _l["Status.Ready.Text"];
        ConfigDataFolder = UiSettingsStore.Load().ResolveConfigDataFolder();

        ContainerImageReferences = new ObservableCollection<string>(_imageImporter.SuggestedImages);
        _selectedContainerImageReference = ContainerImageReferences.FirstOrDefault();
        _containerImageFolder = DefaultImageFolder(_selectedContainerImageReference);

        Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));
        BuildEngines();
        BuildEngineOptions();
        UpdateContainerIsolationStatus();
        Languages = LanguageOption.CreateDefaults();
        SelectedLanguage = LanguageOption.Resolve(_l.CurrentCulture.Name);
        RdpConnectors = RdpConnectorOption.CreateDefaults();
        SelectedRdpConnector = RdpConnectorOption.Resolve(UiSettingsStore.Load().RdpConnector);
        UpdateEnvironmentSummary();
        ResetNewInstance();

        SessionProcessManager.SessionEnded += OnSessionEnded;
    }

    public ObservableCollection<SandboxInstanceViewModel> Instances { get; } = [];

    public ObservableCollection<EngineOptionViewModel> Engines { get; } = [];

    /// <summary>Engine choices for the creation dialog ("Auto" first, then engines).</summary>
    public ObservableCollection<EngineSelectOption> NewEngineOptions { get; } = [];

    public IReadOnlyList<LanguageOption> Languages { get; }

    /// <summary>The selectable RDP connector presentations (WPF, WinUI 3).</summary>
    public IReadOnlyList<RdpConnectorOption> RdpConnectors { get; }

    /// <summary>
    /// The user-selectable config &amp; data folder. Resolved once at startup from
    /// the persisted settings; changing it relaunches the app.
    /// </summary>
    [ObservableProperty] private string _configDataFolder;

    [ObservableProperty] private SandboxInstanceViewModel? _selected;

    [ObservableProperty] private EngineOptionViewModel? _selectedEngine;

    [ObservableProperty] private LanguageOption? _selectedLanguage;

    [ObservableProperty] private RdpConnectorOption? _selectedRdpConnector;

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
        var isolation = _isolation.Evaluate();

        foreach (var engine in ordered)
        {
            // Explain why process isolation is (not) offered.
            var reasonKey = engine.Kind == SandboxEngineKind.WindowsContainer ? isolation.ReasonKey : null;
            var option = new EngineOptionViewModel(engine, _l, reasonKey);
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
        else
        {
            SelectedEngine = null;
        }
    }

    /// <summary>
    /// Rebuilds the creation dialog's engine list. The process-isolation engine
    /// (<c>WindowsContainer</c>) is only enabled when the isolation policy allows
    /// it; unavailable engines are still listed (disabled) for transparency.
    /// </summary>
    private void BuildEngineOptions()
    {
        NewEngineOptions.Clear();
        NewEngineOptions.Add(new EngineSelectOption(null, _l["EngineAutoOption.Text"], isEnabled: true));

        foreach (var engine in _registry.Engines.OrderBy(engine => (int)engine.Kind))
        {
            var display = engine.IsAvailable
                ? engine.Name
                : $"{engine.Name} — {_l["EngineUnavailable.Text"]}";

            NewEngineOptions.Add(new EngineSelectOption(engine.Kind, display, isEnabled: engine.IsAvailable));
        }

        NewEngineOption = NewEngineOptions.FirstOrDefault();
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

    // --------------------- Process-isolation image ---------------------

    /// <summary>Windows Server images offered for process isolation.</summary>
    public ObservableCollection<string> ContainerImageReferences { get; }

    /// <summary>The image reference the download button imports.</summary>
    [ObservableProperty] private string? _selectedContainerImageReference;

    /// <summary>Where the imported base layer is written.</summary>
    [ObservableProperty] private string _containerImageFolder;

    [ObservableProperty] private bool _isContainerImageDownloading;

    /// <summary>Download progress, 0..100.</summary>
    [ObservableProperty] private double _containerImageProgress;

    [ObservableProperty] private string _containerImageProgressText = string.Empty;

    /// <summary>A localized summary of whether process isolation is available and why.</summary>
    [ObservableProperty] private string _containerIsolationStatus = string.Empty;

    /// <summary>True when the current image can be used for process isolation.</summary>
    public bool IsProcessIsolationAvailable => _isolation.Evaluate().IsSupported;

    /// <summary>The download needs elevation (the layer APIs require it).</summary>
    public bool CanDownloadContainerImage => IsElevated && !IsContainerImageDownloading;

    partial void OnIsContainerImageDownloadingChanged(bool value)
        => OnPropertyChanged(nameof(CanDownloadContainerImage));

    partial void OnIsElevatedChanged(bool value)
        => OnPropertyChanged(nameof(CanDownloadContainerImage));

    partial void OnSelectedContainerImageReferenceChanged(string? value)
        => ContainerImageFolder = DefaultImageFolder(value);

    /// <summary>
    /// Downloads and prepares the selected Windows Server base image, then makes
    /// it the engine's image for this process and persists it for the next run.
    /// </summary>
    [RelayCommand]
    private async Task DownloadContainerImageAsync()
    {
        if (!CanDownloadContainerImage || string.IsNullOrWhiteSpace(SelectedContainerImageReference))
        {
            return;
        }

        try
        {
            IsContainerImageDownloading = true;
            ContainerImageProgress = 0;
            ContainerImageProgressText = _l["ContainerImageStarting.Text"];
            Status = _l["Status.ImageImporting.Text"];

            var progress = new Progress<ContainerImageImportProgress>(report =>
            {
                ContainerImageProgressText = report.Stage;
                ContainerImageProgress = report.Fraction is { } fraction ? fraction * 100 : 0;
            });

            var path = await _imageImporter.ImportBaseLayerAsync(
                SelectedContainerImageReference!, ContainerImageFolder, progress);

            // Effective immediately (the engine reads the override at start) and
            // persisted so it survives a restart.
            Environment.SetEnvironmentVariable(WindowsContainerEngineOptions.ImageOverrideVariable, path);
            try
            {
                var current = UiSettingsStore.Load();
                UiSettingsStore.Save(current with { ContainerBaseImagePath = path });
            }
            catch
            {
                // Persisting is best effort; the running process still uses it.
            }

            _isolation.Invalidate();
            BuildEngines();
            BuildEngineOptions();
            UpdateContainerIsolationStatus();
            Status = _l.Get("Status.ImageImported.Text", path);
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
        finally
        {
            IsContainerImageDownloading = false;
            ContainerImageProgressText = string.Empty;
        }
    }

    /// <summary>Re-evaluates and localizes the process-isolation status line.</summary>
    private void UpdateContainerIsolationStatus()
    {
        var support = _isolation.Evaluate();
        var state = support.IsSupported ? _l["Container.Available.Text"] : _l["Container.Unavailable.Text"];
        var reason = _l[support.ReasonKey];
        var image = support.ImageBuild?.ToString() ?? "—";
        ContainerIsolationStatus =
            $"{state} — {reason}{Environment.NewLine}" +
            _l.Get("Container.Versions.Text", support.HostBuild, image);
        OnPropertyChanged(nameof(IsProcessIsolationAvailable));
    }

    /// <summary>Default target folder for an image reference under <c>%LOCALAPPDATA%</c>.</summary>
    private static string DefaultImageFolder(string? reference)
    {
        var slug = string.IsNullOrWhiteSpace(reference)
            ? "image"
            : reference.Replace(':', '-').Replace('/', '-');
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsSandboxManager",
            "images",
            slug);
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
