using System.Runtime.InteropServices;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Phase 3 of the container desktop channel: the pagefile mode (frames in a
/// page-file-backed section, reached through handle passthrough) and its
/// negotiation. The pure parts (mode file, header mode field, handle-table
/// parsing) are pinned; the passthrough itself is exercised against the test
/// process's own section, which needs no elevation and no container.
/// </summary>
public sealed class ContainerDesktopPassthroughTests
{
    private static readonly ContainerDesktopGeometry Geometry = new(64, 32, 64 * 4, 4);

    [Theory]
    [InlineData(null, ContainerDesktopMode.File)]
    [InlineData("", ContainerDesktopMode.File)]
    [InlineData("file", ContainerDesktopMode.File)]
    [InlineData("pagefile", ContainerDesktopMode.Pagefile)]
    [InlineData("  PAGEFILE \r\n", ContainerDesktopMode.Pagefile)]
    [InlineData("whatever", ContainerDesktopMode.File)]
    public void The_mode_file_defaults_to_file(string? content, ContainerDesktopMode expected)
        => Assert.Equal(expected, ShmModeNegotiation.Parse(content));

    [Fact]
    public void The_mode_file_round_trips()
    {
        Assert.Equal("pagefile", ShmModeNegotiation.ToFileContent(ContainerDesktopMode.Pagefile));
        Assert.Equal("file", ShmModeNegotiation.ToFileContent(ContainerDesktopMode.File));
    }

    [Fact]
    public void WriteModeFile_writes_the_negotiated_token()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wsm-mode-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(directory);
            ShmModeNegotiation.WriteModeFile(directory, ContainerDesktopMode.Pagefile);

            var path = Path.Combine(directory, ContainerDesktopPaths.ModeFileName);
            Assert.Equal("shm-mode.txt", ContainerDesktopPaths.ModeFileName);
            Assert.Equal("pagefile", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    // Pagefile only pays off when the process that MAPS the section (the viewer) can
    // do the passthrough: a SYSTEM service launches the viewer with the interactive
    // user's (non-elevated) token, so a SYSTEM host must stay in file mode.
    [InlineData(true, false, ContainerDesktopMode.Pagefile)]
    [InlineData(true, true, ContainerDesktopMode.File)]
    [InlineData(false, false, ContainerDesktopMode.File)]
    [InlineData(false, true, ContainerDesktopMode.File)]
    public void Pagefile_needs_an_elevated_host_and_a_same_token_viewer(
        bool hostElevated, bool viewerRunsAsInteractiveUser, ContainerDesktopMode expected)
        => Assert.Equal(expected, ShmModeNegotiation.Negotiate(hostElevated, viewerRunsAsInteractiveUser));

    [Fact]
    public void The_mode_field_offsets_match_both_layouts()
    {
        Assert.Equal(60, ContainerDesktopFrame.ModeOffset);
        Assert.Equal(60, ContainerDesktopMirror.ModeOffset);
        Assert.Equal(36, ContainerDesktopFrame.WriterPidOffset);
        Assert.Equal(36, ContainerDesktopMirror.WriterPidOffset);
        Assert.Equal(4096, ContainerDesktopFrame.HeaderFileCapacity);
    }

    [Fact]
    public void A_frame_header_round_trips_mode_and_writer_pid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-frame-{Guid.NewGuid():N}.shm");
        try
        {
            var buffer = new byte[ContainerDesktopFrame.HeaderFileCapacity];
            Write(buffer, 0, ContainerDesktopFrame.Magic);
            Write(buffer, 4, ContainerDesktopFrame.CurrentVersion);
            Write(buffer, 8, Geometry.Width);
            Write(buffer, 12, Geometry.Height);
            Write(buffer, 16, Geometry.Stride);
            Write(buffer, 20, Geometry.Fps);
            Write(buffer, ContainerDesktopFrame.WriterPidOffset, 4242);
            Write(buffer, ContainerDesktopFrame.ModeOffset, (int)ContainerDesktopMode.Pagefile);
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopFrame.TryReadHeader(path);

            Assert.NotNull(header);
            Assert.Equal(Geometry, header!.Value.Geometry);
            Assert.Equal(4242, header.Value.WriterPid);
            Assert.Equal(ContainerDesktopMode.Pagefile, header.Value.Mode);
            Assert.Equal(64 + (long)Geometry.Stride * Geometry.Height, ContainerDesktopFrame.Capacity(Geometry));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_file_mode_frame_header_reports_file_mode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-frame-{Guid.NewGuid():N}.shm");
        try
        {
            var buffer = new byte[(int)ContainerDesktopFrame.Capacity(Geometry)];
            Write(buffer, 0, ContainerDesktopFrame.Magic);
            Write(buffer, 8, Geometry.Width);
            Write(buffer, 12, Geometry.Height);
            Write(buffer, 16, Geometry.Stride);
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopFrame.TryReadHeader(path);

            Assert.NotNull(header);
            Assert.Equal(ContainerDesktopMode.File, header!.Value.Mode); // a zeroed slot means file
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_mirror_header_round_trips_mode_and_writer_pid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            var buffer = new byte[ContainerDesktopMirror.Capacity(Geometry)];
            Write(buffer, 0, ContainerDesktopMirror.Magic);
            Write(buffer, 4, ContainerDesktopMirror.CurrentVersion);
            Write(buffer, 8, Geometry.Width);
            Write(buffer, 12, Geometry.Height);
            Write(buffer, 16, Geometry.Stride);
            Write(buffer, ContainerDesktopMirror.WriterPidOffset, 777);
            Write(buffer, ContainerDesktopMirror.ModeOffset, (int)ContainerDesktopMode.Pagefile);
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopMirror.TryReadHeaderInfo(path);

            Assert.NotNull(header);
            Assert.Equal(ContainerDesktopMode.Pagefile, header!.Value.Mode);
            Assert.Equal(777, header.Value.WriterPid);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_handle_table_parser_reads_pid_type_and_handle_value()
    {
        // SYSTEM_HANDLE_INFORMATION_EX: 16-byte header (count + reserved), 40-byte entries.
        var buffer = new byte[16 + (2 * 40)];
        BitConverter.GetBytes(2).CopyTo(buffer, 0);
        BitConverter.GetBytes((long)209216).CopyTo(buffer, 16 + 8);   // UniqueProcessId (no 16-bit truncation)
        BitConverter.GetBytes((long)0x1234).CopyTo(buffer, 16 + 16);  // HandleValue
        BitConverter.GetBytes((ushort)49).CopyTo(buffer, 16 + 30);    // ObjectTypeIndex
        BitConverter.GetBytes((long)7).CopyTo(buffer, 16 + 40 + 8);
        BitConverter.GetBytes((long)0xABCD).CopyTo(buffer, 16 + 40 + 16);
        BitConverter.GetBytes((ushort)5).CopyTo(buffer, 16 + 40 + 30);

        var entries = SectionPassthrough.ParseHandleTable(buffer);

        Assert.Equal(2, entries.Count);
        Assert.Equal(new SectionPassthrough.HandleEntry(209216, 49, 0x1234), entries[0]);
        Assert.Equal(new SectionPassthrough.HandleEntry(7, 5, 0xABCD), entries[1]);
        Assert.Empty(SectionPassthrough.ParseHandleTable(Array.Empty<byte>()));
    }

    [Theory]
    // The kernel rounds a page-file-backed section up to whole pages (measured: an
    // 8256-byte section reports 12288), so the match is a one-page window.
    [InlineData(8256, 8256, true)]
    [InlineData(12288, 8256, true)]
    [InlineData(8192, 8256, false)]
    [InlineData(12288 + 4096, 8256, false)]
    public void Section_size_matching_allows_page_rounding(long maximumSize, long expected, bool matches)
        => Assert.Equal(matches, SectionPassthrough.SectionSizeMatches(maximumSize, expected));

    [Fact]
    public void The_passthrough_maps_the_test_processs_own_page_file_section()
    {
        // No elevation needed to duplicate a handle out of our own process, so the
        // whole algorithm - open, enumerate, resolve the Section type index, match by
        // size and magic, map - runs here exactly as it would against a container.
        var capacity = (int)ContainerDesktopFrame.Capacity(Geometry);
        var section = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)capacity, null);
        Assert.NotEqual(IntPtr.Zero, section);
        var view = IntPtr.Zero;
        try
        {
            view = Native.MapViewOfFile(section, 0xF001F, 0, 0, 0);
            Assert.NotEqual(IntPtr.Zero, view);
            Marshal.WriteInt32(view, 0, ContainerDesktopFrame.Magic);
            Marshal.WriteInt32(view, 8, Geometry.Width);
            Marshal.WriteInt32(view, 12, Geometry.Height);
            Marshal.WriteInt32(view, 64, 0x11223344);

            using var mapped = SectionPassthrough.TryOpenReadOnly(
                Environment.ProcessId, capacity, ContainerDesktopFrame.Magic, out var failure);

            Assert.True(mapped is not null, failure);
            Assert.Equal(capacity, mapped!.Length);
            Assert.Equal(Geometry.Width, Marshal.ReadInt32(mapped.BaseAddress, 8));
            Assert.Equal(0x11223344, Marshal.ReadInt32(mapped.BaseAddress, 64));
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                _ = Native.UnmapViewOfFile(view);
            }

            _ = Native.CloseHandle(section);
        }
    }

    [Fact]
    public void The_passthrough_rejects_a_size_or_magic_mismatch()
    {
        var capacity = (int)ContainerDesktopFrame.Capacity(Geometry);
        var section = Native.CreateFileMappingW(new IntPtr(-1), IntPtr.Zero, 0x04, 0, (uint)capacity, null);
        Assert.NotEqual(IntPtr.Zero, section);
        var view = IntPtr.Zero;
        try
        {
            view = Native.MapViewOfFile(section, 0xF001F, 0, 0, 0);
            Marshal.WriteInt32(view, 0, ContainerDesktopFrame.Magic);

            Assert.Null(SectionPassthrough.TryOpenReadOnly(
                Environment.ProcessId, capacity + 4096, ContainerDesktopFrame.Magic, out _));
            Assert.Null(SectionPassthrough.TryOpenReadOnly(
                Environment.ProcessId, capacity, 0xBADBAD, out _));
        }
        finally
        {
            if (view != IntPtr.Zero)
            {
                _ = Native.UnmapViewOfFile(view);
            }

            _ = Native.CloseHandle(section);
        }
    }

    [Fact]
    public void A_file_mode_source_is_mapped_without_a_passthrough()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-frame-{Guid.NewGuid():N}.shm");
        try
        {
            var buffer = new byte[(int)ContainerDesktopFrame.Capacity(Geometry)];
            Write(buffer, 0, ContainerDesktopFrame.Magic);
            Write(buffer, 8, Geometry.Width);
            Write(buffer, 12, Geometry.Height);
            Write(buffer, 16, Geometry.Stride);
            File.WriteAllBytes(path, buffer);

            using var source = SharedSectionSource.Open(path, ContainerDesktopFrame.Magic,
                header => ContainerDesktopFrame.Capacity(Geometry));

            Assert.False(source.IsPassthrough);
            Assert.Equal(Geometry.Width, Marshal.ReadInt32(source.BaseAddress, 8));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void Write(byte[] buffer, int offset, int value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect,
            uint maximumHigh, uint maximumLow, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, ulong length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
