using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// Resolves which RDP connector the host should present sessions through. The
/// preference is user-scoped (the <c>RdpConnector</c> field of
/// <c>ui-settings.json</c>), overridable by the <c>WSM_RDP_CONNECTOR</c>
/// environment variable, and defaults to <see cref="RdpConnectorKind.Wpf"/>.
/// </summary>
public interface IRdpConnectorPreference
{
    RdpConnectorKind Connector { get; }
}
