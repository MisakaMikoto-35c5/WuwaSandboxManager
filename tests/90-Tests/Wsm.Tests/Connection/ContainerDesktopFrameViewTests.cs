using System.Diagnostics;
using Wsm.Connection.ContainerDesktop;
using Xunit;

namespace Wsm.Tests.Connection;

/// <summary>
/// The pure parts of the full-desktop frame viewer (Phase 4): the letterbox scaling
/// and its inverse coordinate transform, the base64 clipboard command, the viewer
/// lock-file staleness rules the agent's lifetime hangs on, the dynamic-resolution
/// handshake files, and the geometry-change remap decision. The two in-container
/// scripts are validated with the PowerShell parser.
/// </summary>
public sealed class ContainerDesktopFrameViewTests
{
    [Fact]
    public void An_exact_fit_needs_no_letterbox()
        => Assert.Equal(new FrameViewRect(0, 0, 1024, 768), FrameViewScaling.Fit(1024, 768, 1024, 768));

    [Fact]
    public void A_wide_client_letterboxes_left_and_right()
        => Assert.Equal(new FrameViewRect(512, 0, 1024, 768), FrameViewScaling.Fit(1024, 768, 2048, 768));

    [Fact]
    public void A_tall_client_letterboxes_top_and_bottom()
        => Assert.Equal(new FrameViewRect(0, 384, 1024, 768), FrameViewScaling.Fit(1024, 768, 1024, 1536));

    [Fact]
    public void Scaling_keeps_the_frame_aspect()
    {
        var fit = FrameViewScaling.Fit(1024, 768, 1000, 1000);

        Assert.Equal(new FrameViewRect(0, 125, 1000, 750), fit);
    }

    [Theory]
    [InlineData(0, 768, 1024, 768)]
    [InlineData(1024, 0, 1024, 768)]
    [InlineData(1024, 768, 0, 768)]
    [InlineData(1024, 768, 1024, 0)]
    public void An_empty_frame_or_client_fits_to_nothing(int frameWidth, int frameHeight, int clientWidth, int clientHeight)
        => Assert.Equal(default, FrameViewScaling.Fit(frameWidth, frameHeight, clientWidth, clientHeight));

    [Fact]
    public void Client_points_map_back_into_frame_coordinates()
    {
        // An exact fit is the identity.
        Assert.Equal((100, 100), FrameViewScaling.ToFrame(100, 100, 1024, 768, 1024, 768));

        // At half scale every client pixel is two frame pixels.
        Assert.Equal((512, 384), FrameViewScaling.ToFrame(256, 192, 1024, 768, 512, 384));
    }

    [Fact]
    public void Points_in_the_letterbox_clamp_to_the_frame_edge()
    {
        // 2048x768 client: the 1024x768 frame sits at x=512, letterboxed both sides.
        Assert.Equal((0, 300), FrameViewScaling.ToFrame(0, 300, 1024, 768, 2048, 768));
        Assert.Equal((1023, 300), FrameViewScaling.ToFrame(2047, 300, 1024, 768, 2048, 768));
        Assert.Equal((512, 0), FrameViewScaling.ToFrame(1024, -50, 1024, 768, 2048, 768));
    }

    [Fact]
    public void An_empty_client_maps_to_the_frame_origin()
        => Assert.Equal((0, 0), FrameViewScaling.ToFrame(50, 50, 1024, 768, 0, 0));

    [Fact]
    public void Clipboard_commands_round_trip_arbitrary_text()
    {
        const string text = "héllo — 日本語\r\nsecond line\tand a tab";

        var command = MirrorInputCommands.Clipboard(text);

        Assert.StartsWith("clipboard ", command);
        Assert.DoesNotContain("\n", command); // the file transport is line-based
        Assert.True(MirrorInputCommands.TryParseClipboard(command, out var parsed));
        Assert.Equal(text, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("move 1,2")]
    [InlineData("key 83 down")]
    [InlineData("clipboard not-valid-base64!!")]
    [InlineData("clipboards aGVsbG8=")]
    public void Only_well_formed_clipboard_commands_parse(string? command)
        => Assert.False(MirrorInputCommands.TryParseClipboard(command, out _));

    [Fact]
    public void A_lock_is_live_until_the_stale_boundary()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(ViewerLiveness.IsLive(now - ViewerLiveness.StaleAfter, now));
        Assert.False(ViewerLiveness.IsLive(now - ViewerLiveness.StaleAfter - TimeSpan.FromSeconds(1), now));
        // A write in the future (clock disagreement across the silo) counts as live.
        Assert.True(ViewerLiveness.IsLive(now + TimeSpan.FromMinutes(5), now));
    }

    [Fact]
    public void The_agent_exits_only_after_the_empty_delay_without_a_viewer()
    {
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(ViewerShouldExit(now, ViewerLiveness.EmptyExitDelay));
        Assert.True(ViewerShouldExit(now, ViewerLiveness.EmptyExitDelay + TimeSpan.FromSeconds(1)));

        static bool ViewerShouldExit(DateTime now, TimeSpan unseenFor)
            => ViewerLiveness.ShouldExit(now - unseenFor, now);
    }

    [Theory]
    [InlineData(1024, 768)]
    [InlineData(1600, 900)]
    public void View_requests_round_trip(int width, int height)
    {
        var formatted = ViewRequest.Format(width, height);

        Assert.True(ViewRequest.TryParse(formatted, out var parsedWidth, out var parsedHeight));
        Assert.Equal((width, height), (parsedWidth, parsedHeight));
    }

    [Theory]
    [InlineData(" 1024x768 \r\n", true)]
    [InlineData("1024X768", true)]
    [InlineData("", false)]
    [InlineData("1024-768", false)]
    [InlineData("1024x768x1", false)]
    [InlineData("10x10", false)]       // implausibly small
    [InlineData("99999x768", false)]   // implausibly large
    public void View_request_parsing_is_strict(string text, bool expected)
        => Assert.Equal(expected, ViewRequest.TryParse(text, out _, out _));

    [Theory]
    [InlineData("applied 1280x720", true, 1280, 720)]
    [InlineData("rejected 1280x720 (status -1)", false, 1280, 720)]
    public void View_request_acks_parse(string text, bool applied, int width, int height)
    {
        Assert.True(ViewRequest.TryParseAck(text, out var parsedApplied, out var parsedWidth, out var parsedHeight));
        Assert.Equal(applied, parsedApplied);
        Assert.Equal((width, height), (parsedWidth, parsedHeight));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("applied")]
    [InlineData("bogus 1280x720")]
    public void Malformed_acks_do_not_parse(string? text)
        => Assert.False(ViewRequest.TryParseAck(text, out _, out _, out _));

    [Fact]
    public void A_new_writer_or_a_new_geometry_requires_a_remap()
    {
        var geometry = new ContainerDesktopGeometry(1024, 768, 1024 * 4, 5);
        var mapped = new ContainerDesktopFrameHeader(geometry, 1234, ContainerDesktopMode.File);

        Assert.False(ContainerDesktopFrame.RequiresRemap(mapped, mapped));
        Assert.True(ContainerDesktopFrame.RequiresRemap(
            mapped, mapped with { WriterPid = 4321 }));
        Assert.True(ContainerDesktopFrame.RequiresRemap(
            mapped, mapped with { Geometry = new ContainerDesktopGeometry(1280, 720, 1280 * 4, 5) }));
        // An fps change alone is not a new source.
        Assert.False(ContainerDesktopFrame.RequiresRemap(
            mapped, mapped with { Geometry = geometry with { Fps = 10 } }));
    }

    [Fact]
    public void A_backing_mode_change_requires_a_remap()
    {
        var geometry = new ContainerDesktopGeometry(1024, 768, 1024 * 4, 5);
        var mapped = new ContainerDesktopFrameHeader(geometry, 1234, ContainerDesktopMode.Pagefile);

        Assert.True(ContainerDesktopFrame.RequiresRemap(
            mapped, mapped with { Mode = ContainerDesktopMode.File }));
    }

    [Theory]
    [InlineData("Wsm.Connection.ContainerDesktop.Agent.container-desktop-agent.ps1")]
    [InlineData("Wsm.Connection.ContainerDesktop.Agent.wsm-desktop-read.ps1")]
    public void The_embedded_agent_scripts_parse(string resourceName)
    {
        using var resource = typeof(IContainerDesktopProvider).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(resource);

        var path = Path.Combine(Path.GetTempPath(), $"wsm-parse-{Guid.NewGuid():N}.ps1");
        try
        {
            using (var file = File.Create(path))
            {
                resource!.CopyTo(file);
            }

            var quoted = path.Replace("'", "''");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$t=$null;$e=$null;"
                    + $"[void][System.Management.Automation.Language.Parser]::ParseFile('{quoted}',[ref]$t,[ref]$e);"
                    + "if($e.Count -gt 0){$e|ForEach-Object{Write-Host $_.Message};exit 1}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            })!;
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30000), "the PowerShell parser did not answer in 30 s");
            Assert.True(process.ExitCode == 0, $"the PowerShell parser rejected {resourceName}: {output}");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
