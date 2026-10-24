using System.Text;
using Wsm.Connection.ContainerDesktop;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// Version 2 of the mirror layout: the dirty-rect region behind the window table, the
/// shifted title blob, and the structured input commands. The layout is the contract
/// with the in-container read side - pin it down.
/// </summary>
public sealed class ContainerDesktopMirrorV2Tests
{
    private static readonly ContainerDesktopGeometry Geometry = new(64, 32, 64 * 4, 4);

    [Fact]
    public void The_dirty_region_sits_between_the_table_and_the_titles()
    {
        var table = ContainerDesktopMirror.TableOffset(Geometry);

        Assert.Equal(ContainerDesktopMirror.PixelOffset + (Geometry.Stride * Geometry.Height), table);
        Assert.Equal(table + ContainerDesktopMirror.WindowTableBytes,
            ContainerDesktopMirror.DirtyOffset(Geometry));
        Assert.Equal(ContainerDesktopMirror.MaxWindows * ContainerDesktopMirror.DirtyEntrySize,
            ContainerDesktopMirror.DirtyRegionBytes);
        Assert.Equal(ContainerDesktopMirror.DirtyOffset(Geometry) + ContainerDesktopMirror.DirtyRegionBytes,
            ContainerDesktopMirror.TitleOffset(Geometry, ContainerDesktopMirror.CurrentVersion));
        Assert.Equal(ContainerDesktopMirror.TitleOffset(Geometry, ContainerDesktopMirror.CurrentVersion)
                + ContainerDesktopMirror.TitleBlobBytes,
            ContainerDesktopMirror.Capacity(Geometry));
    }

    [Fact]
    public void Version_one_files_keep_the_title_blob_behind_the_table()
    {
        Assert.Equal(
            ContainerDesktopMirror.TableOffset(Geometry) + ContainerDesktopMirror.WindowTableBytes,
            ContainerDesktopMirror.TitleOffset(Geometry, ContainerDesktopMirror.LegacyVersion));
        Assert.Equal(
            ContainerDesktopMirror.TitleOffset(Geometry, ContainerDesktopMirror.LegacyVersion)
                + ContainerDesktopMirror.TitleBlobBytes,
            ContainerDesktopMirror.Capacity(Geometry, ContainerDesktopMirror.LegacyVersion));
    }

    [Fact]
    public void A_version_two_file_round_trips_windows_and_dirty_rects()
    {
        var buffer = new byte[ContainerDesktopMirror.Capacity(Geometry)];
        Write(buffer, 0, ContainerDesktopMirror.Magic);
        Write(buffer, 4, ContainerDesktopMirror.CurrentVersion);
        Write(buffer, 8, Geometry.Width);
        Write(buffer, 12, Geometry.Height);
        Write(buffer, 16, Geometry.Stride);
        Write(buffer, 20, Geometry.Fps);
        WriteLong(buffer, 24, 9L);
        Write(buffer, 40, 1);
        Write(buffer, ContainerDesktopMirror.DirtyCountOffset, 2);

        var table = (int)ContainerDesktopMirror.TableOffset(Geometry);
        WriteLong(buffer, table, 0x1234L);
        Write(buffer, table + 8, 11);
        Write(buffer, table + 12, 22);
        Write(buffer, table + 16, 33);
        Write(buffer, table + 20, 44);
        WriteLong(buffer, table + 24, 9L);
        Write(buffer, table + 32, 0);
        Write(buffer, table + 36, 5);
        var title = Encoding.Unicode.GetBytes("First");
        title.CopyTo(buffer.AsSpan((int)ContainerDesktopMirror.TitleOffset(
            Geometry, ContainerDesktopMirror.CurrentVersion)));

        var dirty = (int)ContainerDesktopMirror.DirtyOffset(Geometry);
        Write(buffer, dirty, 3);
        Write(buffer, dirty + 4, 4);
        Write(buffer, dirty + 8, 16);
        Write(buffer, dirty + 12, 8);
        Write(buffer, dirty + 16, 40);
        Write(buffer, dirty + 20, 30);
        Write(buffer, dirty + 24, 24);
        Write(buffer, dirty + 28, 12);

        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopMirror.TryReadHeaderInfo(path);
            Assert.NotNull(header);
            Assert.Equal(Geometry, header!.Value.Geometry);
            Assert.Equal(ContainerDesktopMirror.CurrentVersion, header.Value.Version);
            Assert.Equal(1, header.Value.WindowCount);
            Assert.Equal(9L, header.Value.Sequence);
            Assert.Equal(2, header.Value.DirtyCount);

            var windows = ContainerDesktopMirror.ReadWindows(
                path, Geometry, header.Value.WindowCount, header.Value.Version);
            Assert.Single(windows);
            Assert.Equal("First", windows[0].Title);
            Assert.Equal((11, 22, 33, 44), (windows[0].X, windows[0].Y, windows[0].Width, windows[0].Height));

            var rects = ContainerDesktopMirror.ReadDirtyRects(path, Geometry, header.Value.DirtyCount);
            Assert.Equal(2, rects.Count);
            Assert.Equal(new ContainerDesktopRect(3, 4, 16, 8), rects[0]);
            Assert.Equal(new ContainerDesktopRect(40, 30, 24, 12), rects[1]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_version_one_file_reports_no_dirty_rects()
    {
        var buffer = new byte[ContainerDesktopMirror.Capacity(Geometry, ContainerDesktopMirror.LegacyVersion)];
        Write(buffer, 0, ContainerDesktopMirror.Magic);
        Write(buffer, 4, ContainerDesktopMirror.LegacyVersion);
        Write(buffer, 8, Geometry.Width);
        Write(buffer, 12, Geometry.Height);
        Write(buffer, 16, Geometry.Stride);
        Write(buffer, 20, Geometry.Fps);
        WriteLong(buffer, 24, 3L);

        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopMirror.TryReadHeaderInfo(path);
            Assert.NotNull(header);
            Assert.Equal(ContainerDesktopMirror.LegacyVersion, header!.Value.Version);
            Assert.Equal(0, header.Value.DirtyCount);
            Assert.Empty(ContainerDesktopMirror.ReadDirtyRects(path, Geometry, header.Value.DirtyCount));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_excessive_dirty_count_is_clamped_and_empty_rects_are_skipped()
    {
        var buffer = new byte[ContainerDesktopMirror.Capacity(Geometry)];
        Write(buffer, 0, ContainerDesktopMirror.Magic);
        Write(buffer, 4, ContainerDesktopMirror.CurrentVersion);
        Write(buffer, 8, Geometry.Width);
        Write(buffer, 12, Geometry.Height);
        Write(buffer, 16, Geometry.Stride);
        Write(buffer, 20, Geometry.Fps);
        Write(buffer, ContainerDesktopMirror.DirtyCountOffset, 5000);

        var dirty = (int)ContainerDesktopMirror.DirtyOffset(Geometry);
        Write(buffer, dirty + 8, 0);   // zero width: skipped
        Write(buffer, dirty + 12, 8);
        Write(buffer, dirty + 16, 10);
        Write(buffer, dirty + 20, 20);
        Write(buffer, dirty + 24, 30);
        Write(buffer, dirty + 28, 40);

        var path = Path.Combine(Path.GetTempPath(), $"wsm-mirror-{Guid.NewGuid():N}.shm");
        try
        {
            File.WriteAllBytes(path, buffer);

            var header = ContainerDesktopMirror.TryReadHeaderInfo(path);
            Assert.Equal(ContainerDesktopMirror.MaxDirtyRects, header!.Value.DirtyCount);

            var rects = ContainerDesktopMirror.ReadDirtyRects(path, Geometry, header.Value.DirtyCount);
            Assert.Single(rects); // only the one non-empty entry survives
            Assert.Equal(new ContainerDesktopRect(10, 20, 30, 40), rects[0]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Structured_key_commands_carry_the_virtual_key()
    {
        Assert.Equal("key 83 down", MirrorInputCommands.Key(0x53, true));
        Assert.Equal("key 83 up", MirrorInputCommands.Key(0x53, false));
        Assert.Equal("key 13 down", MirrorInputCommands.Key(0x0D, true));
    }

    [Fact]
    public void Clicks_carry_button_and_count()
    {
        Assert.Equal("click 10,20,left", MirrorInputCommands.Click(10, 20));
        Assert.Equal("click 10,20,right", MirrorInputCommands.Click(10, 20, "right"));
        Assert.Equal("click 10,20,left,2", MirrorInputCommands.Click(10, 20, "left", 2));
        Assert.Equal("click -3,-4,middle,2", MirrorInputCommands.Click(-3, -4, "middle", 2));
    }

    [Fact]
    public void Window_commands_match_the_read_side_grammar()
    {
        Assert.Equal("move 5,6", MirrorInputCommands.MovePointer(5, 6));
        Assert.Equal("wheel -120", MirrorInputCommands.Wheel(-120));
        Assert.Equal("activate 4660", MirrorInputCommands.Activate(0x1234));
        Assert.Equal("movewindow 4660 5,6", MirrorInputCommands.MoveWindow(0x1234, 5, 6));
        Assert.Equal("resize 4660 800,600", MirrorInputCommands.ResizeWindow(0x1234, 800, 600));
        Assert.Equal("close 4660", MirrorInputCommands.CloseWindow(0x1234));
        Assert.Equal("keys CTRL+S", MirrorInputCommands.Keys("CTRL+S"));
        Assert.Equal("text héllo — 日本語", MirrorInputCommands.Text("héllo — 日本語"));
        Assert.Equal("launch notepad.exe a.txt", MirrorInputCommands.Launch("notepad.exe a.txt"));
    }

    private static void Write(byte[] buffer, int offset, int value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteLong(byte[] buffer, int offset, long value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);
}
