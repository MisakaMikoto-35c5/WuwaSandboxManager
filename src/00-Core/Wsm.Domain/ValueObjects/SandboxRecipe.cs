namespace Wsm.Domain.ValueObjects;

/// <summary>
/// The complete, immutable configuration of a sandbox instance. This is the
/// domain representation of a Windows Sandbox <c>.wsb</c> recipe and is the
/// payload sent to <c>WindowsSandboxServer</c> (as XML) when starting an instance.
/// </summary>
public sealed record SandboxRecipe
{
    public SandboxName Name { get; init; } = SandboxName.Default();

    public SandboxMemoryConfiguration Memory { get; init; } = SandboxMemoryConfiguration.Default();

    public SandboxCpuConfiguration Cpu { get; init; } = SandboxCpuConfiguration.Default();

    public NetworkingMode Networking { get; init; } = NetworkingMode.Default;

    public VirtualGpuMode VirtualGpu { get; init; } = VirtualGpuMode.Default;

    public SandboxRedirections Redirections { get; init; } = SandboxRedirections.Default;

    /// <summary>User-data disk configuration (location/size).</summary>
    public SandboxStorageOptions Storage { get; init; } = SandboxStorageOptions.Default;

    public RdpTransportKind Transport { get; init; } = RdpTransportKind.NamedPipe;

    /// <summary>When true the sandbox is presented as RemoteApp/RAIL windows.</summary>
    public bool RailMode { get; init; }

    /// <summary>
    /// The program a RAIL session launches by default, e.g.
    /// <c>C:\Windows\explorer.exe</c>. Empty/null means the platform default
    /// (<see cref="DefaultRailProgram"/>). Ignored for full-desktop sessions.
    /// </summary>
    public string? RailProgram { get; init; }

    /// <summary>Boot the session in full screen mode.</summary>
    public bool FullScreen { get; init; }

    /// <summary>Protected Client (AppContainer isolation) mode.</summary>
    public bool ProtectedClient { get; init; }

    /// <summary>Support guest reboot without losing the sandbox.</summary>
    public bool RebootSupport { get; init; }

    /// <summary>Persist the sandbox storage between sessions.</summary>
    public bool PersistenceSupport { get; init; }

    public IReadOnlyList<MappedFolder> MappedFolders { get; init; } = Array.Empty<MappedFolder>();

    public IReadOnlyList<LogonCommand> LogonCommands { get; init; } = Array.Empty<LogonCommand>();

    /// <summary>Optional explicit account user/password for the guest.</summary>
    public string? AccountUser { get; init; }

    public string? AccountPassword { get; init; }

    public static SandboxRecipe Default { get; } = new();

    /// <summary>
    /// The program a RAIL session starts when the recipe does not pin one. The
    /// WDAG guest is RemoteApp-only, so a RAIL session must always launch
    /// something; the shell is the closest thing to "a desktop".
    /// </summary>
    public const string DefaultRailProgram = @"C:\Windows\explorer.exe";

    /// <summary>The RAIL program to launch, falling back to <see cref="DefaultRailProgram"/>.</summary>
    public string ResolvedRailProgram =>
        string.IsNullOrWhiteSpace(RailProgram) ? DefaultRailProgram : RailProgram!;

    public SandboxRecipe WithMappedFolder(MappedFolder folder)
    {
        var folders = MappedFolders.Where(f => !f.Equals(folder)).Append(folder).ToArray();
        return this with { MappedFolders = folders };
    }

    public SandboxRecipe WithoutMappedFolder(MappedFolder folder)
        => this with { MappedFolders = MappedFolders.Where(f => !f.Equals(folder)).ToArray() };

    public SandboxRecipe WithLogonCommand(LogonCommand command)
    {
        var commands = LogonCommands.Where(c => c != command).Append(command).ToArray();
        return this with { LogonCommands = commands };
    }

    /// <summary>Produces a copy tuned for a connection mode.</summary>
    public SandboxRecipe ForConnectionMode(SandboxConnectionMode mode)
        => this with { RailMode = mode == SandboxConnectionMode.RemoteApp };
}
