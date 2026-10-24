using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Wsm.Application;
using Wsm.Connection.ContainerDesktop;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Service.Contracts.Grpc.Generated;
using Wsm.Service.Contracts.Internal;

namespace Wsm.Service.Host.Internal;

/// <summary>
/// Server-side implementation of the <c>wsm.service.WsmService</c> contract.
/// Every RPC is forwarded to <see cref="ISandboxManager"/>, which owns the engine
/// instances — so the sandbox VMs (and their process-local running references)
/// live in this service process and stay reachable across client calls.
/// </summary>
/// <remarks>
/// Error convention: domain exceptions are translated to gRPC statuses with
/// <see cref="WsmServiceStatus.ToStatus"/> so clients can reconstruct them (see
/// <c>WsmServiceExceptionMapper</c>). The per-process RDP UI (<c>connect</c>) is
/// intentionally not exposed — clients call <c>PrepareConnect</c> (server-side
/// reclaim + engine-backed runtime) and then launch the session themselves;
/// <c>GetRuntime</c> remains a read-only inspection call.
/// </remarks>
public partial class WsmServiceGrpc : WsmService.WsmServiceBase
{
    private readonly ISandboxManager _sandboxManager;
    private readonly IContainerDesktopHost _containerDesktop;
    private readonly IHostApplicationLifetime _lifetime;

    public WsmServiceGrpc(
        ISandboxManager sandboxManager,
        IContainerDesktopHost containerDesktop,
        IHostApplicationLifetime lifetime)
    {
        _sandboxManager = sandboxManager;
        _containerDesktop = containerDesktop;
        _lifetime = lifetime;
    }

    /// <summary>Lists the ids of the currently running sandbox instances.</summary>
    public override async Task<EnumerateReply> Enumerate(Empty request, ServerCallContext context)
    {
        try
        {
            var running = await _sandboxManager.ListRunningAsync(cancellationToken: context.CancellationToken);
            return WsmServiceMapping.ToEnumerateReply(running.Select(SandboxInstanceId.From));
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Starts an existing instance and returns its runtime parameters.</summary>
    public override async Task<RuntimeReply> Start(StartRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var mode = request.ConnectionMode.ToSandboxConnectionMode();
            var instance = await _sandboxManager.StartAsync(id.Value, mode, context.CancellationToken);
            return instance.ToRuntimeReply();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Stops a running instance.</summary>
    public override async Task<Empty> Stop(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            await _sandboxManager.StopAsync(id.Value, context.CancellationToken);
            return new Empty();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>
    /// Returns the live runtime parameters of an instance. When it is not
    /// currently running only the <c>sandbox_id</c> is populated (the client
    /// treats an empty <c>vm_id</c> as "no runtime").
    /// </summary>
    /// <remarks>
    /// The runtime is transient (it lives in the owning engine's in-memory table,
    /// never in the persisted <c>.wsm</c> file), so it is resolved through
    /// <see cref="ISandboxManager.GetRuntimeAsync"/> rather than read off the
    /// persisted instance.
    /// </remarks>
    public override async Task<RuntimeReply> GetRuntime(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var runtime = await _sandboxManager.GetRuntimeAsync(id.Value, context.CancellationToken);
            return runtime is not null
                ? runtime.ToRuntimeReply()
                : new RuntimeReply { SandboxId = id.ToProto() };
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>
    /// Server-side pre-connect preparation: resolves the instance's live runtime
    /// and reclaims any orphaned guest session before the client establishes the
    /// session itself.
    /// </summary>
    /// <remarks>
    /// The reclaim happens here so the client's subsequent connect can launch RDP
    /// with fresh parameters; the session itself stays client-side (plan §8.5).
    /// An instance that is not running surfaces as a failed-precondition status.
    /// </remarks>
    public override async Task<RuntimeReply> PrepareConnect(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var runtime = await _sandboxManager.PrepareConnectAsync(id.Value, context.CancellationToken);
            return runtime.ToRuntimeReply();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>
    /// Runs a command inside the guest. The application layer only surfaces the
    /// exit code, so <c>stdout</c>/<c>stderr</c> are returned empty.
    /// </summary>
    public override async Task<ExecuteReply> Execute(ExecuteRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var exitCode = await _sandboxManager.ExecuteAsync(id.Value, request.CommandLine, context.CancellationToken);
            return GuestCommandResult.FromExitCode(exitCode).ToExecuteReply();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Maps a host folder into a running instance.</summary>
    public override async Task<Empty> MapFolder(MapFolderRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var folder = request.ToMappedFolder();
            await _sandboxManager.MapFolderAsync(id.Value, folder, context.CancellationToken);
            return new Empty();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Returns the guest IP addresses of a running instance.</summary>
    public override async Task<IpAddressesReply> GetIpAddresses(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var addresses = await _sandboxManager.GetIpAddressesAsync(id.Value, context.CancellationToken);
            return WsmServiceMapping.ToIpAddressesReply(addresses);
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>
    /// Shuts the agent down. Used to stop a standalone (console) agent; an
    /// SCM-installed service is stopped through the Service Control Manager
    /// instead. The reply is sent first; the process exit follows after a short
    /// delay so the client is not cut off mid-response.
    /// </summary>
    public override Task<Empty> Shutdown(Empty request, ServerCallContext context)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(ShutdownReplyDelay).ConfigureAwait(false);
            _lifetime.StopApplication();
        });

        return Task.FromResult(new Empty());
    }

    /// <summary>
    /// Starts the container desktop channel for a process-isolated container, or
    /// adopts the live one when it is already running, and reports its status.
    /// </summary>
    /// <remarks>
    /// The channel must live in this process: the running container (and the
    /// engine capability that exposes its filesystem root) belongs to the agent,
    /// not to the caller. Presenting stays split the same way as RDP
    /// (<c>PrepareConnect</c>): with <c>present = false</c> the caller reads the
    /// frame path out of the reply and launches its own viewer.
    /// </remarks>
    public override async Task<DesktopStatusReply> StartDesktop(StartDesktopRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();

            var existing = _containerDesktop.Get(id);
            if (existing is not null)
            {
                // Adopt the live channel; (re-)present only when asked.
                var adopted = request.Present
                    ? await _containerDesktop.PresentAsync(id, context.CancellationToken)
                    : existing;
                return ToDesktopStatusReply(adopted);
            }

            var instance = await _sandboxManager.GetAsync(id.Value, context.CancellationToken);
            var plan = request.ToDesktopPlan(instance.Recipe.Desktop);
            var session = await _containerDesktop.StartAsync(
                id, instance.Recipe with { Desktop = plan }, context.CancellationToken);
            return ToDesktopStatusReply(session);
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Stops the container desktop channel; no live channel is a no-op.</summary>
    public override async Task<Empty> StopDesktop(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            if (_containerDesktop.Get(id) is not null)
            {
                await _containerDesktop.StopAsync(id, context.CancellationToken);
            }

            return new Empty();
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    /// <summary>Reports the container desktop channel's status without changing it.</summary>
    public override Task<DesktopStatusReply> GetDesktopStatus(SandboxIdRequest request, ServerCallContext context)
    {
        try
        {
            var id = request.SandboxId.ToSandboxInstanceId();
            var session = _containerDesktop.Get(id);
            return Task.FromResult(session is null
                ? WsmServiceMapping.ToNotRunningDesktopStatusReply()
                : ToDesktopStatusReply(session));
        }
        catch (Exception ex)
        {
            if (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new RpcException(WsmServiceStatus.ToStatus(ex));
        }
    }

    private static DesktopStatusReply ToDesktopStatusReply(ContainerDesktopSession session)
        => WsmServiceMapping.ToDesktopStatusReply(
            session.ProviderId,
            KindOf(session.ProviderId),
            session.FramePath,
            session.Geometry.Width,
            session.Geometry.Height,
            session.Geometry.Fps,
            session.Windows?.Count,
            session.ViewerProcessId);

    /// <summary>The kind a provider produces, for the wire; unknown providers report <c>None</c>.</summary>
    private static ContainerDesktopKind KindOf(string providerId) => providerId switch
    {
        ContainerDesktopProviderIds.FrameBuffer => ContainerDesktopKind.FrameBuffer,
        ContainerDesktopProviderIds.WindowMirror => ContainerDesktopKind.WindowMirror,
        _ => ContainerDesktopKind.None,
    };

    /// <summary>Delay between the Shutdown reply and the process stop. Settable from tests.</summary>
    internal TimeSpan ShutdownReplyDelay { get; set; } = TimeSpan.FromMilliseconds(500);
}
