using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Volo.Abp.DependencyInjection;

namespace Wsm.Ui.WinUI.Localization;

/// <summary>
/// Default <see cref="IAppLocalizer"/>. Reads the app's <c>resources.pri</c>
/// (generated from <c>Resources/&lt;lang&gt;/Resources.resw</c>) through the
/// Windows App SDK <see cref="ResourceManager"/>, which works for unpackaged
/// WinUI 3 apps. The same PRI backs XAML <c>x:Uid</c> resolution.
/// </summary>
[ExposeServices(typeof(IAppLocalizer))]
internal sealed class AppLocalizer : IAppLocalizer, ISingletonDependency
{
    private const string ResourceSubtreeName = "Resources";

    private readonly ResourceMap? _strings;
    private readonly ResourceContext? _context;
    private CultureInfo _culture;

    public AppLocalizer()
    {
        _culture = CultureInfo.CurrentUICulture;
        try
        {
            var resources = new ResourceManager();
            _strings = resources.MainResourceMap.GetSubtree(ResourceSubtreeName);
            _context = resources.CreateResourceContext();
            _context.QualifierValues["Language"] = _culture.Name;
        }
        catch
        {
            // If the PRI cannot be opened the UI still renders using the keys
            // themselves; view-model strings simply stay untranslated.
            _strings = null;
            _context = null;
        }
    }

    public event EventHandler? CultureChanged;

    public CultureInfo CurrentCulture => _culture;

    public string this[string key] => Get(key);

    public string Get(string key, params object[] args)
    {
        var value = TryGet(key) ?? key;
        return args.Length == 0 ? value : string.Format(_culture, value, args);
    }

    public Task SetCultureAsync(string cultureName)
    {
        var culture = new CultureInfo(cultureName);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentUICulture = culture;

        // Best effort: make the framework's x:Uid lookup follow the choice too
        // (it uses its own default resource context).
        try
        {
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = cultureName;
        }
        catch
        {
            // Not supported for some unpackaged hosts; x:Uid then keeps the
            // system language while view-model strings still switch.
        }

        _culture = culture;
        if (_context is not null)
        {
            _context.QualifierValues["Language"] = culture.Name;
        }

        CultureChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private string? TryGet(string key)
    {
        if (_strings is null || _context is null)
        {
            return null;
        }

        try
        {
            var value = _strings.GetValue(key, _context).ValueAsString;
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch
        {
            // Missing PRI / key: fall back to the key itself so the UI still renders.
            return null;
        }
    }
}
