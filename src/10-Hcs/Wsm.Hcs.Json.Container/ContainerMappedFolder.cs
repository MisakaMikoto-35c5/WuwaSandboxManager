namespace Wsm.Hcs.Json.Container;

/// <summary>
/// A host folder mapped into a container at creation time. Maps to one entry of
/// the HCS <c>Container.MappedDirectories</c> array.
/// </summary>
/// <param name="HostPath">Absolute host folder that is shared.</param>
/// <param name="ContainerPath">Destination folder inside the container.</param>
/// <param name="ReadOnly">Whether the container may write to the mapping.</param>
public sealed record ContainerMappedFolder(
    string HostPath,
    string ContainerPath,
    bool ReadOnly = true);
