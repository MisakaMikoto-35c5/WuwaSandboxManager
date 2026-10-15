using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The <b>mirror file</b> layout: the one contract shared by the in-container read
/// side (<c>wsm-desktop-read.ps1</c>, running inside the container's session) and
/// every host-side reader (the provider and the viewer). Keeping it in one place is
/// what lets the write side and the read side be developed independently.
/// <para>
/// Layout (little endian), all offsets from the start of the file:
/// <code>
/// 0   magic 'WSMM' (0x4D4D5357)   4  version (1)
/// 8   width                       12 height
/// 16  stride                      20 fps
/// 24  sequence (int64)            32 busy (int32, 1 while the read side writes)
/// 36  writerPid (int32)           40 windowCount (int32)
/// 48  totalFrames (int64)
/// 64  pixels: height * stride bytes, top-down BGRX (the whole desktop frame)
/// pixels + 0     window table, WindowTableBytes, WindowEntrySize per entry:
///                id(8) x(4) y(4) w(4) h(4) sequence(8) titleOffset(4) titleLength(4)
/// pixels + 8192  window titles, TitleBlobBytes, UTF-16, 512 bytes per window
/// </code>
/// </para>
/// </summary>
public static class ContainerDesktopMirror
{
    /// <summary>Magic of the mirror file: <c>'WSMM'</c>.</summary>
    public const int Magic = 0x4D4D5357;

    /// <summary>Offset of the pixel buffer.</summary>
    public const int PixelOffset = 64;

    /// <summary>Bytes per window table entry.</summary>
    public const int WindowEntrySize = 40;

    /// <summary>Bytes reserved for the window table.</summary>
    public const int WindowTableBytes = 8192;

    /// <summary>Bytes reserved for window titles.</summary>
    public const int TitleBlobBytes = 16384;

    /// <summary>Bytes per window title slot.</summary>
    public const int TitleSlotBytes = 512;

    /// <summary>Maximum number of windows the table can hold.</summary>
    public const int MaxWindows = WindowTableBytes / WindowEntrySize;

    /// <summary>Reads the header (and the window table when asked for).</summary>
    /// <param name="mirrorPath">Host path of the mirror file.</param>
    /// <param name="windowCount">How many windows the read side last published.</param>
    /// <param name="sequence">The frame sequence the header carries.</param>
    /// <returns>The geometry, or <c>null</c> when the file is absent or still being written.</returns>
    public static ContainerDesktopGeometry? TryReadHeader(
        string mirrorPath, out int windowCount, out long sequence)
    {
        windowCount = 0;
        sequence = 0;

        try
        {
            if (!File.Exists(mirrorPath))
            {
                return null;
            }

            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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

            windowCount = Math.Clamp(BitConverter.ToInt32(header.Slice(40, 4)), 0, MaxWindows);
            sequence = BitConverter.ToInt64(header.Slice(24, 8));

            return new ContainerDesktopGeometry(
                width,
                height,
                BitConverter.ToInt32(header.Slice(16, 4)),
                BitConverter.ToInt32(header.Slice(20, 4)));
        }
        catch (IOException)
        {
            return null; // the read side is mid-frame; the caller retries
        }
    }

    /// <summary>Reads the published window table.</summary>
    public static IReadOnlyList<ContainerDesktopWindow> ReadWindows(
        string mirrorPath, ContainerDesktopGeometry geometry, int windowCount)
    {
        var windows = new List<ContainerDesktopWindow>(windowCount);
        if (windowCount <= 0)
        {
            return windows;
        }

        try
        {
            using var stream = new FileStream(mirrorPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var tableStart = (long)PixelOffset + (long)geometry.Stride * geometry.Height;
            var titleBase = tableStart + WindowTableBytes;

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

    /// <summary>Total size of a mirror file for a desktop of the given geometry.</summary>
    public static int Capacity(ContainerDesktopGeometry geometry)
        => PixelOffset + (geometry.Stride * geometry.Height) + WindowTableBytes + TitleBlobBytes;
}
