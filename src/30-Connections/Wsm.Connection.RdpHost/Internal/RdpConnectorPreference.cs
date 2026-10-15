using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;
using Wsm.Persistence;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// Default <see cref="IRdpConnectorPreference"/>. Resolution order:
/// the <c>WSM_RDP_CONNECTOR</c> environment variable, then the persisted
/// <c>RdpConnector</c> UI setting, then <see cref="RdpConnectorKind.Wpf"/>.
/// A malformed value is ignored (and logged at debug level) rather than fatal.
/// </summary>
[ExposeServices(typeof(IRdpConnectorPreference))]
internal sealed class RdpConnectorPreference : IRdpConnectorPreference, ISingletonDependency
{
    private readonly ILogger<RdpConnectorPreference> _logger;

    public RdpConnectorPreference(ILogger<RdpConnectorPreference> logger)
    {
        _logger = logger;
        Connector = Resolve();
    }

    public RdpConnectorKind Connector { get; }

    private RdpConnectorKind Resolve()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("WSM_RDP_CONNECTOR");
        if (TryParse(fromEnvironment, out var fromEnv))
        {
            return fromEnv;
        }

        try
        {
            var fromSettings = UiSettingsStore.Load().RdpConnector;
            if (fromSettings is { } kind)
            {
                return kind;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Reading the persisted RDP connector preference failed; using the default.");
        }

        return RdpConnectorKind.Wpf;
    }

    internal static bool TryParse(string? value, out RdpConnectorKind kind)
    {
        kind = RdpConnectorKind.Wpf;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Accept both the enum name and the common aliases, case-insensitively.
        switch (value.Trim().ToLowerInvariant())
        {
            case "wpf":
                kind = RdpConnectorKind.Wpf;
                return true;
            case "winui":
            case "winui3":
            case "winui-3":
                kind = RdpConnectorKind.WinUi;
                return true;
            default:
                return false;
        }
    }
}
