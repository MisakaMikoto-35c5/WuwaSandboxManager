using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wsm.Cli;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Xunit;

namespace Wsm.Tests.Connection;

public sealed class WindowMirrorHealthTests
{
    private static readonly ContainerDesktopGeometry Geometry = new(64, 32, 64 * 4, 4);

    [Theory]
    [InlineData(null)]
    [InlineData("no-pixels")]
    public void Print_table_rejects_missing_or_no_pixel_health(string? health)
    {
        var directory = NewDirectory();
        try
        {
            var mirror = Path.Combine(directory, "mirror.shm");
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            if (health is not null)
            {
                WriteStatus(mirror, 101, 7, health);
            }

            var exit = DesktopMirrorViewCommand.PrintTable(mirror, waitSeconds: 0);

            Assert.Equal(1, exit);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Print_table_rejects_a_stale_live_status()
    {
        var directory = NewDirectory();
        try
        {
            var mirror = Path.Combine(directory, "mirror.shm");
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            WriteStatus(mirror, 101, 7, "live");

            var exit = DesktopMirrorViewCommand.PrintTable(mirror, waitSeconds: 0);

            Assert.Equal(1, exit);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Print_table_rejects_an_empty_window_table_with_a_live_heartbeat()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var mirror = Path.Combine(directory, "mirror.shm");
            WriteMirror(mirror, writerPid: 101, sequence: 7, windowCount: 0);
            WriteStatus(mirror, 101, 7, "live");
            writer = AdvanceWriter(mirror, 101, 7, "live", cancelled.Token, windowCount: 0);

            var exit = DesktopMirrorViewCommand.PrintTable(mirror, waitSeconds: 0);

            Assert.Equal(1, exit);
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_rejects_an_empty_window_table_with_a_live_heartbeat()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7, windowCount: 0);
            WriteStatus(mirror, 101, 7, "live");
            writer = AdvanceWriter(mirror, 101, 7, "live", cancelled.Token, windowCount: 0);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            ContainerDesktopSession? session = null;
            SandboxBackendException? error = null;
            try
            {
                session = await provider.AttachAsync(Request(directory));
            }
            catch (SandboxBackendException ex)
            {
                error = ex;
            }

            Assert.Null(session);
            if (error is not null)
            {
                Assert.Contains("window", error.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_rejects_no_pixels_even_with_a_valid_window_table()
    {
        var directory = NewDirectory();
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            WriteStatus(mirror, 101, 7, "no-pixels");
            var provider = CreateProvider(new CallbackEngine(() => { }));

            var exception = await Assert.ThrowsAsync<SandboxBackendException>(
                () => provider.AttachAsync(Request(directory)));

            Assert.Contains("pixel", exception.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_does_not_adopt_unknown_health()
    {
        var directory = NewDirectory();
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            Assert.Null(await provider.AttachAsync(Request(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_does_not_adopt_a_stale_heartbeat()
    {
        var directory = NewDirectory();
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            WriteStatus(mirror, 101, 7, "live");
            var provider = CreateProvider(new CallbackEngine(() => { }));

            Assert.Null(await provider.AttachAsync(Request(directory)));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_adopts_an_advancing_live_writer()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            WriteStatus(mirror, 101, 7, "live");
            writer = AdvanceWriter(mirror, 101, 7, "live", cancelled.Token);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            var session = await provider.AttachAsync(Request(directory));

            Assert.NotNull(session);
            Assert.Equal(mirror, session.FramePath);
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Start_restarts_a_stale_writer_and_waits_for_the_new_live_one()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var mirror = Path.Combine(directory, "wsm", "mirror.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(mirror)!);
            WriteMirror(mirror, writerPid: 101, sequence: 7);
            WriteStatus(mirror, 101, 7, "live");
            var engine = new CallbackEngine(() =>
            {
                WriteMirror(mirror, writerPid: 202, sequence: 1);
                WriteStatus(mirror, 202, 1, "live");
                writer = AdvanceWriter(mirror, 202, 1, "live", cancelled.Token);
            });
            var provider = CreateProvider(engine);

            var session = await provider.StartAsync(Request(directory));

            Assert.Equal(1, engine.ExecuteCalls);
            Assert.Equal(mirror, session.FramePath);
            Assert.Equal(202, ReadStatusWriterPid(mirror));
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static WindowMirrorDesktopProvider CreateProvider(CallbackEngine engine)
        => new(
            new StubEngineRegistry(engine),
            new StubSessionLauncher(),
            Options.Create(new ContainerDesktopOptions { FrameTimeoutSeconds = 3 }),
            NullLogger<WindowMirrorDesktopProvider>.Instance);

    private static ContainerDesktopRequest Request(string root)
    {
        var id = SandboxInstanceId.New();
        return new ContainerDesktopRequest(id, id.ToString(), root, SandboxRecipe.Default);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteMirror(string path, int writerPid, long sequence, int windowCount = 1)
    {
        var buffer = new byte[ContainerDesktopMirror.Capacity(Geometry)];
        Write(buffer, 0, ContainerDesktopMirror.Magic);
        Write(buffer, 4, ContainerDesktopMirror.CurrentVersion);
        Write(buffer, 8, Geometry.Width);
        Write(buffer, 12, Geometry.Height);
        Write(buffer, 16, Geometry.Stride);
        Write(buffer, 20, Geometry.Fps);
        WriteLong(buffer, 24, sequence);
        Write(buffer, ContainerDesktopMirror.WriterPidOffset, writerPid);
        Write(buffer, 40, windowCount);
        Write(buffer, ContainerDesktopMirror.ModeOffset, (int)ContainerDesktopMode.File);
        var table = (int)ContainerDesktopMirror.TableOffset(Geometry);
        WriteLong(buffer, table, 0x1234);
        Write(buffer, table + 8, 0);
        Write(buffer, table + 12, 0);
        Write(buffer, table + 16, 32);
        Write(buffer, table + 20, 16);
        WriteLong(buffer, table + 24, sequence);
        Write(buffer, table + 32, 0);
        Write(buffer, table + 36, 1);
        buffer[(int)ContainerDesktopMirror.TitleOffset(Geometry, ContainerDesktopMirror.CurrentVersion)] = (byte)'X';
        File.WriteAllBytes(path, buffer);
    }

    private static void WriteStatus(string mirror, int writerPid, long sequence, string health)
        => File.WriteAllText(
            mirror + ".status",
            $"version=1\nwriterPid={writerPid}\nsequence={sequence}\nhealth={health}\n");

    private static int ReadStatusWriterPid(string mirror)
        => File.ReadAllLines(mirror + ".status")
            .Where(line => line.StartsWith("writerPid=", StringComparison.Ordinal))
            .Select(line => int.Parse(line["writerPid=".Length..]))
            .Single();

    private static Task AdvanceWriter(
        string mirror, int writerPid, long initialSequence, string health, CancellationToken cancellationToken,
        int windowCount = 1)
        => Task.Run(async () =>
        {
            var sequence = initialSequence;
            try
            {
                while (true)
                {
                    await Task.Delay(40, cancellationToken);
                    sequence++;
                    WriteMirror(mirror, writerPid, sequence, windowCount);
                    WriteStatus(mirror, writerPid, sequence, health);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }, cancellationToken);

    private static async Task StopWriter(CancellationTokenSource cancelled, Task? writer)
    {
        cancelled.Cancel();
        if (writer is not null)
        {
            await writer.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static void Write(byte[] buffer, int offset, int value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteLong(byte[] buffer, int offset, long value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private sealed class CallbackEngine(Action onExecute) : ISandboxEngine
    {
        public int ExecuteCalls { get; private set; }

        public string Name => "loopback";

        public SandboxEngineKind Kind => SandboxEngineKind.WindowsContainer;

        public SandboxEngineCapabilities Capabilities => SandboxEngineCapabilities.None;

        public bool IsAvailable => true;

        public Task<int> ExecuteAsync(
            SandboxInstanceId id, LogonCommand command, CancellationToken cancellationToken = default)
        {
            ExecuteCalls++;
            onExecute();
            return Task.FromResult(0);
        }

        public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SandboxRuntimeInfo> StartAsync(
            SandboxStartRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SandboxRuntimeInfo?> GetRuntimeAsync(
            SandboxInstanceId id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubEngineRegistry(CallbackEngine engine) : ISandboxEngineRegistry
    {
        public IReadOnlyList<ISandboxEngine> Engines { get; } = [engine];

        public ISandboxEngine? Find(SandboxEngineKind kind)
            => kind == SandboxEngineKind.WindowsContainer ? engine : null;
    }

    private sealed class StubSessionLauncher : ISessionProcessLauncher
    {
        public bool CanLaunchIntoInteractiveSession => true;

        public SessionProcessLaunchResult Launch(string commandLine, string? workingDirectory = null)
            => throw new NotSupportedException();
    }
}
