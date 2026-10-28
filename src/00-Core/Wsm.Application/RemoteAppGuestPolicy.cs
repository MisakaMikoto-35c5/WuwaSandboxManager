namespace Wsm.Application;

/// <summary>
/// The guest-side RemoteApp enablement that must be applied before a RAIL
/// connect, mirroring what <c>remoteapptool</c> configures on an RDS host before
/// publishing applications:
/// <list type="bullet">
/// <item><c>TSAppAllowList\fDisabledAllowList=1</c> — the guest does not restrict
/// RemoteApp programs to a published allow list;</item>
/// <item><c>Terminal Services\fAllowUnlistedRemotePrograms=1</c> — the policy
/// that admits applications that were never explicitly published.</item>
/// </list>
/// Without them a client RAIL request for an arbitrary program path stalls before
/// the application launches. Both values are idempotent.
/// <para>
/// The commands are applied through whatever guest-command channel is at hand:
/// the engine directly (in-process paths, via
/// <see cref="Internal.SandboxManager"/>), or the agent service
/// (<c>wsm connect --service</c>, which has no engine in the client process).
/// </para>
/// </summary>
public static class RemoteAppGuestPolicy
{
    /// <summary>The guest commands that open the RemoteApp surface (idempotent).</summary>
    public static IReadOnlyList<string> EnablementCommands { get; } = new[]
    {
        "reg add \"HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Terminal Server\\TSAppAllowList\" " +
        "/v fDisabledAllowList /t REG_DWORD /d 1 /f",
        "reg add \"HKLM\\SOFTWARE\\Policies\\Microsoft\\Windows NT\\Terminal Services\" " +
        "/v fAllowUnlistedRemotePrograms /t REG_DWORD /d 1 /f",
    };
}
