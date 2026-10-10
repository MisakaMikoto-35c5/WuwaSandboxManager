using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Volo.Abp;
using Volo.Abp.Autofac;
using Wsm.Application;
using Wsm.Persistence;

namespace Wsm.Ui.WinUI;

/// <summary>
/// WinUI 3 application host. Boots the ABP module graph in <see cref="OnLaunched"/>
/// (the same pattern the CLI and the bootstrap tests use), resolves the UI
/// services from <c>app.ServiceProvider</c>, and shuts the container down when the
/// last window closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Language</b>: the chosen culture is persisted in <c>ui-settings.json</c> and
/// applied here, in the constructor, before any window or resource lookup. A
/// genuine change in the settings dialog persists the choice and calls
/// <see cref="Relaunch"/>; for a non-packaged WinUI app the framework caches its
/// resource context, so a full process restart is the only reliable way to
/// re-resolve XAML <c>x:Uid</c> strings.
/// </para>
/// <para>
/// <b>Open with</b>: when launched with a <c>.wsm</c> sandbox path argument the
/// file is opened and its sandbox is started with zero configuration (the main
/// window imports it and starts it).
/// </para>
/// </remarks>
public partial class App : Microsoft.UI.Xaml.Application
{
    private IAbpApplicationWithInternalServiceProvider? _abp;
    private Window? _window;

    public App()
    {
        // Must run before InitializeComponent: the XAML resource context is
        // created lazily on first use and caches the active language.
        ApplyPersistedCulture();
        InitializeComponent();
    }

    /// <summary>The ABP service provider, available once <see cref="OnLaunched"/> has run.</summary>
    public IServiceProvider Services { get; private set; } = null!;

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        _abp = await AbpApplicationFactory.CreateAsync<WinUiModule>(options => options.UseAutofac());
        await _abp.InitializeAsync();

        // ABP 10.2.0: app.Services is the IServiceCollection; the built provider
        // is app.ServiceProvider.
        Services = _abp.ServiceProvider;

        TryRegisterFileAssociation();

        ShowMainWindow(ResolveOpenManifestPath());
    }

    private void ShowMainWindow(string? openManifestPath)
    {
        var window = new MainWindow(Services, openManifestPath);
        window.Closed += OnWindowClosed;
        _window = window;
        window.Activate();
    }

    private async void OnWindowClosed(object sender, WindowEventArgs args) => await ShutdownAsync();

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
    /// Restarts the process so the persisted language is fully re-resolved, then
    /// shuts the current app down. Callers must persist the choice first.
    /// </summary>
    public void Relaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch
        {
            // If the relaunch cannot start, keep the current process alive; the
            // persisted choice still applies on the next manual start.
        }

        _window?.Close();
    }

    private static string? ResolveOpenManifestPath()
    {
        try
        {
            return WsmLaunchArguments.GetManifestPath(Environment.GetCommandLineArgs());
        }
        catch
        {
            // Unknown/malformed arguments must never take the app down.
            return null;
        }
    }

    private static void TryRegisterFileAssociation()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
            {
                FileAssociationRegistrar.Register(exe);
            }
        }
        catch
        {
            // Registering the association is a convenience; never fatal.
        }
    }

    private static void ApplyPersistedCulture()
    {
        try
        {
            var settings = UiSettingsStore.Load();
            if (string.IsNullOrWhiteSpace(settings.CultureName))
            {
                return;
            }

            var culture = new CultureInfo(settings.CultureName);
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;

            try
            {
                // Windows App SDK's ApplicationLanguages (unlike the UWP one)
                // honours the override for unpackaged apps too. Must be set
                // before InitializeComponent / the first resource lookup.
                Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = settings.CultureName;
            }
            catch
            {
                // Not supported for some hosts; the culture set above still
                // drives the view-model strings.
            }
        }
        catch
        {
            // Fall back to the system culture.
        }
    }
}
