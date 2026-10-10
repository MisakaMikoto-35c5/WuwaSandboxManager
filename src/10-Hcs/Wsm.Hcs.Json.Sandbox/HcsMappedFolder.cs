namespace Wsm.Hcs.Json.Sandbox;

/// <summary>A host folder exposed to the guest through a vSMB share.</summary>
public sealed record HcsMappedFolder(string HostPath, string SandboxPath, bool ReadOnly);
