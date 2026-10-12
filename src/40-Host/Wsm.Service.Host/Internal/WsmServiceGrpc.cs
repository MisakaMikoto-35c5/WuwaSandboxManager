using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Wsm.Application;
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

    public WsmServiceGrpc(ISandboxManager sandboxManager)
    {
        _sandboxManager = sandboxManager;
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
}
