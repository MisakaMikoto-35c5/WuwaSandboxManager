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
}
