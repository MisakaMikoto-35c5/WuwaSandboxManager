namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// The <b>frame file</b> layout: the contract shared by the in-container agent
/// (<c>container-desktop-agent.ps1</c>) and every host-side reader (the frame-buffer
/// provider and the <c>desktop-view</c> viewer).
/// <para>
/// Layout (little endian), all offsets from the start of the mapping:
/// <code>
/// 0   magic 'FSM1' (0x314D5346)   4  version (1)
/// 8   width                       12 height
/// 16  stride                      20 fps
/// 24  seq (int64, ++ per frame)   32 busy (int32, 1 while writing)
/// 36  writerPid (int32)           40 reserved
/// 48  totalFrames (int64)         56 reserved
/// 60  mode (int32, <see cref="ContainerDesktopMode"/>)
/// 64.. pixels (height * stride bytes, top-down BGRX)
/// </code>
/// In <see cref="ContainerDesktopMode.Pagefile"/> mode the file itself is only
/// <see cref="HeaderFileCapacity"/> bytes and carries this header; the pixels live in
/// a page-file-backed section of <c>writerPid</c> that the host maps through handle
/// passthrough. The section carries the same header, magic first, so it can be told
/// apart from any other section of matching size.
/// </para>
/// </summary>
public static class ContainerDesktopFrame
{
    /// <summary>Magic of the frame file: <c>'FSM1'</c>.</summary>
    public const int Magic = 0x314D5346;

    /// <summary>Protocol version the agent writes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Offset of the pixel buffer.</summary>
    public const int PixelOffset = 64;

    /// <summary>Header offset of the writer's process id.</summary>
    public const int WriterPidOffset = 36;

    /// <summary>Header offset of the <see cref="ContainerDesktopMode"/> field.</summary>
    public const int ModeOffset = 60;

    /// <summary>
    /// Size of the <c>.shm</c> file in pagefile mode: the header alone, so the
    /// "wait for header / adopt a running agent" logic works unchanged.
    /// </summary>
    public const int HeaderFileCapacity = 4096;

    /// <summary>Total size of a frame source for a desktop of the given geometry.</summary>
    public static long Capacity(ContainerDesktopGeometry geometry)
        => PixelOffset + (long)geometry.Stride * geometry.Height;

    /// <summary>
    /// True when the header a reader just re-polled describes a different source than the
    /// one it has mapped - a restarted agent (new writer pid), a changed backing mode or
    /// a changed geometry. The caller remaps; a mere fps change does not count.
    /// </summary>
    public static bool RequiresRemap(ContainerDesktopFrameHeader mapped, ContainerDesktopFrameHeader current)
        => mapped.WriterPid != current.WriterPid
            || mapped.Mode != current.Mode
            || mapped.Geometry.Width != current.Geometry.Width
            || mapped.Geometry.Height != current.Geometry.Height;

    /// <summary>
    /// Reads the frame header, or <c>null</c> when the file is absent or still being
    /// written. In pagefile mode the header-sized file is enough.
    /// </summary>
    public static ContainerDesktopFrameHeader? TryReadHeader(string framePath)
    {
        try
        {
            if (!File.Exists(framePath))
            {
                return null;
            }

            using var stream = new FileStream(framePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> header = stackalloc byte[PixelOffset];
            if (stream.Read(header) < header.Length)
            {
                return null;
            }

            var magic = BitConverter.ToInt32(header[..4]);
            var width = BitConverter.ToInt32(header.Slice(8, 4));
            var height = BitConverter.ToInt32(header.Slice(12, 4));
            if (magic != Magic || width <= 0 || height <= 0)
            {
                return null;
            }

            var geometry = new ContainerDesktopGeometry(
                width,
                height,
                BitConverter.ToInt32(header.Slice(16, 4)),
                BitConverter.ToInt32(header.Slice(20, 4)));

            return new ContainerDesktopFrameHeader(
                geometry,
                BitConverter.ToInt32(header.Slice(WriterPidOffset, 4)),
                (ContainerDesktopMode)BitConverter.ToInt32(header.Slice(ModeOffset, 4)))
            {
                Sequence = BitConverter.ToInt64(header.Slice(24, 8)),
            };
        }
        catch (IOException)
        {
            return null; // the agent is mid-write; the caller retries
        }
    }
}

/// <summary>The frame file header: geometry plus the writer's process id and mode.</summary>
/// <param name="Geometry">Frame geometry.</param>
/// <param name="WriterPid">Process id of the in-container agent (the passthrough source).</param>
/// <param name="Mode">Where the pixels live (<see cref="ContainerDesktopMode"/>).</param>
public readonly record struct ContainerDesktopFrameHeader(
    ContainerDesktopGeometry Geometry,
    int WriterPid,
    ContainerDesktopMode Mode)
{
    /// <summary>Heartbeat sequence published by the writer.</summary>
    public long Sequence { get; init; }
}
