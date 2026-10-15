namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// Configuration for the Windows container engine.
/// </summary>
public sealed class WindowsContainerEngineOptions
{
    /// <summary>
    /// Environment variable that overrides <see cref="BaseImageLayerPath"/> at
    /// runtime (the GUI/CLI set it once an image is imported, so a changed image
    /// takes effect without a restart).
    /// </summary>
    public const string ImageOverrideVariable = "WSM_CONTAINER_BASE_IMAGE";

    /// <summary>
    /// The base OS <b>image layer</b> the container is built from: the root
    /// directory of a processed WCOW layer (the folder holding <c>Files</c> and
    /// <c>Hives</c>). Process isolation requires the image build to match the
    /// host build, except for a Windows Server 2022/2025 image, which a Windows
    /// 11 host may run despite the mismatch. The Windows Sandbox image is a
    /// <b>client</b> image and is therefore only usable when its build matches
    /// the host's; set this to an imported server image when it does not.
    /// <para>
    /// <c>null</c> falls back to the newest registered Windows Sandbox image
    /// layer (which only works on a host whose build matches it).
    /// </para>
    /// </summary>
    public string? BaseImageLayerPath { get; set; }

    /// <summary>
    /// Services started inside a container once it is running. The image is
    /// stripped down, so a handful of core services have to be brought up
    /// explicitly for a usable session.
    /// </summary>
    public List<string> RequiredServices { get; set; } =
    [
        "RpcSs",
        "DcomLaunch",
        "ProfSvc",
        "CryptSvc",
    ];

    /// <summary>
    /// Opt-in desktop session inside the container (shell, auto-logon) plus the
    /// experimental RDP attempt. Requires an image prepared for a console session
    /// (<c>wsm image desktop</c>); RDP itself stays unavailable in a container. See
    /// <see cref="ContainerDesktopOptions"/>.
    /// </summary>
    public ContainerDesktopOptions Desktop { get; set; } = new();
}
