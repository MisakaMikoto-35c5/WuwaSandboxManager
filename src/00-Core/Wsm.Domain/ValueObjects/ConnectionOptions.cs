using WindowsSandboxManager.Domain.Exceptions;

namespace WindowsSandboxManager.Domain.ValueObjects;

/// <summary>
/// Transport used to reach the guest RDP endpoint.
/// <para>
/// <see cref="NamedPipe"/> (the default) connects through
/// <c>\\.\pipe\&lt;VMId&gt;</c> using the undocumented <c>RDPBASE.dll</c>
/// named-pipe connector and works even when networking is disabled.
/// </para>
/// </summary>
public enum RdpTransportKind
{
    /// <summary>RDP over a per-VM named pipe (no networking required).</summary>
    NamedPipe = 0,

    /// <summary>Loopback / local transport.</summary>
    Local = 1,

    /// <summary>Classic RDP over TCP to the guest IP (requires networking).</summary>
    Tcp = 2,
}

/// <summary>
/// How the user wants to consume the sandbox.
/// <see cref="RemoteApp"/> corresponds to RAIL mode (individual windows, the
/// same technique Windows Defender Application Guard used), while
/// <see cref="FullDesktop"/> behaves like a normal Remote Desktop session.
/// </summary>
public enum SandboxConnectionMode
{
    /// <summary>Full guest desktop (Remote Desktop style).</summary>
    FullDesktop = 0,

    /// <summary>Seamless per-application windows (RemoteApp / RAIL style).</summary>
    RemoteApp = 1,
}

/// <summary>A single command executed inside the guest after logon.</summary>
public sealed record LogonCommand
{
    public string Command { get; }

    public LogonCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new SandboxValidationException("Logon command must not be empty.");
        }

        Command = command.Trim();
    }

    public override string ToString() => Command;
}
