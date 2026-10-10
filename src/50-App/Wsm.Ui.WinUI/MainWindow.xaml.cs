using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Wsm.Application;
using Wsm.Engines.Abstractions;
using Wsm.Host.Elevation;
using Wsm.Host.Policy;
using Wsm.Ui.WinUI.Localization;
using Wsm.Ui.WinUI.ViewModels;

namespace Wsm.Ui.WinUI;

/// <summary>
/// The single management window: header with the language switch, the engine
/// selector, the instance list, the configuration/actions panel and the status
/// bar. Services are resolved from the ABP container supplied by <see cref="App"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly IAppLocalizer _localizer;
    private readonly IElevationService _elevation;
    private readonly Guid? _selectId;
    private bool _initialized;
    private bool _languageReady;
    private string _appliedCulture;

    public MainWindow(IServiceProvider services, Guid? selectId)
    {
        InitializeComponent();
        Title = "Windows Sandbox Manager";

        _localizer = services.GetRequiredService<IAppLocalizer>();
        _elevation = services.GetRequiredService<IElevationService>();
        _selectId = selectId;
        _appliedCulture = LanguageOption.Resolve(_localizer.CurrentCulture.Name).CultureName;

        ViewModel = new MainViewModel(
            services.GetRequiredService<ISandboxManager>(),
            services.GetRequiredService<ISandboxEngineRegistry>(),
            services.GetRequiredService<IMultiInstancePolicy>(),
            _elevation,
            _localizer);

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
        await ViewModel.InitializeAsync(_selectId);
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_languageReady || sender is not ComboBox { SelectedItem: LanguageOption option })
        {
            return;
        }

        // Only react to a genuine change between shipped languages; the initial
        // selection (system culture -> nearest shipped language) must not rebuild.
        if (string.Equals(option.CultureName, _appliedCulture, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _appliedCulture = option.CultureName;
        _ = ApplyLanguageAsync(option.CultureName, ViewModel.Selected?.Id);
    }

    private async Task ApplyLanguageAsync(string cultureName, Guid? selectId)
    {
        await _localizer.SetCultureAsync(cultureName);

        // WinUI has no hot reload of x:Uid markup, so rebuild the window. Defer it
        // so it happens after the current selection event has fully unwound.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (Microsoft.UI.Xaml.Application.Current is App app)
            {
                app.RebuildForLanguage(selectId);
            }
        });
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
}
