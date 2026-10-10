namespace Wsm.Hcs.Json.Sandbox;

/// <summary>
/// Builds the JSON configuration document passed to
/// <c>HcsCreateComputeSystem</c> for a Windows Sandbox (DisposableVM) container.
/// </summary>
public interface ISandboxVmJsonBuilder
{
    string Build(HcsSandboxDefinition definition);
}
