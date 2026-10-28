using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.RemoteApp;

/// <summary>
/// Describes a desktop shortcut that connects to one application inside a
/// sandbox as a RemoteApp session.
/// <para>
/// The shortcut targets the <c>wsm</c> CLI (<c>connect &lt;id&gt; --remoteapp
/// --program &lt;path&gt; --quiet</c>) instead of baking an <c>.rdp</c> file: the
/// CLI resolves the live guest address and credential at click time, so the
/// shortcut keeps working when the sandbox is restarted (the named-pipe
/// transport is bridged, and a TCP guest address may change between starts).
/// </para>
/// </summary>
public sealed record RemoteAppShortcutRequest
{
    /// <summary>The full path of the <c>.lnk</c> file to write (its folder must exist).</summary>
    public required string LinkPath { get; init; }

    /// <summary>The full path of the companion <c>wsm.exe</c> that the shortcut runs.</summary>
    public required string CliExecutablePath { get; init; }

    /// <summary>The sandbox the shortcut connects to.</summary>
    public required Guid SandboxId { get; init; }

    /// <summary>The guest program to launch as a RemoteApp.</summary>
    public string Program { get; init; } = SandboxRecipe.DefaultRailProgram;

    /// <summary>Optional command line for <see cref="Program"/>.</summary>
    public string? ProgramArguments { get; init; }

    /// <summary>Shortcut tooltip/description.</summary>
    public string? Description { get; init; }

    /// <summary>The icon file for the shortcut; defaults to the Remote Desktop client.</summary>
    public string? IconPath { get; init; }
}

/// <summary>
/// Writes desktop shortcuts for sandbox applications (see
/// <see cref="RemoteAppShortcutRequest"/>). Windows only — implemented with the
/// shell link COM interfaces, so no shell/scripting dependency is needed.
/// </summary>
public interface IRemoteAppShortcutWriter
{
    /// <summary>Creates (or overwrites) the shortcut described by <paramref name="request"/>.</summary>
    void Create(RemoteAppShortcutRequest request);
}
