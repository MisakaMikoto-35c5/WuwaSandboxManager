using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Wsm.Application;
using Wsm.Engine.WindowsContainer;
using Wsm.Engines.Abstractions;
using Wsm.Hcs.Storage;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Persistence;
using Wsm.Service.Client;
using Wsm.Ui.WinUI.Localization;
using Wsm.Ui.WinUI.ViewModels;

namespace Wsm.Ui.WinUI;

/// <summary>
/// The single management window. The main surface is the instance list plus the
/// primary lifecycle actions (Start/Connect, Disconnect, Stop); every
/// configuration concern lives in the per-instance settings dialog, and the
/// app-level settings dialog holds the language switch, engine routing and
/// environment details. Services are resolved from the ABP container supplied by
/// <see cref="App"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IAppLocalizer _localizer;
    private readonly IElevationService _elevation;
    private readonly string? _openManifestPath;
    private bool _initialized;
    private bool _languageReady;
    private string _appliedCulture;

    public MainWindow(IServiceProvider services, string? openManifestPath)
    {
        InitializeComponent();
        Title = "Windows Sandbox Manager";

        _localizer = services.GetRequiredService<IAppLocalizer>();
        _elevation = services.GetRequiredService<IElevationService>();
        _openManifestPath = openManifestPath;
        _appliedCulture = LanguageOption.Resolve(_localizer.CurrentCulture.Name).CultureName;

        // Route VM-lifecycle calls through the resident WSM agent (the
        // maintenance process) when one answers — spawning a standalone console
        // agent on demand when none does — so running sandboxes survive this
        // GUI process. Definition-level operations stay in-process regardless.
        ISandboxManager manager = services.GetRequiredService<ISandboxManager>();
        var agentClient = services.GetService<IWsmServiceClient>();
        if (agentClient is not null)
        {
            manager = new ServiceBackedSandboxManager(
                manager,
                agentClient,
                UiSettingsStore.Load().ResolveConfigDataFolder(),
                services.GetRequiredService<ILogger<ServiceBackedSandboxManager>>());
        }

        ViewModel = new MainViewModel(
            manager,
            services.GetRequiredService<ISandboxEngineRegistry>(),
            services.GetRequiredService<IMultiInstancePolicy>(),
            _elevation,
            _localizer,
            services.GetRequiredService<IContainerImageImporter>(),
            services.GetRequiredService<IContainerIsolationPolicy>());

        Root.DataContext = ViewModel;

        TryEnableMica();
        TryResize();

        Activated += OnActivated;
    }

    public MainViewModel ViewModel { get; }

    private void TryEnableMica()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Older Windows builds: keep the default backdrop.
        }
    }

    private void TryResize()
    {
        try
        {
            AppWindow.Resize(new SizeInt32(1200, 840));
        }
        catch
        {
            // Resizing is cosmetic; ignore failures on unusual shells.
        }
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _languageReady = true;
        await ViewModel.InitializeAsync();

        // Opening a .wsm (association / cli argument) starts it with zero config.
        if (!string.IsNullOrWhiteSpace(_openManifestPath))
        {
            await ViewModel.OpenManifestAsync(_openManifestPath);
        }
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady || sender is not ComboBox { SelectedItem: LanguageOption option })
        {
            return;
        }

        // Only react to a genuine change between shipped languages; the initial
        // selection (system culture -> nearest shipped language) must not relaunch.
        if (string.Equals(option.CultureName, _appliedCulture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appliedCulture = option.CultureName;
        PersistAndRelaunch(option.CultureName);
    }

    /// <summary>
    /// Persists the chosen language and/or config &amp; data folder and restarts the
    /// process. A non-packaged WinUI app caches its XAML resource context, so
    /// rebuilding the window in the same process does not reliably re-resolve
    /// <c>x:Uid</c>; a full restart does. The other persisted setting is preserved.
    /// </summary>
    private void PersistAndRelaunch(string? cultureName = null, string? configDataFolder = null)
    {
        try
        {
            var current = UiSettingsStore.Load();
            UiSettingsStore.Save(current with
            {
                CultureName = cultureName ?? current.CultureName,
                ConfigDataFolder = configDataFolder ?? current.ConfigDataFolder,
            });
        }
        catch (Exception ex)
        {
            ViewModel.Status = ex.Message;
            return;
        }

        if (Microsoft.UI.Xaml.Application.Current is App app)
        {
            app.Relaunch();
        }
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        if (_elevation.TryRelaunchElevated())
        {
            Close();
        }
        else
        {
            ViewModel.Status = _localizer["Status.ElevationDeclined.Text"];
        }
    }

    // ----------------------------- Creation -----------------------------

    private async void OnOpenFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(Wsm.Application.WsmFileAssociation.Extension);
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
            {
                await ViewModel.OpenManifestAsync(file.Path);
            }
        }
        catch (Exception ex)
        {
            ViewModel.Status = ex.Message;
        }
    }

    private async void OnNewSandboxClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ResetNewInstance();
        CreateDialog.XamlRoot = Root.XamlRoot;
        CreateDialog.DataContext = ViewModel;
        await CreateDialog.ShowAsync();
    }

    private async void OnCreateDialogPrimary(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            // Keep the dialog open when creation fails so the message stays visible.
            var created = await ViewModel.CreateFromDialogAsync();
            args.Cancel = !created;
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnBrowseConfigDataFolder(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (string.Equals(path, ViewModel.ConfigDataFolder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Mirror the language flow: persist the new folder and restart so the
        // persistence and HCS layout pick it up on the next start.
        ViewModel.ConfigDataFolder = path;
        PersistAndRelaunch(configDataFolder: path);
    }

    /// <summary>
    /// Picks where an imported container image layer is written. Unlike the data
    /// folder this is not persisted on its own: the download button saves the
    /// resulting image path once the import succeeds.
    /// </summary>
    private async void OnBrowseContainerImageFolder(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (!string.IsNullOrWhiteSpace(path))
        {
            ViewModel.ContainerImageFolder = path;
        }
    }

    // ----------------------------- Settings -----------------------------

    private async void OnAppSettingsClick(object sender, RoutedEventArgs e)
    {
        AppSettingsDialog.XamlRoot = Root.XamlRoot;
        AppSettingsDialog.DataContext = ViewModel;
        await AppSettingsDialog.ShowAsync();
    }

    private async void OnInstanceSettingsClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Selected is null)
        {
            return;
        }

        InstanceSettingsDialog.XamlRoot = Root.XamlRoot;
        InstanceSettingsDialog.DataContext = ViewModel;
        await InstanceSettingsDialog.ShowAsync();
    }

    private async void OnSaveInstanceSettings(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            if (ViewModel.Selected is { } vm)
            {
                await vm.SaveCommand.ExecuteAsync(null);
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void OnDeleteInstanceClick(object sender, RoutedEventArgs e)
    {
        // Close the settings dialog first: only one ContentDialog may be open.
        InstanceSettingsDialog.Hide();

        ConfirmDeleteDialog.XamlRoot = Root.XamlRoot;
        ConfirmDeleteDialog.DataContext = ViewModel;
        var result = await ConfirmDeleteDialog.ShowAsync();

        if (result == ContentDialogResult.Primary && ViewModel.Selected is { } vm)
        {
            await vm.DeleteCommand.ExecuteAsync(null);
        }
    }

    // ----------------------------- Manifest -----------------------------

    private void OnShowManifestClick(object sender, RoutedEventArgs e) => RevealManifest(select: true);

    private void OnOpenManifestFolderClick(object sender, RoutedEventArgs e) => RevealManifest(select: false);

    private void RevealManifest(bool select)
    {
        var path = ViewModel.Selected?.ManifestPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            ViewModel.Status = _localizer["Status.ManifestMissing.Text"];
            return;
        }

        try
        {
            var target = select
                ? $"/select,\"{path}\""
                : $"\"{System.IO.Path.GetDirectoryName(path)}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ViewModel.Status = ex.Message;
        }
    }

    // ----------------------------- Helpers -----------------------------

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
            picker.FileTypeFilter.Add("*");
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch
        {
            // A folder picker failure should not take the window down.
            return null;
        }
    }
}
