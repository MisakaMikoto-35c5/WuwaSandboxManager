using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionLauncher"/>. Every sandbox engine is presented
/// through the <b>selectable RDP connector</b>: the request is projected from the
/// live runtime, then handed to <see cref="IRdpSessionHost"/>, which delegates to
/// the WPF or WinUI 3 presentation chosen by <c>IRdpConnectorPreference</c>.
/// <para>
/// This deliberately covers the Windows Sandbox engine as well as the HCS engine,
/// so the platform's <c>wsb connect</c> / packaged <c>WindowsSandboxRemoteSession</c>
/// client is no longer part of the connection path. The Windows Sandbox control
/// plane still supplies the VM id and guest credential (from its
/// <c>RdpClientConfig</c>), so the same connector works for it.
/// </para>
/// </summary>
[ExposeServices(typeof(ISessionLauncher))]
internal sealed class SessionLauncher : ISessionLauncher, ISingletonDependency
{
    private readonly IRdpSessionHost _rdpHost;
    private readonly ILogger<SessionLauncher> _logger;

    public SessionLauncher(IRdpSessionHost rdpHost, ILogger<SessionLauncher> logger)
    {
        _rdpHost = rdpHost;
        _logger = logger;
    }

    public Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        SandboxEngineKind engineKind,
        string? remoteAppProgram = null,
        CancellationToken cancellationToken = default)
    {
        var request = RdpSessionRequestFactory.Create(id, runtime, mode, remoteAppProgram);

        var endpoint = request.ServerAddress is { Length: > 0 }
            ? $"{request.ServerAddress}:{request.ServerPort}"
            : request.PipeName;
        _logger.LogInformation(
            "Presenting sandbox {SandboxId} (engine={EngineKind}) over {Endpoint} as {Presentation}{Program} through the RDP connector.",
            id, engineKind, endpoint, request.RemoteApp ? "RAIL" : "full desktop",
            request.RemoteApp ? $" (program={request.RemoteAppProgram})" : string.Empty);

        return _rdpHost.ShowAsync(request, cancellationToken);
    }

    public void CloseAll() => _rdpHost.CloseAll();
}
