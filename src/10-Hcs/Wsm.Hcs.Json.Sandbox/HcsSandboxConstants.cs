namespace Wsm.Hcs.Json.Sandbox;

/// <summary>
/// Well-known HCS identifiers used by the Windows Sandbox (VM) configuration.
/// </summary>
public static class HcsSandboxConstants
{
    /// <summary>Host layer identifier used by the container filesystem namespace.</summary>
    public const string HostLayerId = "8264f677-40b0-4ca5-bf9a-944ac2da8087";

    // From WindowsSandboxServer.Constants.
    public static readonly Guid LsmServiceId = new("F58797F6-C9F3-4D63-9BD4-E52AC020E586");
    public static readonly Guid NlmServiceId = new("A715AC94-B745-4889-9A0F-772D85A3CFA4");

    /// <summary>SID of the "Virtual Machines" group (<c>S-1-5-83-0</c>).</summary>
    public const string VirtualMachinesSid = "S-1-5-83-0";

    /// <summary>Capability SID <c>S-1-15-3-3</c> used by the platform's UI agent.</summary>
    public const string UiCapabilitySid = "S-1-15-3-3";

    /// <summary>Full access for everyone (used as the guest's connect grant).</summary>
    public const string EveryoneFullAccess = "D:P(D;;FA;;;WD)";

    /// <summary>Full access for LOCAL SYSTEM.</summary>
    public const string SystemFullAccess = "D:P(A;;FA;;;SY)";

    /// <summary>
    /// The platform's <b>full</b> HvSocket service table, minus the LSM/NLM pair the
    /// builder always emits. These six services are part of the Windows Sandbox
    /// <c>VirtualMachine.Devices.HvSocket.HvSocketConfig.ServiceTable</c>; the guest's
    /// interactive session (shell) infrastructure binds/connects them, and a
    /// configuration that declares only LSM/NLM cannot complete a full-desktop logon
    /// (the session is arbitrated and then dropped without ever starting the shell).
    /// </summary>
    /// <param name="interactiveUserSid">
    /// The connecting user's SID (or <c>null</c>); it is added to the UI agent service's
    /// bind grant so an unelevated viewer can reach it.
    /// </param>
    public static IReadOnlyList<HcsHvSocketService> PlatformAdditionalHvSocketServices(string? interactiveUserSid)
    {
        var uiAgentBind = $"{SystemFullAccess}(A;;FA;;;BA)(A;;FA;;;{UiCapabilitySid})";
        if (!string.IsNullOrWhiteSpace(interactiveUserSid))
        {
            uiAgentBind += $"(A;;FA;;;{interactiveUserSid})";
        }

        return
        [
            new HcsHvSocketService
            {
                Id = new Guid("BEFCBC10-1381-45AB-946E-B1A12D6BCE94"),
                BindSecurityDescriptor = EveryoneFullAccess,
                ConnectSecurityDescriptor = EveryoneFullAccess,
                AllowWildcardBinds = true,
            },
            new HcsHvSocketService
            {
                Id = new Guid("7D2E0620-034A-4438-B0FD-AE27FC0172A1"),
                BindSecurityDescriptor = $"{SystemFullAccess}(A;;FA;;;{VirtualMachinesSid})",
                ConnectSecurityDescriptor = EveryoneFullAccess,
            },
            new HcsHvSocketService
            {
                Id = new Guid("7B3014C3-284A-40D4-A97F-9D23A75C6A80"),
                BindSecurityDescriptor = EveryoneFullAccess,
                ConnectSecurityDescriptor = EveryoneFullAccess,
                AllowWildcardBinds = true,
            },
            new HcsHvSocketService
            {
                Id = new Guid("E97910D9-55BB-455E-9170-114FDFCE763D"),
                BindSecurityDescriptor = EveryoneFullAccess,
                ConnectSecurityDescriptor = EveryoneFullAccess,
                AllowWildcardBinds = true,
            },
            new HcsHvSocketService
            {
                Id = new Guid("E5AFD2E3-9B98-4913-B37C-09DE98772940"),
                BindSecurityDescriptor = EveryoneFullAccess,
                ConnectSecurityDescriptor = EveryoneFullAccess,
                AllowWildcardBinds = true,
            },
            new HcsHvSocketService
            {
                Id = new Guid("ABD802E8-FFCC-40D2-A5F1-F04B1D12CBC8"),
                BindSecurityDescriptor = uiAgentBind,
                ConnectSecurityDescriptor = EveryoneFullAccess,
            },
        ];
    }
}
