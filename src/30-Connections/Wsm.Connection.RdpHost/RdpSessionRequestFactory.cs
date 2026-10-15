using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RdpHost;

/// <summary>
/// Builds an <see cref="RdpSessionRequest"/> from a live
/// <see cref="SandboxRuntimeInfo"/>. Shared by the in-process session launcher
/// and the connector windows (which rebuild the request to reconnect after a
/// restart).
/// </summary>
public static class RdpSessionRequestFactory
{
    /// <summary>
    /// Projects a runtime into a connector request, applying the RAIL program
    /// precedence (explicit override, then the recipe-pinned program, then the
    /// platform default) and validating the transport.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The runtime carries neither a usable TCP address nor a named-pipe endpoint
    /// with a VM id.
    /// </exception>
    public static RdpSessionRequest Create(
        SandboxInstanceId id,
        SandboxRuntimeInfo runtime,
        SandboxConnectionMode mode,
        string? remoteAppProgram = null)
    {
        var useTcp = runtime.Transport == RdpTransportKind.Tcp &&
                     !string.IsNullOrWhiteSpace(runtime.IpAddress);
        if (!useTcp && (runtime.Transport != RdpTransportKind.NamedPipe || runtime.VmId == Guid.Empty))
        {
            throw new InvalidOperationException(
                $"The RDP connector requires a TCP address or a named-pipe transport with a VM id, " +
                $"but sandbox {id} reports transport={runtime.Transport}, " +
                $"vmId={runtime.VmId:D}, ip='{runtime.IpAddress}'.");
        }

        var remoteApp = runtime.RailMode || mode == SandboxConnectionMode.RemoteApp;
        var program = remoteApp
            ? FirstNonEmpty(remoteAppProgram, runtime.RailProgram, SandboxRecipe.DefaultRailProgram)
            : null;

        return new RdpSessionRequest
        {
            SandboxId = id.Value,
            Mode = remoteApp ? SandboxConnectionMode.RemoteApp : mode,
            PipeName = runtime.NamedPipeEndpoint,
            ServerAddress = useTcp ? runtime.IpAddress : null,
            ServerPort = 3389,
            VmId = runtime.VmId,
            RemoteApp = remoteApp,
            RemoteAppProgram = program,
            WindowTitle = runtime.WindowTitle,
            FullScreen = runtime.FullScreen,
            UserName = runtime.UserName,
            Password = runtime.Password,
            Redirections = runtime.Redirections,
        };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
