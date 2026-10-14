using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionLauncher"/>. Every sandbox engine is presented
/// through the <b>custom RDP connector</b>: the host acquires the RDPBASE
/// named-pipe endpoint on an STA thread and shows it through the strongly typed
/// <c>mstscax</c> control.
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
        var useTcp = runtime.Transport == RdpTransportKind.Tcp &&
                     !string.IsNullOrWhiteSpace(runtime.IpAddress);
        if (!useTcp && (runtime.Transport != RdpTransportKind.NamedPipe || runtime.VmId == Guid.Empty))
        {
            throw new InvalidOperationException(
                $"The RDP connector requires a TCP address or a named-pipe transport with a VM id, " +
                $"but sandbox {id} (engine {engineKind}) reports transport={runtime.Transport}, " +
                $"vmId={runtime.VmId:D}, ip='{runtime.IpAddress}'.");
        }

        var remoteApp = runtime.RailMode || mode == SandboxConnectionMode.RemoteApp;

        // The RAIL program: an explicit per-connect override wins, then the
        // recipe's pinned program, then the platform default. Only meaningful in
        // RAIL mode.
        var program = remoteApp
            ? FirstNonEmpty(remoteAppProgram, runtime.RailProgram, SandboxRecipe.DefaultRailProgram)
            : null;

        var endpoint = useTcp ? $"{runtime.IpAddress}:3389" : runtime.NamedPipeEndpoint;
        _logger.LogInformation(
            "Presenting sandbox {SandboxId} (engine={EngineKind}) over {Endpoint} as {Presentation}{Program} through mstscax.",
            id, engineKind, endpoint, remoteApp ? "RAIL" : "full desktop",
            remoteApp ? $" (program={program})" : string.Empty);

        return _rdpHost.ShowAsync(
            new RdpSessionRequest
            {
                PipeName = runtime.NamedPipeEndpoint,
                ServerAddress = useTcp ? runtime.IpAddress : null,
                VmId = runtime.VmId,
                RemoteApp = remoteApp,
                RemoteAppProgram = program,
                WindowTitle = runtime.WindowTitle,
                FullScreen = runtime.FullScreen,
                UserName = runtime.UserName,
                Password = runtime.Password,
                Redirections = runtime.Redirections,
            },
            cancellationToken);
    }

    public void CloseAll() => _rdpHost.CloseAll();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
