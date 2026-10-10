using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Connection.Legacy;
using Wsm.Connection.RdpBase;
using Wsm.Connection.RdpHost;
using Wsm.Domain.ValueObjects;
using Wsm.Host.Environment;

namespace Wsm.Connection.Session.Internal;

/// <summary>
/// Default <see cref="ISessionLauncher"/>. Routes a connection to one of two real
/// paths, both behind the single <see cref="ISessionLauncher"/> contract:
/// <list type="bullet">
/// <item>
/// <b>Full desktop</b>: opens the existing sandbox <b>by id</b>. The platform CLI
/// (<c>wsb.exe connect --id &lt;id&gt;</c>) is preferred; the packaged client
/// (<c>WindowsSandboxRemoteSession.exe --id &lt;id&gt;</c>) is the equivalent
/// fallback. No <c>--config</c>/<c>.rdp</c> is passed: <c>wsb connect</c> accepts
/// <c>--id</c> only, and <c>--config</c> on <c>wsb start</c> expects an inline
/// settings string (e.g. <c>&lt;Configuration&gt;...&lt;/Configuration&gt;</c>),
/// never an <c>.rdp</c> path. The client's config type is the XML
/// <c>SandboxCommon.Config.RdpClientConfig</c>; there is no <c>.rdp</c> format.
/// </item>
/// <item>
/// <b>RemoteApp (RAIL)</b>: acquires the RDP network stream from the RDPBASE
/// named-pipe connector (<c>\\.\pipe\&lt;VmId&gt;</c>) and presents it through
/// <c>mstscax</c> (<c>MsRdpClient10</c>) with <c>RemoteProgramMode</c> enabled.
/// </item>
/// </list>
/// When neither packaged client is present it falls back to the legacy
/// <c>WindowsSandbox.exe</c> launcher, which starts a <b>new</b> sandbox rather
/// than connecting to the existing instance (see <see cref="LaunchLegacyAsync"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>HCS-created sandboxes (known gap).</b> The by-id path
/// (<c>wsb connect --id</c> / <c>WindowsSandboxRemoteSession.exe --id</c>) is
/// resolved by <c>WindowsSandboxServer</c>: the server must know the sandbox id.
/// A sandbox created directly through the HCS API (<c>Wsm.Engine.Hcs</c>) is not
/// registered with that server, so the by-id path cannot resolve it and will
/// fail to connect.
/// </para>
/// <para>
/// The only transport that does not depend on the server is the direct RDPBASE
/// named-pipe endpoint (<c>\\.\pipe&lt;VmId&gt;</c>), which the RAIL path already
/// uses. Extending that direct path to a <i>full desktop</i> additionally
/// requires the guest credential (<c>WDAGUtilityAccount</c>): its password is
/// neither provisioned by the HCS engine nor supplied by any caller today, so a
/// credential-less full desktop would fail at logon. It is therefore
/// deliberately not attempted. Until the HCS engine provisions (or the caller
/// supplies) that credential, an HCS-created sandbox can only be connected in
/// RAIL mode through the direct path; a full-desktop connect to it is
/// unsupported.
/// </para>
/// </remarks>
[ExposeServices(typeof(ISessionLauncher))]
internal sealed class SessionLauncher : ISessionLauncher, ISingletonDependency
{
    private readonly ISandboxEnvironment _environment;
    private readonly ILegacySandboxLauncher _legacyLauncher;
    private readonly IRdpBaseConnectorFactory _rdpBaseFactory;
    private readonly IRdpSessionHost _rdpHost;
    private readonly ISessionProcessLauncher _processLauncher;
    private readonly ILogger<SessionLauncher> _logger;

    public SessionLauncher(
        ISandboxEnvironment environment,
        ILegacySandboxLauncher legacyLauncher,
        IRdpBaseConnectorFactory rdpBaseFactory,
        IRdpSessionHost rdpHost,
        ISessionProcessLauncher processLauncher,
        ILogger<SessionLauncher> logger)
    {
        _environment = environment;
        _legacyLauncher = legacyLauncher;
        _rdpBaseFactory = rdpBaseFactory;
        _rdpHost = rdpHost;
        _processLauncher = processLauncher;
        _logger = logger;
    }

    public Task LaunchAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        CancellationToken cancellationToken = default)
    {
        var path = ConnectionRouter.Route(mode, runtime);
        _logger.LogInformation(
            "Connecting to sandbox {SandboxId} via {Path} (requested {Mode}, rail={RailMode}, transport={Transport})",
            id, path, mode, runtime.RailMode, runtime.Transport);

        return path == ConnectionPath.RemoteApp
            ? LaunchRemoteAppAsync(id, runtime, cancellationToken)
            : LaunchFullDesktopAsync(id, runtime, cancellationToken);
    }

    private Task LaunchFullDesktopAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        CancellationToken cancellationToken)
    {
        // Full desktop = open the existing sandbox by id. `wsb connect` takes
        // `--id` only (verified against wsb.exe on the host); passing `--config`
        // with an `.rdp` path is wrong — `--config` on `wsb start` is an inline
        // settings string, and no client binary understands a `.rdp` file.
        return LaunchPackagedConnectAsync(id, runtime);
    }

    private async Task LaunchRemoteAppAsync(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        CancellationToken cancellationToken)
    {
        if (runtime.Transport == RdpTransportKind.NamedPipe && runtime.VmId != Guid.Empty)
        {
            try
            {
                using var connector = _rdpBaseFactory.Create();
                var endpoint = await connector
                    .GetEndpointAsync(runtime.NamedPipeEndpoint, cancellationToken)
                    .ConfigureAwait(false);

                _logger.LogInformation(
                    "Acquired RDPBASE named-pipe endpoint {Endpoint} for sandbox {SandboxId}; presenting RAIL through mstscax.",
                    runtime.NamedPipeEndpoint, id);

                await _rdpHost.ShowAsync(
                    new RdpSessionRequest
                    {
                        Endpoint = endpoint,
                        RemoteApp = true,
                        WindowTitle = runtime.WindowTitle,
                        FullScreen = runtime.FullScreen,
                        UserName = runtime.UserName,
                        Password = runtime.Password,
                        Redirections = runtime.Redirections,
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "The direct RAIL path failed for sandbox {SandboxId}; falling back to the packaged client. " +
                    "The RDPBASE stream was acquired but could not be hosted.",
                    id);
            }
        }
        else
        {
            _logger.LogWarning(
                "Sandbox {SandboxId} does not expose a named-pipe endpoint (transport={Transport}, vmId={VmId}); the direct RAIL path is unavailable.",
                id, runtime.Transport, runtime.VmId);
        }

        await LaunchRemoteAppFallbackAsync(id, runtime).ConfigureAwait(false);
    }

    /// <summary>
    /// RAIL fallback. The packaged clients derive the presentation from the
    /// sandbox's own <c>RailMode</c> (baked in at start time), so connecting
    /// <c>by id</c> is the right call here as well.
    /// </summary>
    private Task LaunchRemoteAppFallbackAsync(SandboxInstanceId id, SandboxRuntimeInfo runtime)
    {
        _logger.LogWarning(
            "Falling back to the packaged client for the RAIL session of sandbox {SandboxId}.",
            id);
        return LaunchPackagedConnectAsync(id, runtime);
    }

    /// <summary>
    /// Opens the existing sandbox by id through the first available packaged
    /// client: the platform CLI <c>wsb connect --id &lt;id&gt;</c> first, then the
    /// packaged per-instance client <c>WindowsSandboxRemoteSession.exe --id
    /// &lt;id&gt;</c>. Both require the sandbox to be registered with
    /// <c>WindowsSandboxServer</c> (see the type remarks for the HCS gap). When
    /// neither is present the legacy launcher is used.
    /// </summary>
    private Task LaunchPackagedConnectAsync(SandboxInstanceId id, SandboxRuntimeInfo runtime)
    {
        var cli = _environment.SandboxCliExecutablePath;
        if (cli is not null)
        {
            _logger.LogInformation(
                "Opening sandbox {SandboxId} through the platform CLI: \"{Cli}\" connect --id {SandboxId}.",
                id, cli, id);

            _processLauncher.Launch(new ProcessStartInfo
            {
                FileName = cli,
                Arguments = $"connect --id {id}",
                UseShellExecute = true,
            });
            return Task.CompletedTask;
        }

        var client = _environment.RemoteSessionExecutablePath;
        if (client is not null)
        {
            _logger.LogInformation(
                "Opening sandbox {SandboxId} through the packaged client {Client} --id {SandboxId}.",
                id, client, id);

            _processLauncher.Launch(new ProcessStartInfo
            {
                FileName = client,
                Arguments = $"--id {id}",
                UseShellExecute = true,
            });
            return Task.CompletedTask;
        }

        return LaunchLegacyAsync(id, runtime);
    }

    /// <summary>
    /// Last-resort fallback. <b>This does not connect to the existing instance.</b>
    /// <c>WindowsSandbox.exe</c> has no notion of re-attaching to a running
    /// sandbox: it serializes a recipe (here <see cref="SandboxRecipe.Default"/>,
    /// tuned for the requested presentation) and starts a brand-new platform
    /// sandbox. It is a substitute, not a reconnect.
    /// </summary>
    private Task LaunchLegacyAsync(SandboxInstanceId id, SandboxRuntimeInfo runtime)
    {
        _logger.LogWarning(
            "No packaged Windows Sandbox client was found (wsb.exe / WindowsSandboxRemoteSession.exe). " +
            "The legacy fallback will START A NEW WindowsSandbox.exe sandbox from a default recipe " +
            "instead of connecting to the existing instance {SandboxId}.",
            id);

        var recipe = SandboxRecipe.Default with
        {
            RailMode = runtime.RailMode,
            FullScreen = runtime.FullScreen,
            Redirections = runtime.Redirections,
        };
        _legacyLauncher.Launch(id, recipe);
        return Task.CompletedTask;
    }
}
