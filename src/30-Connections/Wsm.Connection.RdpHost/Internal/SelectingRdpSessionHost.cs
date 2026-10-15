using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost.Internal;

/// <summary>
/// The <see cref="IRdpSessionHost"/> the rest of the system resolves. It picks
/// the registered <see cref="IRdpConnectorHost"/>, matching
/// <see cref="IRdpConnectorPreference"/>, and delegates to it. When the preferred
/// connector is not present but another is, the available one is used instead
/// (so a host that only ships one connector still works); when no connector is
/// present at all, <see cref="ShowAsync"/> fails with a clear message.
/// </summary>
[ExposeServices(typeof(IRdpSessionHost))]
internal sealed class SelectingRdpSessionHost : IRdpSessionHost, ISingletonDependency
{
    private readonly IReadOnlyList<IRdpConnectorHost> _hosts;
    private readonly IRdpConnectorPreference _preference;
    private readonly IServiceProvider _services;
    private readonly ILogger<SelectingRdpSessionHost> _logger;

    public SelectingRdpSessionHost(
        IEnumerable<IRdpConnectorHost> hosts,
        IRdpConnectorPreference preference,
        IServiceProvider services,
        ILogger<SelectingRdpSessionHost> logger)
    {
        _hosts = hosts.ToArray();
        _preference = preference;
        _services = services;
        _logger = logger;
    }

    public Task ShowAsync(RdpSessionRequest request, CancellationToken cancellationToken = default)
    {
        var host = Resolve();
        var control = _services.GetService<IRdpSandboxControl>();
        _logger.LogDebug(
            "Presenting sandbox {SandboxId} through the {Connector} connector.",
            request.SandboxId, host.Kind);
        return host.ShowAsync(request, control, cancellationToken);
    }

    public void CloseAll()
    {
        foreach (var host in _hosts)
        {
            host.CloseAll();
        }
    }

    private IRdpConnectorHost Resolve()
    {
        if (_hosts.Count == 0)
        {
            throw new InvalidOperationException(
                "No RDP connector module is registered. Add a reference to either " +
                "Wsm.Connection.RdpHost.Wpf or Wsm.Connection.RdpHost.WinUi.");
        }

        var preferred = _preference.Connector;
        var match = _hosts.FirstOrDefault(host => host.Kind == preferred);
        if (match is not null)
        {
            return match;
        }

        var fallback = _hosts[0];
        _logger.LogWarning(
            "The preferred RDP connector {Preferred} is not registered; falling back to {Fallback}.",
            preferred, fallback.Kind);
        return fallback;
    }
}
