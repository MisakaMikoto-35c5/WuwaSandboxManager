namespace Wsm.Hcs.Storage;

/// <summary>Resolved storage paths for a sandbox.</summary>
public sealed record StorageLayout(string Directory, string DiskPath, string GuestStatePath);
