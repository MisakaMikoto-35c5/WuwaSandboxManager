namespace Wsm.Hcs.Json.Container;

/// <summary>
/// The minimal inputs needed to describe a Windows container compute system.
/// <b>Reserved / not implemented yet.</b>
/// </summary>
public sealed record ContainerDefinition(
    string Id,
    string StoragePath,
    string BaseLayerPath,
    int MemoryMb,
    int CpuCount);
