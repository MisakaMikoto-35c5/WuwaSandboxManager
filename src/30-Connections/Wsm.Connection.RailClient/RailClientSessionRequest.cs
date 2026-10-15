using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RailClient;

/// <summary>
/// A request to present a sandbox <b>RemoteApp (RAIL)</b> session through the
/// WDAG-style client: the <c>mstscax</c> control hosted over the RDPBASE named
/// pipe, with the remote application shown as a free-floating window.
/// </summary>
public sealed record RailClientSessionRequest
{
    /// <summary>The sandbox the session belongs to (bookkeeping/logging).</summary>
    public required Guid SandboxId { get; init; }

    /// <summary>
    /// The RDPBASE named-pipe endpoint, i.e. <c>\\.\pipe&lt;VmId&gt;</c>. Used when
    /// <see cref="ServerAddress"/> is not set.
    /// </summary>
    public string? PipeName { get; init; }

    /// <summary>
    /// The guest TCP address to connect to instead of the named pipe; the pipe is
    /// the sandbox-native transport and the default.
    /// </summary>
    public string? ServerAddress { get; init; }

    /// <summary>The TCP port used with <see cref="ServerAddress"/>.</summary>
    public int ServerPort { get; init; } = 3389;

    /// <summary>The compute system / VM id; routes the named-pipe transport.</summary>
    public Guid VmId { get; init; }

    /// <summary>
    /// The program the RAIL session launches in the guest, e.g.
    /// <c>C:\Windows\explorer.exe</c>.
    /// </summary>
    public string Program { get; init; } = SandboxRecipe.DefaultRailProgram;

    /// <summary>Optional command line for <see cref="Program"/>.</summary>
    public string? ProgramArguments { get; init; }

    /// <summary>The sandbox display name shown by the status dialog.</summary>
    public string WindowTitle { get; init; } = "Windows Sandbox";

    /// <summary>The application display name shown on the status dialog.</summary>
    public string ApplicationName { get; init; } = "RemoteApp";

    public string UserName { get; init; } = string.Empty;

    public string Password { get; init; } = string.Empty;

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;
}
