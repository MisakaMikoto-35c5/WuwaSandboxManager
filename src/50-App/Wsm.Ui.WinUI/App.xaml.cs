using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Volo.Abp;
using Volo.Abp.Autofac;

namespace Wsm.Ui.WinUI;

/// <summary>
/// WinUI 3 application host. Boots the ABP module graph in <see cref="OnLaunched"/>
/// (the same pattern the CLI and the bootstrap tests use), resolves the UI
/// services from <c>app.ServiceProvider</c>, and shuts the container down when the
/// last window closes. Switching language rebuilds the window so the XAML
/// <c>x:Uid</c> strings re-resolve against the new culture.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private IAbpApplicationWithInternalServiceProvider? _abp;
    private Window? _window;
    private bool _rebuilding;

    public App() => InitializeComponent();

    /// <summary>The ABP service provider, available once <see cref="OnLaunched"/> has run.</summary>
    public IServiceProvider Services { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _abp = await AbpApplicationFactory.CreateAsync<WinUiModule>(options => options.UseAutofac());
        await _abp.InitializeAsync();

        // ABP 10.2.0: app.Services is the IServiceCollection; the built provider
        // is app.ServiceProvider.
        Services = _abp.ServiceProvider;

        ShowMainWindow(null);
    }

    private void ShowMainWindow(Guid? selectId)
    {
        var window = new MainWindow(Services, selectId);
        window.Closed += OnWindowClosed;
        _window = window;
        window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args)
    {
        if (_rebuilding)
        {
            return;
        }

        await ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        var abp = _abp;
        _abp = null;
        if (abp is null)
        {
            return;
        }

        await abp.ShutdownAsync();
        abp.Dispose();
    }

    /// <summary>
    /// Rebuilds the main window after a language change. The new window is shown
    /// before the old one is closed so the app never runs windowless.
    /// </summary>
    public void RebuildForLanguage(Guid? selectId)
    {
        _rebuilding = true;
        var old = _window;
        if (old is not null)
        {
            old.Closed -= OnWindowClosed;
        }

        ShowMainWindow(selectId);
        _rebuilding = false;

        old?.Close();
    }
}
