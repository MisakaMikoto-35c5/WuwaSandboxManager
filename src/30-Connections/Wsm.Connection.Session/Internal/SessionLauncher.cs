using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.RailClient;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionLauncher"/>. Routes by connection mode:
/// <list type="bullet">
/// <item>
/// <b>RemoteApp (RAIL)</b> goes through <see cref="IRailClientSessionHost"/>: a
/// dedicated WDAG-style client hosts the <c>mstscax</c> control over the RDPBASE
/// named pipe (or TCP) and presents each remote application as a
/// <b>free-floating top-level window</b> — the way <c>hvsirdpclient.exe</c> and
/// the platform's remote-session client did it. A small status dialog brokers
/// the session and gets out of the way (empty window region) once the
/// application window appears.
/// </item>
/// <item>
/// <b>Full desktop</b> goes through the selectable RDP connector
/// (<see cref="IRdpSessionHost"/>): the live runtime is projected into an
/// <see cref="RdpSessionRequest"/> and handed to the WPF or WinUI 3 presentation
/// chosen by <c>IRdpConnectorPreference</c>.
/// </item>
/// </list>
/// <para>
/// The Windows Sandbox control plane still supplies the VM id and guest
/// credential (from its <c>RdpClientConfig</c>), so both connectors work for it.
/// </para>
/// </summary>
[ExposeServices(typeof(ISessionLauncher))]
internal sealed class SessionLauncher : ISessionLauncher, ISingletonDependency
{
    private readonly IRdpSessionHost _rdpHost;
    private readonly IRailClientSessionHost _railClientHost;
    private readonly ILogger<SessionLauncher> _logger;

    public SessionLauncher(
        IRdpSessionHost rdpHost,
        IRailClientSessionHost railClientHost,
        ILogger<SessionLauncher> logger)
    {
        _rdpHost = rdpHost;
        _railClientHost = railClientHost;
        _logger = logger;
    }

    public Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        SandboxEngineKind engineKind,
        string? remoteAppProgram = null,
        string? remoteAppArguments = null,
        CancellationToken cancellationToken = default)
    {
        var remoteApp = runtime.RailMode || mode == SandboxConnectionMode.RemoteApp;
        if (remoteApp)
        {
            return LaunchRemoteAppAsync(id, runtime, engineKind, remoteAppProgram, remoteAppArguments, cancellationToken);
        }

        return LaunchDesktopAsync(id, runtime, mode, engineKind, cancellationToken);
    }

    /// <summary>
    /// Presents the session as a RemoteApp through the WDAG-style client. The
    /// RAIL program is an explicit per-connect override, then the recipe's pinned
    /// program, then the platform default.
    /// </summary>
    private Task LaunchRemoteAppAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxEngineKind engineKind,
        string? remoteAppProgram,
        string? remoteAppArguments,
        CancellationToken cancellationToken)
    {
        var program = FirstNonEmpty(remoteAppProgram, runtime.RailProgram, SandboxRecipe.DefaultRailProgram)!;
        var useTcp = runtime.Transport == RdpTransportKind.Tcp && !string.IsNullOrWhiteSpace(runtime.IpAddress);

        if (!useTcp && runtime.VmId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"The RemoteApp client requires a TCP address or a VM id for the RDPBASE named " +
                $"pipe, but sandbox {id} (engine {engineKind}) reports transport={runtime.Transport}, " +
                $"vmId={runtime.VmId:D}, ip='{runtime.IpAddress}'.");
        }

        var endpoint = useTcp ? $"{runtime.IpAddress}:3389" : runtime.NamedPipeEndpoint;
        _logger.LogInformation(
            "Presenting sandbox {SandboxId} (engine={EngineKind}) as a RemoteApp through the WDAG-style client over {Endpoint} (program={Program}).",
            id, engineKind, endpoint, program);

        return _railClientHost.ShowAsync(
            new RailClientSessionRequest
            {
                SandboxId = id.Value,
                PipeName = useTcp ? null : runtime.NamedPipeEndpoint,
                ServerAddress = useTcp ? runtime.IpAddress : null,
                VmId = runtime.VmId,
                Program = program,
                ProgramArguments = remoteAppArguments,
                WindowTitle = runtime.WindowTitle,
                ApplicationName = Path.GetFileNameWithoutExtension(program),
                UserName = runtime.UserName,
                Password = runtime.Password,
                Redirections = runtime.Redirections,
            },
            cancellationToken);
    }

    /// <summary>
    /// Presents the full guest desktop through the selectable RDP connector: the
    /// named pipe over RDPBASE, or the guest's TCP endpoint.
    /// </summary>
    private Task LaunchDesktopAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        SandboxEngineKind engineKind,
        CancellationToken cancellationToken)
    {
        var request = RdpSessionRequestFactory.Create(id, runtime, mode);

        var endpoint = request.ServerAddress is { Length: > 0 }
            ? $"{request.ServerAddress}:{request.ServerPort}"
            : request.PipeName;
        _logger.LogInformation(
            "Presenting sandbox {SandboxId} (engine={EngineKind}) over {Endpoint} as a full desktop through the RDP connector.",
            id, engineKind, endpoint);

        return _rdpHost.ShowAsync(request, cancellationToken);
    }

    public void CloseAll()
    {
        _rdpHost.CloseAll();
        _railClientHost.CloseAll();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
