namespace Wsm.Hcs.Layout;

/// <summary>A registered container layer (from the CmService registry hive).</summary>
public sealed record HcsLayerInfo
{
    public required Guid Id { get; init; }

    public required string Path { get; init; }

    public string? OsVersion { get; init; }

    public int ParentType { get; init; }

    public Guid ParentId { get; init; }

    public int SequenceNumber { get; init; }

    public int State { get; init; }

    /// <summary>The layer's OS filesystem root (<c>&lt;Path&gt;\Files</c>).</summary>
    public string FilesPath => System.IO.Path.Combine(Path, "Files");

    public bool IsUsableBaseImage => Directory.Exists(System.IO.Path.Combine(FilesPath, "Windows"));
}
