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
/// <remarks>
/// For an unpackaged app the default <see cref="ResourceManager"/> does not
/// always bind to the assembly PRI (named <c>&lt;assembly&gt;.pri</c> rather than
/// <c>resources.pri</c>), so the lookup tries the default manager first and then
/// the app's own PRI file. It always reads the effective
/// <see cref="CultureInfo.CurrentUICulture"/>.
/// </remarks>
[ExposeServices(typeof(IAppLocalizer))]
internal sealed class AppLocalizer : IAppLocalizer, ISingletonDependency
{
    private const string ResourceSubtreeName = "Resources";

    /// <summary>A key present in both shipped languages, used to validate a manager.</summary>
    private const string SentinelKey = "Type.Desktop.Text";

    private readonly ResourceMap? _strings;
    private readonly ResourceContext? _context;
    private CultureInfo _culture;

    public AppLocalizer()
    {
        _culture = CultureInfo.CurrentUICulture;
        (_strings, _context) = OpenResources(_culture);
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

        // Best effort: make the framework's x:Uid lookup follow the choice too.
        // The Windows App SDK ApplicationLanguages honours this for unpackaged
        // apps as well (the UWP Windows.Globalization one does not).
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = cultureName;
        }
        catch
        {
            // Not supported for some hosts; x:Uid then keeps the current
            // language while view-model strings still switch.
        }

        _culture = culture;
        if (_context is not null)
        {
            _context.QualifierValues["Language"] = culture.Name;
        }

        CultureChanged?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    private static (ResourceMap? Strings, ResourceContext? Context) OpenResources(CultureInfo culture)
    {
        foreach (var manager in CreateManagers())
        {
            try
            {
                var map = manager.MainResourceMap.GetSubtree(ResourceSubtreeName);
                var context = manager.CreateResourceContext();
                context.QualifierValues["Language"] = culture.Name;

                // Reject a manager that opened but cannot resolve our resources
                // (for example an empty default manager) and try the next one.
                if (Resolve(map, context, SentinelKey) is not null)
                {
                    return (map, context);
                }
            }
            catch
            {
                // Try the next candidate.
            }
        }

        // If the PRI cannot be opened the UI still renders using the keys
        // themselves; view-model strings simply stay untranslated.
        return (null, null);
    }

    private static List<ResourceManager> CreateManagers()
    {
        var managers = new List<ResourceManager>();

        try
        {
            managers.Add(new ResourceManager());
        }
        catch
        {
            // No default resources.pri; fall through to the assembly PRI.
        }

        try
        {
            var assemblyName = typeof(AppLocalizer).Assembly.GetName().Name;
            var pri = Path.Combine(AppContext.BaseDirectory, $"{assemblyName}.pri");
            if (File.Exists(pri))
            {
                managers.Add(new ResourceManager(pri));
            }
        }
        catch
        {
            // Ignore; the default manager above may still work.
        }

        return managers;
    }

    /// <summary>
    /// Resolves a <c>.resw</c> key. Keys use dots as their hierarchy separator
    /// (<c>Type.Desktop.Text</c>) while the resource map uses slashes
    /// (<c>Type/Desktop/Text</c>), so both forms are attempted.
    /// </summary>
    private static string? Resolve(ResourceMap map, ResourceContext context, string key)
    {
        foreach (var candidate in KeyCandidates(key))
        {
            try
            {
                var value = map.GetValue(candidate, context).ValueAsString;
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }
            catch
            {
                // Missing key in this form; try the next.
            }
        }

        return null;
    }

    private static IEnumerable<string> KeyCandidates(string key)
    {
        yield return key;

        var slashed = key.Replace('.', '/');
        if (!string.Equals(slashed, key, StringComparison.Ordinal))
        {
            yield return slashed;
        }
    }

    private string? TryGet(string key)
    {
        if (_strings is null || _context is null)
        {
            return null;
        }

        // Missing PRI / key: fall back to the key itself so the UI still renders.
        return Resolve(_strings, _context, key);
    }
}
