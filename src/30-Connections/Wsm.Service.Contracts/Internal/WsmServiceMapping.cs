using System.Linq;
using Wsm.Application;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Wsm.Service.Contracts.Grpc.Generated;

namespace Wsm.Service.Contracts.Internal;

/// <summary>
/// Static mapping helpers between the domain/application types and the generated
/// <c>wsm.service.WsmService</c> proto messages. Shared by both the service host
/// (domain -&gt; proto) and the client (proto -&gt; domain), so the helpers are
/// public even though they live in the <c>Internal</c> namespace.
/// </summary>
/// <remarks>
/// The wire contract is intentionally thin: enums travel as their integer value
/// and identifiers as strings. Identifiers that cannot be parsed on the way in
/// raise <see cref="SandboxValidationException"/> so a malformed request never
/// reaches the backend.
/// </remarks>
public static class WsmServiceMapping
{
    /// <summary>Wire value of <see cref="RdpTransportKind.NamedPipe"/> (see the .proto).</summary>
    public const int NamedPipeTransport = 0;

    /// <summary>Wire value of <see cref="RdpTransportKind.Tcp"/> (see the .proto).</summary>
    public const int TcpTransport = 1;

    // ---- SandboxInstanceId <-> string ------------------------------------

    /// <summary>Formats a sandbox id as its canonical <c>"D"</c> GUID string.</summary>
    public static string ToProto(this SandboxInstanceId id) => id.ToString();

    /// <summary>
    /// Parses a wire sandbox id. Throws <see cref="SandboxValidationException"/>
    /// when the value is not a GUID.
    /// </summary>
    public static SandboxInstanceId ToSandboxInstanceId(this string? sandboxId)
    {
        if (SandboxInstanceId.TryParse(sandboxId, out var id))
        {
            return id;
        }

        throw new SandboxValidationException(
            $"'{sandboxId}' is not a valid sandbox id (expected a GUID).");
    }

    // ---- enum <-> int32 --------------------------------------------------

    /// <summary>
    /// Maps <see cref="SandboxConnectionMode"/> to the proto <c>connection_mode</c>
    /// field. The numeric values are aligned (0 = FullDesktop, 1 = RemoteApp).
    /// </summary>
    public static int ToProto(this SandboxConnectionMode mode) => (int)mode;

    /// <summary>
    /// Maps the proto <c>connection_mode</c> field back to the domain enum.
    /// Throws <see cref="SandboxValidationException"/> for an undefined value so a
    /// malformed request never silently becomes a garbage (e.g. FullDesktop) mode.
    /// </summary>
    public static SandboxConnectionMode ToSandboxConnectionMode(this int connectionMode)
    {
        if (!Enum.IsDefined(typeof(SandboxConnectionMode), connectionMode))
        {
            throw new SandboxValidationException(
                $"'{connectionMode}' is not a valid sandbox connection mode.");
        }

        return (SandboxConnectionMode)connectionMode;
    }

    /// <summary>
    /// Maps <see cref="RdpTransportKind"/> to the proto <c>transport</c> field.
    /// </summary>
    /// <remarks>
    /// The .proto only distinguishes <c>0 = NamedPipe</c> and <c>1 = Tcp</c>, and
    /// <c>Tcp = 1</c> collides with the domain's <see cref="RdpTransportKind.Local"/>
    /// value of 1. To keep the wire stable, <see cref="RdpTransportKind.Local"/> is
    /// normalised to <c>NamedPipe</c> (both are local, network-free transports).
    /// </remarks>
    public static int ToProto(this RdpTransportKind transport) => transport switch
    {
        RdpTransportKind.Tcp => TcpTransport,
        RdpTransportKind.NamedPipe => NamedPipeTransport,
        RdpTransportKind.Local => NamedPipeTransport,
        _ => NamedPipeTransport,
    };

    /// <summary>
    /// Maps the proto <c>transport</c> field back to the domain enum. Throws
    /// <see cref="SandboxValidationException"/> for an undefined wire value.
    /// </summary>
    public static RdpTransportKind ToRdpTransportKind(this int transport) => transport switch
    {
        TcpTransport => RdpTransportKind.Tcp,
        NamedPipeTransport => RdpTransportKind.NamedPipe,
        _ => throw new SandboxValidationException(
            $"'{transport}' is not a valid RDP transport."),
    };

    // ---- Guid <-> string -------------------------------------------------

    /// <summary>Formats a VM id as its canonical <c>"D"</c> GUID string.</summary>
    public static string ToProto(this Guid vmId) => vmId.ToString("D");

    /// <summary>Parses a wire VM id, returning <see cref="Guid.Empty"/> when absent/invalid.</summary>
    public static Guid ToVmId(this string? vmId)
        => Guid.TryParse(vmId, out var value) ? value : Guid.Empty;

    // ---- SandboxRuntimeInfo <-> RuntimeReply -----------------------------

    /// <summary>Projects a <see cref="SandboxRuntimeInfo"/> onto a <see cref="RuntimeReply"/>.</summary>
    public static RuntimeReply ToRuntimeReply(this SandboxRuntimeInfo runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);

        var reply = new RuntimeReply
        {
            SandboxId = runtime.SandboxId.ToProto(),
            VmId = runtime.VmId.ToProto(),
            Transport = runtime.Transport.ToProto(),
            RailMode = runtime.RailMode,
            RailProgram = runtime.RailProgram,
            FullScreen = runtime.FullScreen,
            WindowTitle = runtime.WindowTitle,
            UserName = runtime.UserName,
            Password = runtime.Password,
            IpAddress = runtime.IpAddress,
        };

        reply.IpAddresses.AddRange(runtime.IpAddresses);
        return reply;
    }

    /// <summary>Rebuilds a <see cref="SandboxRuntimeInfo"/> from a <see cref="RuntimeReply"/>.</summary>
    public static SandboxRuntimeInfo ToSandboxRuntimeInfo(this RuntimeReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        return new SandboxRuntimeInfo
        {
            SandboxId = reply.SandboxId.ToSandboxInstanceId(),
            VmId = reply.VmId.ToVmId(),
            Transport = reply.Transport.ToRdpTransportKind(),
            RailMode = reply.RailMode,
            RailProgram = reply.RailProgram,
            FullScreen = reply.FullScreen,
            WindowTitle = string.IsNullOrEmpty(reply.WindowTitle)
                ? SandboxRuntimeInfoWindowTitleDefault
                : reply.WindowTitle,
            UserName = reply.UserName,
            Password = reply.Password,
            IpAddress = reply.IpAddress,
            IpAddresses = reply.IpAddresses.ToArray(),
        };
    }

    // ---- SandboxInstanceDto -> RuntimeReply ------------------------------

    /// <summary>
    /// Projects an application <see cref="SandboxInstanceDto"/> onto a
    /// <see cref="RuntimeReply"/>, preferring its live <see cref="SandboxInstanceDto.Runtime"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="RuntimeReply"/> carries no state or name field, so when the
    /// instance has no live runtime only its identity (<c>sandbox_id</c>) is sent.
    /// </remarks>
    public static RuntimeReply ToRuntimeReply(this SandboxInstanceDto instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (instance.Runtime is not null)
        {
            return instance.Runtime.ToRuntimeReply();
        }

        return new RuntimeReply
        {
            SandboxId = SandboxInstanceId.From(instance.Id).ToProto(),
        };
    }

    // ---- GuestCommandResult <-> ExecuteReply -----------------------------

    /// <summary>Maps an <see cref="ExecuteReply"/> onto a <see cref="GuestCommandResult"/>.</summary>
    public static GuestCommandResult ToGuestCommandResult(this ExecuteReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return new GuestCommandResult(reply.ExitCode, reply.StandardOutput, reply.StandardError);
    }

    /// <summary>Maps a <see cref="GuestCommandResult"/> onto an <see cref="ExecuteReply"/>.</summary>
    public static ExecuteReply ToExecuteReply(this GuestCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new ExecuteReply
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
        };
    }

    // ---- MappedFolder <-> MapFolderRequest -------------------------------

    /// <summary>
    /// Builds a <see cref="MapFolderRequest"/> for a sandbox from a domain
    /// <see cref="MappedFolder"/>. Note the proto carries the folder's
    /// <see cref="MappedFolder.ReadOnly"/> flag (not its inverse "allow write").
    /// </summary>
    public static MapFolderRequest ToMapFolderRequest(this MappedFolder folder, SandboxInstanceId sandboxId)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return new MapFolderRequest
        {
            SandboxId = sandboxId.ToProto(),
            HostFolder = folder.HostFolder,
            SandboxFolder = folder.SandboxFolder,
            ReadOnly = folder.ReadOnly,
        };
    }

    /// <summary>Rebuilds a domain <see cref="MappedFolder"/> from a <see cref="MapFolderRequest"/>.</summary>
    public static MappedFolder ToMappedFolder(this MapFolderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new MappedFolder(request.HostFolder, request.SandboxFolder, request.ReadOnly);
    }

    // ---- reply builders --------------------------------------------------

    /// <summary>Builds an <see cref="EnumerateReply"/> from sandbox ids.</summary>
    public static EnumerateReply ToEnumerateReply(IEnumerable<SandboxInstanceId> sandboxIds)
    {
        ArgumentNullException.ThrowIfNull(sandboxIds);

        var reply = new EnumerateReply();
        reply.SandboxIds.AddRange(sandboxIds.Select(id => id.ToProto()));
        return reply;
    }

    /// <summary>Parses the sandbox ids out of an <see cref="EnumerateReply"/>, skipping invalid entries.</summary>
    public static IReadOnlyList<SandboxInstanceId> ToSandboxInstanceIds(this EnumerateReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        var ids = new List<SandboxInstanceId>(reply.SandboxIds.Count);
        foreach (var raw in reply.SandboxIds)
        {
            if (SandboxInstanceId.TryParse(raw, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>Builds an <see cref="IpAddressesReply"/> from a set of addresses.</summary>
    public static IpAddressesReply ToIpAddressesReply(IEnumerable<string> ipAddresses)
    {
        ArgumentNullException.ThrowIfNull(ipAddresses);

        var reply = new IpAddressesReply();
        reply.IpAddresses.AddRange(ipAddresses);
        return reply;
    }

    /// <summary>Materialises the addresses out of an <see cref="IpAddressesReply"/>.</summary>
    public static IReadOnlyList<string> ToIpAddresses(this IpAddressesReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply.IpAddresses.ToArray();
    }

    // ---- Container desktop -----------------------------------------------

    /// <summary>
    /// Applies a <see cref="StartDesktopRequest"/>'s overrides to a base desktop
    /// plan (from the sandbox's recipe): an explicit provider id wins over the
    /// kind, a zero kind means "the recipe decides", and <c>present</c> replaces
    /// the plan's <see cref="SandboxDesktopPlan.PresentToInteractiveSession"/>.
    /// Throws <see cref="SandboxValidationException"/> for an undefined kind so a
    /// malformed request never reaches the channel.
    /// </summary>
    public static SandboxDesktopPlan ToDesktopPlan(this StartDesktopRequest request, SandboxDesktopPlan basePlan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(basePlan);

        var plan = basePlan with { PresentToInteractiveSession = request.Present };

        if (request.Kind.ToContainerDesktopKindOverride() is { } kind)
        {
            plan = plan with { Kind = kind };
        }

        if (!string.IsNullOrWhiteSpace(request.Provider))
        {
            plan = plan with { Provider = request.Provider };
        }

        return plan;
    }

    /// <summary>
    /// Maps the proto <c>kind</c> override field to a <see cref="ContainerDesktopKind"/>,
    /// where <c>0</c> means "not specified" (<c>null</c>). Throws
    /// <see cref="SandboxValidationException"/> for an undefined value.
    /// </summary>
    public static ContainerDesktopKind? ToContainerDesktopKindOverride(this int kind)
    {
        if (kind == 0)
        {
            return null;
        }

        if (!Enum.IsDefined(typeof(ContainerDesktopKind), kind))
        {
            throw new SandboxValidationException(
                $"'{kind}' is not a valid container desktop kind.");
        }

        return (ContainerDesktopKind)kind;
    }

    /// <summary>Maps the wire <c>kind</c> of a <see cref="DesktopStatusReply"/> to the domain enum.</summary>
    public static ContainerDesktopKind ToContainerDesktopKind(this int kind)
        => Enum.IsDefined(typeof(ContainerDesktopKind), kind)
            ? (ContainerDesktopKind)kind
            : ContainerDesktopKind.None;

    /// <summary>Builds a <see cref="DesktopStatusReply"/> describing a live channel.</summary>
    public static DesktopStatusReply ToDesktopStatusReply(
        string providerId,
        ContainerDesktopKind kind,
        string framePath,
        int width,
        int height,
        int fps,
        int? windowCount,
        int? viewerProcessId)
    {
        return new DesktopStatusReply
        {
            Running = true,
            ProviderId = providerId ?? string.Empty,
            Kind = (int)kind,
            FramePath = framePath ?? string.Empty,
            Width = width,
            Height = height,
            Fps = fps,
            // The proto cannot distinguish "no windows" from "not window-based",
            // so the frame-buffer shape travels as -1 (see the .proto).
            WindowCount = windowCount ?? -1,
            ViewerProcessId = viewerProcessId ?? 0,
        };
    }

    /// <summary>Builds the status of a sandbox with no live desktop channel.</summary>
    public static DesktopStatusReply ToNotRunningDesktopStatusReply() => new() { Running = false };

    /// <summary>Maps a <see cref="DesktopStatusReply"/> onto a client-side <see cref="ContainerDesktopStatus"/>.</summary>
    public static ContainerDesktopStatus ToContainerDesktopStatus(this DesktopStatusReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);

        if (!reply.Running)
        {
            return ContainerDesktopStatus.NotRunning;
        }

        return new ContainerDesktopStatus(
            Running: true,
            ProviderId: reply.ProviderId,
            Kind: reply.Kind.ToContainerDesktopKind(),
            FramePath: reply.FramePath,
            Width: reply.Width,
            Height: reply.Height,
            Fps: reply.Fps,
            WindowCount: reply.WindowCount < 0 ? null : reply.WindowCount,
            ViewerProcessId: reply.ViewerProcessId <= 0 ? null : reply.ViewerProcessId);
    }

    private const string SandboxRuntimeInfoWindowTitleDefault = "Windows Sandbox";
}
