namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The <b>mirror file</b> layout: the one contract shared by the in-container read
/// side (<c>wsm-desktop-read.ps1</c>, running inside the container's session) and
/// every host-side reader (the provider and the viewer). Keeping it in one place is
/// what lets the write side and the read side be developed independently.
/// <para>
/// Layout (little endian), all offsets from the start of the mapping:
/// <code>
/// 0   magic 'WSMM' (0x4D4D5357)   4  version (2; version 1 files lack the dirty region)
/// 8   width                       12 height
/// 16  stride                      20 fps
/// 24  sequence (int64)            32 busy (int32, 1 while the read side writes)
/// 36  writerPid (int32)           40 windowCount (int32)
/// 48  totalFrames (int64)         56 dirtyCount (int32, version 2)
/// 60  mode (int32, ContainerDesktopMode)
/// 64  pixels: height * stride bytes, top-down BGRX (the whole desktop frame)
/// pixels + 0     window table, WindowTableBytes, WindowEntrySize per entry:
///                id(8) x(4) y(4) w(4) h(4) sequence(8) titleOffset(4) titleLength(4)
/// pixels + 8192  dirty rects (version 2), DirtyRegionBytes, DirtyEntrySize per entry:
///                x(4) y(4) w(4) h(4)
/// pixels + 8192 + 3264  window titles, TitleBlobBytes, UTF-16, 512 bytes per window
/// </code>
/// In a version 1 file the title blob sits directly behind the window table; every
/// offset helper therefore takes the version.
/// </para>
/// <para>
/// In <see cref="ContainerDesktopMode.Pagefile"/> mode the file itself is only
/// header-sized (<see cref="ContainerDesktopFrame.HeaderFileCapacity"/>) and carries
/// the header above; the rest of the layout lives in a page-file-backed section of
/// <c>writerPid</c> that the host maps through handle passthrough (same layout, magic
/// first). Readers therefore take either a path or a mapped view.
/// </para>
/// </summary>
public static class ContainerDesktopMirror
{
    /// <summary>Magic of the mirror file: <c>'WSMM'</c>.</summary>
    public const int Magic = 0x4D4D5357;

    /// <summary>Protocol version without a dirty region (title blob directly behind the table).</summary>
    public const int LegacyVersion = 1;

    /// <summary>Current protocol version the read side writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Offset of the pixel buffer.</summary>
    public const int PixelOffset = 64;

    /// <summary>Header offset of the dirty-rect count (version 2).</summary>
    public const int DirtyCountOffset = 56;

    /// <summary>Header offset of the writer's process id (the passthrough source).</summary>
    public const int WriterPidOffset = 36;

    /// <summary>Header offset of the <see cref="ContainerDesktopMode"/> field.</summary>
    public const int ModeOffset = 60;

    /// <summary>Bytes per window table entry.</summary>
    public const int WindowEntrySize = 40;

    /// <summary>Bytes reserved for the window table.</summary>
    public const int WindowTableBytes = 8192;

    /// <summary>Bytes per dirty rect entry: x(4) y(4) w(4) h(4).</summary>
    public const int DirtyEntrySize = 16;

    /// <summary>Maximum number of dirty rects a frame can carry.</summary>
    public const int MaxDirtyRects = MaxWindows;

    /// <summary>Bytes reserved for the dirty rect array (version 2).</summary>
    public const int DirtyRegionBytes = MaxDirtyRects * DirtyEntrySize;

    /// <summary>Bytes reserved for window titles.</summary>
    public const int TitleBlobBytes = 16384;

    /// <summary>Bytes per window title slot.</summary>
    public const int TitleSlotBytes = 512;

    /// <summary>Maximum number of windows the table can hold.</summary>
    public const int MaxWindows = WindowTableBytes / WindowEntrySize;

    /// <summary>Offset of the window table for a desktop of the given geometry.</summary>
    public static long TableOffset(ContainerDesktopGeometry geometry)
        => PixelOffset + (long)geometry.Stride * geometry.Height;

    /// <summary>Offset of the dirty rect array (version 2 files).</summary>
    public static long DirtyOffset(ContainerDesktopGeometry geometry)
        => TableOffset(geometry) + WindowTableBytes;

    /// <summary>Offset of the title blob; version 1 files have no dirty region before it.</summary>
    public static long TitleOffset(ContainerDesktopGeometry geometry, int version)
        => DirtyOffset(geometry) + (version >= CurrentVersion ? DirtyRegionBytes : 0);

    /// <summary>Total size of a mirror file for a desktop of the given geometry (current version).</summary>
    public static int Capacity(ContainerDesktopGeometry geometry)
        => Capacity(geometry, CurrentVersion);

    /// <summary>Total size of a mirror file of the given protocol version.</summary>
    public static int Capacity(ContainerDesktopGeometry geometry, int version)
        => (int)TitleOffset(geometry, version) + TitleBlobBytes;

    /// <summary>Reads the header (and the window table when asked for).</summary>
    /// <param name="mirrorPath">Host path of the mirror file.</param>
    /// <param name="windowCount">How many windows the read side last published.</param>
    /// <param name="sequence">The frame sequence the header carries.</param>
    /// <returns>The geometry, or <c>null</c> when the file is absent or still being written.</returns>
    public static ContainerDesktopGeometry? TryReadHeader(
        string mirrorPath, out int windowCount, out long sequence)
    {
        if (TryReadHeaderInfo(mirrorPath) is { } header)
        {
            windowCount = header.WindowCount;
            sequence = header.Sequence;
            return header.Geometry;
        }

        windowCount = 0;
        sequence = 0;
        return null;
    }

    /// <summary>
    /// Reads the full header, including the protocol version and the dirty-rect count,
    /// or <c>null</c> when the file is absent or still being written.
    /// </summary>
    public static ContainerDesktopHeader? TryReadHeaderInfo(string mirrorPath)
    {
        try
        {
            if (!File.Exists(mirrorPath))
            {
                return null;
            }

            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return TryReadHeaderInfo(stream);
        }
        catch (IOException)
        {
            return null; // the read side is mid-frame; the caller retries
        }
    }

    /// <summary>
    /// Reads the full header from a mapped view (a passed-through section in pagefile
    /// mode), or <c>null</c> when it is too short or carries no magic.
    /// </summary>
    public static ContainerDesktopHeader? TryReadHeaderInfo(IntPtr baseAddress, long length)
    {
        if (length < PixelOffset)
        {
            return null;
        }

        using var stream = new Internal.MappingReadStream(baseAddress, length);
        return TryReadHeaderInfo(stream);
    }

    private static ContainerDesktopHeader? TryReadHeaderInfo(Stream stream)
    {
        try
        {
            Span<byte> header = stackalloc byte[PixelOffset];
            if (stream.Read(header) < PixelOffset)
            {
                return null;
            }

            if (BitConverter.ToInt32(header[..4]) != Magic)
            {
                return null;
            }

            var width = BitConverter.ToInt32(header.Slice(8, 4));
            var height = BitConverter.ToInt32(header.Slice(12, 4));
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var version = BitConverter.ToInt32(header.Slice(4, 4));
            var geometry = new ContainerDesktopGeometry(
                width,
                height,
                BitConverter.ToInt32(header.Slice(16, 4)),
                BitConverter.ToInt32(header.Slice(20, 4)));

            var dirtyCount = version >= CurrentVersion
                ? Math.Clamp(BitConverter.ToInt32(header.Slice(DirtyCountOffset, 4)), 0, MaxDirtyRects)
                : 0;

            return new ContainerDesktopHeader(
                geometry,
                version,
                Math.Clamp(BitConverter.ToInt32(header.Slice(40, 4)), 0, MaxWindows),
                BitConverter.ToInt64(header.Slice(24, 8)),
                dirtyCount,
                BitConverter.ToInt32(header.Slice(WriterPidOffset, 4)),
                (ContainerDesktopMode)BitConverter.ToInt32(header.Slice(ModeOffset, 4)));
        }
        catch (IOException)
        {
            return null; // the read side is mid-frame; the caller retries
        }
    }

    /// <summary>Reads the published window table of a version 1 (legacy) mirror file.</summary>
    public static IReadOnlyList<ContainerDesktopWindow> ReadWindows(
        string mirrorPath, ContainerDesktopGeometry geometry, int windowCount)
        => ReadWindows(mirrorPath, geometry, windowCount, LegacyVersion);

    /// <summary>Reads the published window table.</summary>
    public static IReadOnlyList<ContainerDesktopWindow> ReadWindows(
        string mirrorPath, ContainerDesktopGeometry geometry, int windowCount, int version)
    {
        try
        {
            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadWindows(stream, geometry, windowCount, version);
        }
        catch (IOException)
        {
            return Array.Empty<ContainerDesktopWindow>();
        }
    }

    /// <summary>Reads the published window table from a mapped view.</summary>
    public static IReadOnlyList<ContainerDesktopWindow> ReadWindows(
        IntPtr baseAddress, long length, ContainerDesktopGeometry geometry, int windowCount, int version)
    {
        using var stream = new Internal.MappingReadStream(baseAddress, length);
        return ReadWindows(stream, geometry, windowCount, version);
    }

    private static IReadOnlyList<ContainerDesktopWindow> ReadWindows(
        Stream stream, ContainerDesktopGeometry geometry, int windowCount, int version)
    {
        var windows = new List<ContainerDesktopWindow>(windowCount);
        if (windowCount <= 0)
        {
            return windows;
        }

        try
        {
            var tableStart = TableOffset(geometry);
            var titleBase = TitleOffset(geometry, version);

            var entry = new byte[WindowEntrySize];
            for (var i = 0; i < windowCount; i++)
            {
                stream.Position = tableStart + (long)i * WindowEntrySize;
                if (stream.Read(entry) < WindowEntrySize)
                {
                    break;
                }

                var title = string.Empty;
                var titleOffset = BitConverter.ToInt32(entry.AsSpan(32, 4));
                var titleLength = BitConverter.ToInt32(entry.AsSpan(36, 4));
                if (titleLength is > 0 and <= TitleSlotBytes / 2)
                {
                    var buffer = new byte[titleLength * 2];
                    stream.Position = titleBase + titleOffset;
                    if (stream.Read(buffer) == buffer.Length)
                    {
                        title = System.Text.Encoding.Unicode.GetString(buffer);
                    }
                }

                windows.Add(new ContainerDesktopWindow(
                    BitConverter.ToInt64(entry.AsSpan(0, 8)),
                    title,
                    BitConverter.ToInt32(entry.AsSpan(8, 4)),
                    BitConverter.ToInt32(entry.AsSpan(12, 4)),
                    BitConverter.ToInt32(entry.AsSpan(16, 4)),
                    BitConverter.ToInt32(entry.AsSpan(20, 4)),
                    BitConverter.ToInt64(entry.AsSpan(24, 8))));
            }
        }
        catch (IOException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return windows;
    }

    /// <summary>Reads the dirty rects of the last frame (version 2 files).</summary>
    public static IReadOnlyList<ContainerDesktopRect> ReadDirtyRects(
        string mirrorPath, ContainerDesktopGeometry geometry, int dirtyCount)
    {
        try
        {
            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ReadDirtyRects(stream, geometry, dirtyCount);
        }
        catch (IOException)
        {
            return Array.Empty<ContainerDesktopRect>();
        }
    }

    /// <summary>Reads the dirty rects of the last frame from a mapped view.</summary>
    public static IReadOnlyList<ContainerDesktopRect> ReadDirtyRects(
        IntPtr baseAddress, long length, ContainerDesktopGeometry geometry, int dirtyCount)
    {
        using var stream = new Internal.MappingReadStream(baseAddress, length);
        return ReadDirtyRects(stream, geometry, dirtyCount);
    }

    private static IReadOnlyList<ContainerDesktopRect> ReadDirtyRects(
        Stream stream, ContainerDesktopGeometry geometry, int dirtyCount)
    {
        var rects = new List<ContainerDesktopRect>(dirtyCount);
        if (dirtyCount <= 0)
        {
            return rects;
        }

        try
        {
            var dirtyStart = DirtyOffset(geometry);

            var entry = new byte[DirtyEntrySize];
            for (var i = 0; i < Math.Min(dirtyCount, MaxDirtyRects); i++)
            {
                stream.Position = dirtyStart + (long)i * DirtyEntrySize;
                if (stream.Read(entry) < DirtyEntrySize)
                {
                    break;
                }

                var width = BitConverter.ToInt32(entry.AsSpan(8, 4));
                var height = BitConverter.ToInt32(entry.AsSpan(12, 4));
                if (width <= 0 || height <= 0)
                {
                    continue;
                }

                rects.Add(new ContainerDesktopRect(
                    BitConverter.ToInt32(entry.AsSpan(0, 4)),
                    BitConverter.ToInt32(entry.AsSpan(4, 4)),
                    width,
                    height));
            }
        }
        catch (IOException)
        {
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        return rects;
    }
}

/// <summary>The mirror file header: geometry plus protocol version and dirty-rect count.</summary>
/// <param name="Geometry">Frame geometry.</param>
/// <param name="Version">Protocol version the read side writes (<see cref="ContainerDesktopMirror.CurrentVersion"/>).</param>
/// <param name="WindowCount">How many windows the read side last published.</param>
/// <param name="Sequence">The frame sequence the header carries.</param>
/// <param name="DirtyCount">How many dirty rects the last frame carries (version 2).</param>
/// <param name="WriterPid">Process id of the read side (the passthrough source).</param>
/// <param name="Mode">Where the pixels live (<see cref="ContainerDesktopMode"/>).</param>
public readonly record struct ContainerDesktopHeader(
    ContainerDesktopGeometry Geometry,
    int Version,
    int WindowCount,
    long Sequence,
    int DirtyCount,
    int WriterPid,
    ContainerDesktopMode Mode);

/// <summary>A changed rectangle of the desktop frame, in desktop coordinates.</summary>
public readonly record struct ContainerDesktopRect(int X, int Y, int Width, int Height);
