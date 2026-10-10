using System.Text;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Reads the VHDX <b>Parent Locator</b> metadata entry to discover the path of a
/// differencing disk's parent. Pure managed parsing of the documented VHDX
/// layout, so it works without elevation, the Hyper-V module, or the VirtDisk
/// API — and can be unit tested against synthetic files.
/// <para>
/// Layout used here: the file identifier (<c>vhdxfile</c>) at 0, the region
/// table (<c>regi</c>) at 0x30000 whose entries locate the metadata region
/// (<c>metadata</c>), and the metadata region's parent-locator item
/// (<c>{B04AEFB7-D19E-4A81-B789-25B8E9445913}</c>) which holds
/// <c>key = value</c> UTF-16 locator strings.
/// </para>
/// </summary>
internal static class VhdxParentLocator
{
    private const long RegionTableOffset = 0x30000; // 192 KiB
    private const int RegionTableEntrySize = 32;
    private const int MetadataHeaderSize = 32;
    private const int MetadataEntrySize = 32;

    /// <summary>Maximum number of region/metadata entries we will iterate.</summary>
    private const int MaxEntries = 4096;

    private static readonly Guid MetadataRegionId = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");

    /// <summary>Metadata item id of the <b>Parent Locator</b> entry.</summary>
    private static readonly Guid ParentLocatorItemId = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");

    /// <summary>Locator-type GUID stored at the start of the parent locator data.</summary>
    private static readonly Guid ParentLocatorTypeId = new("B04AEFB7-D19E-4A81-B789-25B8E9445913");

    private static readonly byte[] FileIdentifier = Encoding.ASCII.GetBytes("vhdxfile");

    /// <summary>
    /// Returns the parent's absolute path recorded in <paramref name="vhdxPath"/>,
    /// or <see langword="null"/> when the disk is a base (no parent locator) or
    /// the file cannot be parsed.
    /// </summary>
    public static string? TryGetAbsoluteParentPath(string vhdxPath)
    {
        try
        {
            using var stream = new FileStream(
                vhdxPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096);

            if (!HasFileIdentifier(stream))
            {
                return null;
            }

            var metadata = FindMetadataRegion(stream);
            if (metadata is null)
            {
                return null;
            }

            var locator = ReadParentLocator(stream, metadata.Value.Offset, metadata.Value.Length);
            if (locator is null || locator.Count == 0)
            {
                return null;
            }

            if (locator.TryGetValue("absolute_win32_path", out var absolute) &&
                !string.IsNullOrWhiteSpace(absolute))
            {
                return absolute;
            }

            if (locator.TryGetValue("relative_path", out var relative) &&
                !string.IsNullOrWhiteSpace(relative))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(vhdxPath));
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    return Path.GetFullPath(Path.Combine(directory, relative));
                }
            }

            if (locator.TryGetValue("volume_path", out var volume) &&
                !string.IsNullOrWhiteSpace(volume))
            {
                return volume;
            }

            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool HasFileIdentifier(FileStream stream)
    {
        if (stream.Length < FileIdentifier.Length)
        {
            return false;
        }

        var buffer = new byte[FileIdentifier.Length];
        stream.Position = 0;
        return stream.Read(buffer, 0, buffer.Length) == buffer.Length &&
               buffer.AsSpan().SequenceEqual(FileIdentifier);
    }

    private static (long Offset, uint Length)? FindMetadataRegion(FileStream stream)
    {
        if (stream.Length < RegionTableOffset + 16)
        {
            return null;
        }

        var header = new byte[16];
        stream.Position = RegionTableOffset;
        if (stream.Read(header, 0, header.Length) != header.Length)
        {
            return null;
        }

        if (header[0] != (byte)'r' || header[1] != (byte)'e' ||
            header[2] != (byte)'g' || header[3] != (byte)'i')
        {
            return null;
        }

        var count = BitConverter.ToInt32(header, 8);
        if (count <= 0 || count > MaxEntries)
        {
            return null;
        }

        var entries = new byte[count * RegionTableEntrySize];
        if (stream.Read(entries, 0, entries.Length) != entries.Length)
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            var baseOffset = i * RegionTableEntrySize;
            var id = new Guid(entries.AsSpan(baseOffset, 16));
            if (id != MetadataRegionId)
            {
                continue;
            }

            var fileOffset = BitConverter.ToInt64(entries, baseOffset + 16);
            var length = BitConverter.ToUInt32(entries, baseOffset + 24);
            if (fileOffset <= 0 || length == 0)
            {
                return null;
            }

            return (fileOffset, length);
        }

        return null;
    }

    private static Dictionary<string, string>? ReadParentLocator(
        FileStream stream, long metadataOffset, uint metadataLength)
    {
        if (metadataOffset < 0 || metadataOffset + MetadataHeaderSize > stream.Length)
        {
            return null;
        }

        var header = new byte[MetadataHeaderSize];
        stream.Position = metadataOffset;
        if (stream.Read(header, 0, header.Length) != header.Length)
        {
            return null;
        }

        if (header[0] != (byte)'m' || header[1] != (byte)'e' ||
            header[2] != (byte)'t' || header[3] != (byte)'a')
        {
            return null;
        }

        var count = BitConverter.ToUInt16(header, 10);
        if (count == 0 || count > MaxEntries)
        {
            return null;
        }

        var entries = new byte[count * MetadataEntrySize];
        if (stream.Read(entries, 0, entries.Length) != entries.Length)
        {
            return null;
        }

        for (var i = 0; i < count; i++)
        {
            var baseOffset = i * MetadataEntrySize;
            var itemId = new Guid(entries.AsSpan(baseOffset, 16));
            if (itemId != ParentLocatorItemId)
            {
                continue;
            }

            var itemOffset = BitConverter.ToUInt32(entries, baseOffset + 16);
            var itemLength = BitConverter.ToUInt32(entries, baseOffset + 20);
            if (itemLength < 20 || itemOffset + itemLength > metadataLength)
            {
                return null;
            }

            var dataOffset = metadataOffset + itemOffset;
            if (dataOffset + itemLength > stream.Length)
            {
                return null;
            }

            var data = new byte[itemLength];
            stream.Position = dataOffset;
            if (stream.Read(data, 0, data.Length) != data.Length)
            {
                return null;
            }

            return ParseKeyValues(data);
        }

        return null;
    }

    private static Dictionary<string, string> ParseKeyValues(byte[] data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (data.Length < 20 || new Guid(data.AsSpan(0, 16)) != ParentLocatorTypeId)
        {
            return result;
        }

        // Parent-locator header: LocatorType GUID (16) + reserved (2) + KeyValueCount (2).
        var count = BitConverter.ToUInt16(data, 18);
        if (count > MaxEntries)
        {
            return result;
        }

        for (var i = 0; i < count; i++)
        {
            var entryOffset = 20 + (i * 12);
            if (entryOffset + 12 > data.Length)
            {
                break;
            }

            var keyOffset = BitConverter.ToUInt32(data, entryOffset);
            var valueOffset = BitConverter.ToUInt32(data, entryOffset + 4);
            var keyLength = BitConverter.ToUInt16(data, entryOffset + 8);
            var valueLength = BitConverter.ToUInt16(data, entryOffset + 10);

            var key = ReadUtf16(data, keyOffset, keyLength);
            var value = ReadUtf16(data, valueOffset, valueLength);
            if (key is not null && value is not null)
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string? ReadUtf16(byte[] data, uint offset, int lengthBytes)
    {
        if (lengthBytes <= 0 || offset > data.Length || offset + lengthBytes > data.Length)
        {
            return null;
        }

        var value = Encoding.Unicode.GetString(data, (int)offset, lengthBytes).TrimEnd('\0');
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
