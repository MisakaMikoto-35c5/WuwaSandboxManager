using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Host.Environment;
using Wsm.SandboxServer.Client.Grpc.Generated;

namespace Wsm.SandboxServer.Client.Internal;

/// <summary>
/// Default <see cref="ISandboxServerClient"/>. Drives the control-plane gRPC
/// service over the control named pipe and maps the server's HRESULT replies to
/// domain exceptions.
/// </summary>
[ExposeServices(typeof(ISandboxServerClient))]
internal sealed class WindowsSandboxServerClient : ISandboxServerClient, ISingletonDependency
{
    public const int CoEAppSingleUse = unchecked((int)0x800401F6);
    public const int ENotFound = unchecked((int)0x80070490);
    public const int ServerUnavailable = unchecked((int)0x800706BA);

    private readonly ISandboxEnvironment _environment;
    private readonly NamedPipeGrpcChannelFactory _channelFactory;
    private readonly ILogger<WindowsSandboxServerClient> _logger;

    public WindowsSandboxServerClient(
        ISandboxEnvironment environment,
        NamedPipeGrpcChannelFactory channelFactory,
        ILogger<WindowsSandboxServerClient> logger)
    {
        _environment = environment;
        _channelFactory = channelFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SandboxInstanceId>> EnumerateRunningAsync(CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.EnumerateSandboxVMsAsync(new Empty(), cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "EnumerateSandboxVMs");

        return reply.SandboxIds
            .Select(id => SandboxInstanceId.TryParse(id, out var parsed) ? parsed : (SandboxInstanceId?)null)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToArray();
    }

    public async Task<SandboxRuntimeInfo> StartAsync(
        SandboxInstanceId id,
        string recipeXml,
        CancellationToken cancellationToken = default)
    {
        var request = new StartRequest
        {
            Config = recipeXml,
            Id = id.ToString(),
        };

        var reply = await WithClientAsync(c => c.StartSandboxAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        _logger.LogInformation(
            "StartSandbox request: hasId={HasId} id={Id} hasConfig={HasConfig} configLength={ReqLen}; reply: hresult=0x{HResult:X8} configLength={Length}",
            request.HasId, request.Id, request.HasConfig, recipeXml?.Length ?? -1,
            reply.Hresult, reply.RdpClientConfig?.Length ?? -1);
        ThrowOnHresult(reply.Hresult, "StartSandbox");

        var runtime = RdpClientConfigParser.Parse(reply.RdpClientConfig, id);
        if (string.IsNullOrWhiteSpace(reply.RdpClientConfig) || runtime.VmId == Guid.Empty)
        {
            throw new SandboxBackendException(
                "WindowsSandboxServer returned an empty runtime configuration; the instance was not created.",
                0);
        }

        _logger.LogInformation("Started sandbox {SandboxId} through the control plane", id);
        return runtime;
    }

    public async Task ShutdownAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await WithClientAsync(c => c.ShutdownSandboxAsync(
                new ShutdownRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            ThrowOnHresult(reply.Hresult, "ShutdownSandbox");
        }
        catch (SandboxNotFoundException)
        {
            // Already gone; treat as stopped.
        }
    }

    public async Task MapFolderAsync(
        SandboxInstanceId id,
        string hostPath,
        string sandboxPath,
        bool allowWrite,
        CancellationToken cancellationToken = default)
    {
        var request = new MapFolderRequest
        {
            SandboxId = id.ToString(),
            HostPath = hostPath,
            SandboxPath = sandboxPath,
            AllowWrite = allowWrite,
        };

        var reply = await WithClientAsync(c => c.MapFolderAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "MapFolder");
    }

    public async Task<int> ExecuteAsync(SandboxInstanceId id, string commandLine, CancellationToken cancellationToken = default)
    {
        var request = new CreateProcessRequest
        {
            SandboxId = id.ToString(),
            CommandLine = commandLine,
            WorkingDirectory = string.Empty,
            RunAs = RunAs.System,
        };

        var reply = await WithClientAsync(c => c.CreateProcessAsync(request, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "CreateProcess");
        return reply.ProcessExitCode;
    }

    public async Task<IReadOnlyList<string>> GetIpAddressesAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.GetIpAddressAsync(
            new IpAddressRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "GetIpAddress");
        return reply.IpAddresses.ToArray();
    }

    public async Task<SandboxRuntimeInfo?> GetRuntimeInfoAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        try
        {
            var reply = await WithClientAsync(c => c.GetRdpClientConfigAsync(
                new RdpClientConfigRequest { SandboxId = id.ToString() }, cancellationToken: cancellationToken)).ConfigureAwait(false);
            ThrowOnHresult(reply.Hresult, "GetRdpClientConfig");
            return RdpClientConfigParser.Parse(reply.RdpClientConfig, id);
        }
        catch (SandboxNotFoundException)
        {
            return null;
        }
    }

    public async Task<SandboxTerminationReason> WaitForTerminationAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
    {
        var reply = await WithClientAsync(c => c.GetTerminateReasonAsync(
            new VMTerminateReasonRequest { SandboxId = id.ToString() },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        ThrowOnHresult(reply.Hresult, "GetTerminateReason");
        return (SandboxTerminationReason)reply.TerminatedReason;
    }

    private async Task<T> WithClientAsync<T>(Func<SandboxCore.SandboxCoreClient, AsyncUnaryCall<T>> action)
    {
        if (!_environment.EnsureServerRunning())
        {
            throw new SandboxNotSupportedException(
                "The Windows Sandbox control server (WindowsSandboxServer.exe) could not be started. " +
                "Ensure the Windows Sandbox optional feature is enabled.");
        }

        using var channel = _channelFactory.CreateChannel();
        var client = new SandboxCore.SandboxCoreClient(channel);

        try
        {
            return await action(client).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            var code = ex.StatusCode == StatusCode.Unavailable ? ServerUnavailable : 0;
            throw new SandboxBackendException(
                $"Windows Sandbox control-plane call failed: {ex.StatusCode} - {ex.Status.Detail}", code, ex);
        }
    }

    private static void ThrowOnHresult(int hresult, string operation)
    {
        if (hresult == 0)
        {
            return;
        }

        if (hresult == CoEAppSingleUse)
        {
            throw new MultipleInstancesNotAllowedException(
                "The host policy only allows a single Windows Sandbox instance at a time " +
                "(CO_E_APPSINGLEUSE). Stop the running instance, or use a multi-instance capable backend.");
        }

        if (hresult == ENotFound)
        {
            throw new SandboxNotFoundException($"The sandbox was not found while calling {operation}.");
        }

        throw new SandboxBackendException(
            $"Windows Sandbox operation '{operation}' failed with HRESULT 0x{hresult:X8}.", hresult);
    }
}
