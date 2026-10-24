using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Application;
using Wsm.Engine.WindowsContainer;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Persistence;
using Wsm.Service.Client;
using Wsm.Connection.ContainerDesktop;
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
        IContainerIsolationPolicy isolation,
        IWsmServiceClient? serviceClient = null,
        IContainerDesktopHost? containerDesktop = null)
    {
        _manager = manager;
        _registry = registry;
        _policy = policy;
        _l = localizer;
        _imageImporter = imageImporter;
        _isolation = isolation;

        ContainerDesktop = new ContainerDesktopConnector(
            serviceClient, containerDesktop, UiSettingsStore.Load().ResolveConfigDataFolder());

        IsElevated = elevation.IsElevated;
        BuildBranch = _policy.CurrentBuildBranch ?? "—";
        MultiInstanceAllowed = _policy.AllowsMultipleInstances;
        Status = _l["Status.Ready.Text"];
        ConfigDataFolder = UiSettingsStore.Load().ResolveConfigDataFolder();

        ContainerImageOptions = new ObservableCollection<ContainerImageOption>(
            _imageImporter.ImageCatalog.Select(info => new ContainerImageOption(info, _l)));
        // Keep Server Core 2022 the default suggestion: it is the smallest image
        // and the one the importer falls back to for a bare reference.
        _selectedContainerImageReference =
            ContainerImageOptions.FirstOrDefault(
                option => option.Reference == "mcr.microsoft.com/windows/servercore:ltsc2022")?.Reference
            ?? ContainerImageOptions.FirstOrDefault()?.Reference;
        _containerImageFolder = ContainerImagePaths.GetDefaultLayerPath(_selectedContainerImageReference);
        RefreshContainerImageInstallState();

        Instances.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasInstances));
        BuildEngines();
        BuildEngineOptions();
        NewDesktopKindOptions =
        [
            new EnumOption<ContainerDesktopKind>(ContainerDesktopKind.None, _l["DesktopKind.None.Text"]),
            new EnumOption<ContainerDesktopKind>(ContainerDesktopKind.FrameBuffer, _l["DesktopKind.FrameBuffer.Text"]),
            new EnumOption<ContainerDesktopKind>(ContainerDesktopKind.WindowMirror, _l["DesktopKind.WindowMirror.Text"]),
        ];
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

    /// <summary>True when the creation dialog pins the process-isolation (container) engine.</summary>
    public bool IsNewContainerEngine => NewEngine == SandboxEngineKind.WindowsContainer;

    /// <summary>Container desktop kinds offered when the container engine is pinned.</summary>
    public IReadOnlyList<EnumOption<ContainerDesktopKind>> NewDesktopKindOptions { get; }

    /// <summary>The desktop kind a new container sandbox gets (<see cref="SandboxRecipe.Desktop"/>).</summary>
    [ObservableProperty] private EnumOption<ContainerDesktopKind>? _newDesktopKindOption;

    /// <summary>Whether a new container sandbox presents its desktop into the user's session.</summary>
    [ObservableProperty] private bool _newDesktopPresent = true;

    /// <summary>The engine kind pinned for the new sandbox, or <c>null</c> for auto.</summary>
    public SandboxEngineKind? NewEngine => NewEngineOption?.Kind;

    /// <summary>Starts container desktop channels (agent-routed, in-process fallback).</summary>
    internal ContainerDesktopConnector ContainerDesktop { get; }

    public bool HasSelection => Selected is not null;

    public bool HasInstances => Instances.Count > 0;

    /// <summary>True when the creation dialog is set to the RemoteApp (RAIL) type.</summary>
    public bool IsNewRailMode => NewTypeIndex == 0;

    partial void OnSelectedChanged(SandboxInstanceViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnNewTypeIndexChanged(int value) => OnPropertyChanged(nameof(IsNewRailMode));

    partial void OnNewEngineOptionChanged(EngineSelectOption? value) => OnPropertyChanged(nameof(IsNewContainerEngine));

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
        NewDesktopKindOption = NewDesktopKindOptions.FirstOrDefault(o => o.Value == ContainerDesktopKind.FrameBuffer);
        NewDesktopPresent = true;
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
                // Only the process-isolation engine has a desktop to plan; other
                // engines keep the recipe default.
                Desktop = IsNewContainerEngine
                    ? new SandboxDesktopPlan
                    {
                        Kind = NewDesktopKindOption?.Value ?? ContainerDesktopKind.FrameBuffer,
                        InteractiveSession = NewDesktopKindOption?.Value is
                            ContainerDesktopKind.FrameBuffer or ContainerDesktopKind.WindowMirror,
                        PresentToInteractiveSession = NewDesktopPresent,
                    }
                    : SandboxDesktopPlan.Default,
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

        var isolation = _isolation.Evaluate();
        foreach (var engine in _registry.Engines.OrderBy(engine => (int)engine.Kind))
        {
            var display = engine.Name;
            if (!engine.IsAvailable)
            {
                display = $"{engine.Name} — {_l["EngineUnavailable.Text"]}";
                // A bare "Unavailable" says nothing; name the reason for the
                // process-isolation engine (e.g. no compatible image found).
                if (engine.Kind == SandboxEngineKind.WindowsContainer)
                {
                    display += $" ({IsolationReason(isolation)})";
                }
            }

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

    /// <summary>Windows Server images offered for process isolation (editable combo entries).</summary>
    public ObservableCollection<ContainerImageOption> ContainerImageOptions { get; }

    /// <summary>The image reference the download button imports; free text is allowed.</summary>
    [ObservableProperty] private string? _selectedContainerImageReference;

    /// <summary>Where the imported base layer is written.</summary>
    [ObservableProperty] private string _containerImageFolder;

    [ObservableProperty] private bool _isContainerImageDownloading;

    /// <summary>Download progress, 0..100.</summary>
    [ObservableProperty] private double _containerImageProgress;

    /// <summary>True outside the download stage, when no fraction is known.</summary>
    [ObservableProperty] private bool _containerImageProgressIndeterminate;

    /// <summary>The localized stage of the running download.</summary>
    [ObservableProperty] private string _containerImageProgressText = string.Empty;

    /// <summary>Bytes, speed and ETA of the running download.</summary>
    [ObservableProperty] private string _containerImageProgressDetailText = string.Empty;

    /// <summary>What the target folder currently holds.</summary>
    [ObservableProperty] private ContainerImageInstallState _containerImageInstallState;

    /// <summary>A localized summary of whether process isolation is available and why.</summary>
    [ObservableProperty] private string _containerIsolationStatus = string.Empty;

    /// <summary>Cancels the in-flight download; owned here so it survives the settings dialog.</summary>
    private CancellationTokenSource? _imageDownloadCts;

    /// <summary>True when the target folder already holds a downloaded image.</summary>
    public bool IsContainerImageInstalled => ContainerImageInstallState == ContainerImageInstallState.Installed;

    /// <summary>True when the target folder exists with content that is not a downloaded image.</summary>
    public bool IsContainerImageOccupied => ContainerImageInstallState == ContainerImageInstallState.Occupied;

    /// <summary>True when the current image can be used for process isolation.</summary>
    public bool IsProcessIsolationAvailable => _isolation.Evaluate().IsSupported;

    /// <summary>
    /// The download needs elevation (the layer APIs require it), no running
    /// download, and a target folder the importer will accept.
    /// </summary>
    public bool CanDownloadContainerImage =>
        IsElevated && !IsContainerImageDownloading && ContainerImageInstallState == ContainerImageInstallState.Missing;

    /// <summary>Re-download and delete are available whenever no download runs.</summary>
    public bool CanManageContainerImage => !IsContainerImageDownloading;

    partial void OnIsContainerImageDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDownloadContainerImage));
        OnPropertyChanged(nameof(CanManageContainerImage));
    }

    partial void OnIsElevatedChanged(bool value)
        => OnPropertyChanged(nameof(CanDownloadContainerImage));

    partial void OnContainerImageInstallStateChanged(ContainerImageInstallState value)
    {
        OnPropertyChanged(nameof(CanDownloadContainerImage));
        OnPropertyChanged(nameof(IsContainerImageInstalled));
        OnPropertyChanged(nameof(IsContainerImageOccupied));
    }

    partial void OnSelectedContainerImageReferenceChanged(string? value)
        => ContainerImageFolder = ContainerImagePaths.GetDefaultLayerPath(value);

    partial void OnContainerImageFolderChanged(string value)
        => RefreshContainerImageInstallState();

    /// <summary>Re-reads what the target folder holds (installed / occupied / free).</summary>
    private void RefreshContainerImageInstallState()
        => ContainerImageInstallState = ContainerImagePaths.GetInstallState(ContainerImageFolder);

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

        await RunImageDownloadAsync(deleteExisting: false);
    }

    /// <summary>Deletes the installed image and downloads it again.</summary>
    [RelayCommand]
    private async Task RedownloadContainerImageAsync()
    {
        if (!CanManageContainerImage || string.IsNullOrWhiteSpace(SelectedContainerImageReference))
        {
            return;
        }

        await RunImageDownloadAsync(deleteExisting: true);
    }

    /// <summary>Cancels the running download; the partial layer is removed.</summary>
    [RelayCommand]
    private void CancelContainerImageDownload() => _imageDownloadCts?.Cancel();

    /// <summary>Deletes the image in the target folder (best effort) and refreshes the state.</summary>
    [RelayCommand]
    private void DeleteContainerImage()
    {
        try
        {
            if (Directory.Exists(ContainerImageFolder))
            {
                Directory.Delete(ContainerImageFolder, recursive: true);
            }

            Status = _l["ContainerImageDeleted.Text"];
        }
        catch (Exception ex)
        {
            Status = _l.Get("ContainerImageDeleteFailed.Text", ex.Message);
        }
        finally
        {
            RefreshContainerImageInstallState();
        }
    }

    private async Task RunImageDownloadAsync(bool deleteExisting)
    {
        try
        {
            IsContainerImageDownloading = true;
            ContainerImageProgress = 0;
            ContainerImageProgressIndeterminate = true;
            ContainerImageProgressText = _l["ContainerImageStarting.Text"];
            ContainerImageProgressDetailText = string.Empty;
            Status = _l["Status.ImageImporting.Text"];

            if (deleteExisting && Directory.Exists(ContainerImageFolder))
            {
                // A WCOW layer holds reparse points that can be slow to delete;
                // keep the UI thread free.
                await Task.Run(() => Directory.Delete(ContainerImageFolder, recursive: true));
                RefreshContainerImageInstallState();
            }

            var progress = CreateImageImportProgress();
            _imageDownloadCts = new CancellationTokenSource();
            var path = await _imageImporter.ImportBaseLayerAsync(
                SelectedContainerImageReference!, ContainerImageFolder, progress, _imageDownloadCts.Token);

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
        catch (OperationCanceledException) when (_imageDownloadCts is { IsCancellationRequested: true })
        {
            Status = _l["ContainerImageCancelled.Text"];
        }
        catch (OperationCanceledException)
        {
            // Not a user cancel: an HTTP timeout surfaces as TaskCanceledException.
            Status = _l.Get("ContainerImageError.Network.Text", SelectedContainerImageReference ?? string.Empty);
        }
        catch (HttpRequestException)
        {
            Status = _l.Get("ContainerImageError.Network.Text", SelectedContainerImageReference ?? string.Empty);
        }
        catch (SandboxValidationException)
        {
            Status = _l.Get("ContainerImageError.FolderNotEmpty.Text", ContainerImageFolder);
        }
        catch (Exception ex)
        {
            Status = _l.Get("ContainerImageError.Generic.Text", ex.Message);
        }
        finally
        {
            _imageDownloadCts?.Dispose();
            _imageDownloadCts = null;
            IsContainerImageDownloading = false;
            ContainerImageProgressIndeterminate = false;
            ContainerImageProgressText = string.Empty;
            ContainerImageProgressDetailText = string.Empty;
            RefreshContainerImageInstallState();
        }
    }

    /// <summary>
    /// Reports importer progress into the dialog (stage, bar, bytes/speed/ETA)
    /// and mirrors it into the main window status area, so closing the settings
    /// dialog neither cancels nor hides the operation. Reopening the dialog
    /// re-binds to these same properties.
    /// </summary>
    private Progress<ContainerImageImportProgress> CreateImageImportProgress()
    {
        var stopwatch = Stopwatch.StartNew();
        var lastSample = stopwatch.Elapsed;
        long lastBytes = 0;
        double smoothedBytesPerSecond = 0;

        return new Progress<ContainerImageImportProgress>(report =>
        {
            ContainerImageProgressText = _l[$"ContainerImageStage.{report.Stage}.Text"];
            ContainerImageProgressIndeterminate = report.Fraction is null;
            if (report.Fraction is { } fraction)
            {
                ContainerImageProgress = fraction * 100;
            }

            if (report is { Stage: ContainerImageImportStage.Downloading, BytesReceived: { } received })
            {
                var now = stopwatch.Elapsed;
                var elapsed = now - lastSample;
                if (elapsed >= TimeSpan.FromMilliseconds(500))
                {
                    // Exponentially smoothed speed so the ETA does not jitter.
                    var instant = (received - lastBytes) / elapsed.TotalSeconds;
                    smoothedBytesPerSecond = smoothedBytesPerSecond <= 0
                        ? instant
                        : (smoothedBytesPerSecond * 0.7) + (instant * 0.3);
                    lastBytes = received;
                    lastSample = now;

                    ContainerImageProgressDetailText = report.TotalBytes is { } totalBytes
                        ? _l.Get(
                            "ContainerImageProgress.Detail.Text",
                            FormatBytes(received),
                            FormatBytes(totalBytes),
                            $"{FormatBytes((long)Math.Max(0, smoothedBytesPerSecond))}/s",
                            FormatEta(totalBytes - received, smoothedBytesPerSecond))
                        : _l.Get("ContainerImageProgress.BytesOnly.Text", FormatBytes(received));
                }
            }
            else
            {
                ContainerImageProgressDetailText = string.Empty;
            }

            Status = string.IsNullOrEmpty(ContainerImageProgressDetailText)
                ? ContainerImageProgressText
                : $"{ContainerImageProgressText} — {ContainerImageProgressDetailText}";
        });
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1L << 30
            ? $"{bytes / 1073741824.0:0.#} GB"
            : $"{bytes / 1048576.0:0} MB";

    private string FormatEta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 0 || remainingBytes <= 0)
        {
            return "—";
        }

        var seconds = remainingBytes / bytesPerSecond;
        return seconds >= 90
            ? _l.Get("ContainerImageProgress.EtaMinutes.Text", (int)Math.Ceiling(seconds / 60))
            : _l.Get("ContainerImageProgress.EtaSeconds.Text", Math.Max(1, (int)Math.Round(seconds)));
    }

    /// <summary>Re-evaluates and localizes the process-isolation status line.</summary>
    private void UpdateContainerIsolationStatus()
    {
        var support = _isolation.Evaluate();
        var state = support.IsSupported ? _l["Container.Available.Text"] : _l["Container.Unavailable.Text"];
        var image = support.ImageBuild?.ToString() ?? "—";
        ContainerIsolationStatus =
            $"{state} — {IsolationReason(support)}{Environment.NewLine}" +
            _l.Get("Container.Versions.Text", support.HostBuild, image);
        OnPropertyChanged(nameof(IsProcessIsolationAvailable));
    }

    /// <summary>The localized reason (<c>Container.Reason.*</c>) behind an isolation-policy verdict.</summary>
    private string IsolationReason(ContainerIsolationSupport support) => _l[support.ReasonKey];

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
