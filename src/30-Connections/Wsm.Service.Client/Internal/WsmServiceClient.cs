using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Service.Contracts.Grpc.Generated;
using Wsm.Service.Contracts.Internal;
using ProtoService = Wsm.Service.Contracts.Grpc.Generated.WsmService;

namespace Wsm.Service.Client.Internal;

/// <summary>
/// Default <see cref="IWsmServiceClient"/>. Drives the WSM agent gRPC service over
/// the fixed control named pipe and maps gRPC statuses back to domain exceptions.
/// </summary>
[ExposeServices(typeof(IWsmServiceClient))]
internal sealed class WsmServiceClient : IWsmServiceClient, ISingletonDependency
{
    // Per-RPC deadlines. The guest-ready waits inside the engine run for
    // minutes, so a cold Start (or a long Execute) needs a generous ceiling;
    // everything else is quick and must fail fast. The caller's CancellationToken
    // is still passed through, so the earliest of deadline/cancellation wins.
    private static readonly TimeSpan StartDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExecuteDeadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MapFolderDeadline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GetRuntimeDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PrepareConnectDeadline = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EnumerateDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan GetIpAddressesDeadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(5);

    private readonly NamedPipeWsmServiceChannelFactory _channelFactory;
    private readonly IStandaloneAgentStarter _starter;

    public WsmServiceClient(NamedPipeWsmServiceChannelFactory channelFactory, IStandaloneAgentStarter starter)
    {
        _channelFactory = channelFactory;
        _starter = starter;
    }

    public async Task<IReadOnlyList<Guid>> EnumerateAsync(CancellationToken ct = default)
    {
        var reply = await InvokeAsync(
            (client, deadline, token) => client.EnumerateAsync(new Empty(), deadline: deadline, cancellationToken: token),
            ct,
            EnumerateDeadline).ConfigureAwait(false);

        return reply.ToSandboxInstanceIds().Select(id => id.Value).ToArray();
    }

    public async Task<SandboxRuntimeInfo> StartAsync(Guid id, SandboxConnectionMode mode, CancellationToken ct = default)
    {
        var request = new StartRequest
        {
            SandboxId = id.ToProto(),
            ConnectionMode = mode.ToProto(),
        };

        var reply = await InvokeAsync(
            (client, deadline, token) => client.StartAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            StartDeadline).ConfigureAwait(false);

        return reply.ToSandboxRuntimeInfo();
    }

    public async Task StopAsync(Guid id, CancellationToken ct = default)
    {
        var request = new SandboxIdRequest { SandboxId = id.ToProto() };

        await InvokeAsync(
            (client, deadline, token) => client.StopAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            StopDeadline).ConfigureAwait(false);
    }

    public async Task<SandboxRuntimeInfo?> GetRuntimeAsync(Guid id, CancellationToken ct = default)
    {
        var request = new SandboxIdRequest { SandboxId = id.ToProto() };

        var reply = await InvokeAsync(
            (client, deadline, token) => client.GetRuntimeAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            GetRuntimeDeadline).ConfigureAwait(false);

        // The instance exists (no NotFound) but has no live runtime when the VM id is empty.
        return string.IsNullOrEmpty(reply.VmId) ? null : reply.ToSandboxRuntimeInfo();
    }

    public async Task<SandboxRuntimeInfo> PrepareConnectAsync(Guid id, CancellationToken ct = default)
    {
        var request = new SandboxIdRequest { SandboxId = id.ToProto() };

        var reply = await InvokeAsync(
            (client, deadline, token) => client.PrepareConnectAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            PrepareConnectDeadline).ConfigureAwait(false);

        return reply.ToSandboxRuntimeInfo();
    }

    public async Task<GuestCommandResult> ExecuteAsync(Guid id, string commandLine, CancellationToken ct = default)
    {
        var request = new ExecuteRequest
        {
            SandboxId = id.ToProto(),
            CommandLine = commandLine,
        };

        var reply = await InvokeAsync(
            (client, deadline, token) => client.ExecuteAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            ExecuteDeadline).ConfigureAwait(false);

        return reply.ToGuestCommandResult();
    }

    public async Task MapFolderAsync(Guid id, MappedFolder folder, CancellationToken ct = default)
    {
        var request = folder.ToMapFolderRequest(SandboxInstanceId.From(id));

        await InvokeAsync(
            (client, deadline, token) => client.MapFolderAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            MapFolderDeadline).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(Guid id, CancellationToken ct = default)
    {
        var request = new SandboxIdRequest { SandboxId = id.ToProto() };

        var reply = await InvokeAsync(
            (client, deadline, token) => client.GetIpAddressesAsync(request, deadline: deadline, cancellationToken: token),
            ct,
            GetIpAddressesDeadline).ConfigureAwait(false);

        return reply.ToIpAddresses();
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        try
        {
            var client = new ProtoService.WsmServiceClient(_channelFactory.Channel);
            var deadline = DateTime.UtcNow.Add(NamedPipeWsmServiceChannelFactory.ProbeTimeout);
            await client.EnumerateAsync(new Empty(), deadline: deadline, cancellationToken: ct)
                .ResponseAsync.ConfigureAwait(false);
            return true;
        }
        catch (RpcException ex)
        {
            // Probe semantics = "is an agent answering on the pipe?" (plan §5), not
            // "did Enumerate succeed?". The server never synthesizes these statuses
            // (WsmServiceStatus maps domain failures to NotFound/FailedPrecondition/
            // Unknown), so anything else — Unknown/NotFound/FailedPrecondition with a
            // type-name detail — proves the agent is up (its backend may be degraded,
            // which callers surface on the real call). Transport failures against a
            // dead pipe surface as Cancelled (fresh channel) / DeadlineExceeded
            // (probe deadline) / Unavailable / Internal (warm channel).
            return ex.StatusCode is not (
                StatusCode.Unavailable or
                StatusCode.DeadlineExceeded or
                StatusCode.Cancelled or
                StatusCode.Internal);
        }
        catch
        {
            // Missing pipe, refused connect, channel failure → not available.
            return false;
        }
    }

    public Task<bool> EnsureAvailableAsync(string? dataFolder = null, CancellationToken ct = default)
        => _starter.EnsureRunningAsync(IsAvailableAsync, dataFolder, ct);

    public async Task ShutdownAsync(CancellationToken ct = default)
    {
        try
        {
            var client = new ProtoService.WsmServiceClient(_channelFactory.Channel);
            var deadline = DateTime.UtcNow.Add(ShutdownDeadline);
            await client.ShutdownAsync(new Empty(), deadline: deadline, cancellationToken: ct)
                .ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.Internal)
        {
            // The agent honoured the request and exited while the reply was in
            // flight: a torn transport right after Shutdown means success.
        }
    }

    private async Task<T> InvokeAsync<T>(
        Func<ProtoService.WsmServiceClient, DateTime?, CancellationToken, AsyncUnaryCall<T>> call,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        bool retryOnUnavailable = true)
    {
        var client = new ProtoService.WsmServiceClient(_channelFactory.Channel);

        try
        {
            return await CallAsync(call, client, timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
        {
            // The caller aborted the call: surface cancellation as cancellation so
            // auto-mode never mistakes a user cancel for an unreachable agent and
            // re-runs the operation in-process.
            throw new OperationCanceledException("The WSM agent service call was cancelled.", ex, cancellationToken);
        }
        catch (RpcException ex) when (retryOnUnavailable && ex.StatusCode == StatusCode.Unavailable)
        {
            // A stale pooled connection right after a service restart surfaces as
            // Unavailable. Retry exactly once (and only for Unavailable) so the
            // channel reconnects; every other status is terminal.
            try
            {
                return await CallAsync(call, client, timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException retryEx) when (retryEx.StatusCode == StatusCode.Cancelled && cancellationToken.IsCancellationRequested)
            {
                // Same caller-cancel guard as the outer catch: a cancel landing
                // during the retry must surface as cancellation, not as
                // "agent unreachable" (which would trigger the in-process fallback).
                throw new OperationCanceledException("The WSM agent service call was cancelled.", retryEx, cancellationToken);
            }
            catch (RpcException retryEx)
            {
                throw WsmServiceExceptionMapper.ToDomainException(retryEx);
            }
        }
        catch (RpcException ex)
        {
            throw WsmServiceExceptionMapper.ToDomainException(ex);
        }
    }

    private static async Task<T> CallAsync<T>(
        Func<ProtoService.WsmServiceClient, DateTime?, CancellationToken, AsyncUnaryCall<T>> call,
        ProtoService.WsmServiceClient client,
        TimeSpan? timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? NamedPipeWsmServiceChannelFactory.DefaultTimeout);
        return await call(client, deadline, cancellationToken).ResponseAsync.ConfigureAwait(false);
    }
}
