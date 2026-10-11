namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// Configuration for the Windows container engine.
/// </summary>
public sealed class WindowsContainerEngineOptions
{
    /// <summary>
    /// Services started inside a container once it is running. The container is
    /// built from the shared Windows Sandbox minimal image, which is stripped
    /// down, so these core services have to be brought up explicitly. The
    /// defaults are the minimum set the image needs for a usable session.
    /// </summary>
    public List<string> RequiredServices { get; set; } =
    [
        "RpcSs",
        "DcomLaunch",
        "ProfSvc",
        "CryptSvc",
    ];
}
