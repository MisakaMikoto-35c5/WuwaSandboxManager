using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wsm.Connection.ContainerDesktop;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Domain.ValueObjects;
using Wsm.Engines.Abstractions;
using Xunit;

namespace Wsm.Tests.Connection;

public sealed class FrameBufferHealthTests
{
    private static readonly ContainerDesktopGeometry Geometry = new(64, 32, 64 * 4, 4);

    [Fact]
    public async Task Attach_does_not_adopt_a_stale_frame_header()
    {
        var directory = NewDirectory();
        try
        {
            var frame = Path.Combine(directory, "wsm", "frame.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            WriteFrame(frame, 101, 7);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            var session = await provider.AttachAsync(Request(directory));

            Assert.Null(session);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_rejects_an_advancing_frame_writer_without_pixels()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var frame = Path.Combine(directory, "wsm", "frame.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            WriteFrame(frame, 101, 7);
            writer = AdvanceWriter(frame, 101, 7, cancelled.Token);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            var session = await provider.AttachAsync(Request(directory));

            Assert.Null(session);
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Attach_adopts_an_advancing_frame_writer_with_pixels()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var frame = Path.Combine(directory, "wsm", "frame.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            WriteFrame(frame, 101, 7, hasPixels: true);
            writer = AdvanceWriter(frame, 101, 7, cancelled.Token, hasPixels: true);
            var provider = CreateProvider(new CallbackEngine(() => { }));

            var session = await provider.AttachAsync(Request(directory));

            Assert.NotNull(session);
            Assert.Equal(frame, session.FramePath);
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Start_restarts_a_stale_frame_writer_and_waits_for_new_frames()
    {
        var directory = NewDirectory();
        using var cancelled = new CancellationTokenSource();
        Task? writer = null;
        try
        {
            var frame = Path.Combine(directory, "wsm", "frame.shm");
            Directory.CreateDirectory(Path.GetDirectoryName(frame)!);
            WriteFrame(frame, 101, 7);
            var engine = new CallbackEngine(() =>
            {
                WriteFrame(frame, 202, 1, hasPixels: true);
                writer = AdvanceWriter(frame, 202, 1, cancelled.Token, hasPixels: true);
            });
            var provider = CreateProvider(engine);

            var session = await provider.StartAsync(Request(directory));

            Assert.Equal(1, engine.ExecuteCalls);
            Assert.Equal(frame, session.FramePath);
            Assert.Equal(202, ReadWriterPid(frame));
        }
        finally
        {
            await StopWriter(cancelled, writer);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static FrameBufferDesktopProvider CreateProvider(CallbackEngine engine)
        => new(
            new StubEngineRegistry(engine),
            new StubSessionLauncher(),
            Options.Create(new ContainerDesktopOptions { FrameTimeoutSeconds = 3 }),
            NullLogger<FrameBufferDesktopProvider>.Instance);

    private static ContainerDesktopRequest Request(string root)
    {
        var id = SandboxInstanceId.New();
        return new ContainerDesktopRequest(id, id.ToString(), root, SandboxRecipe.Default);
    }

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wsm-frame-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFrame(string path, int writerPid, long sequence, bool hasPixels = false)
    {
        var buffer = new byte[ContainerDesktopFrame.Capacity(Geometry)];
        Write(buffer, 0, ContainerDesktopFrame.Magic);
        Write(buffer, 4, ContainerDesktopFrame.CurrentVersion);
        Write(buffer, 8, Geometry.Width);
        Write(buffer, 12, Geometry.Height);
        Write(buffer, 16, Geometry.Stride);
        Write(buffer, 20, Geometry.Fps);
        WriteLong(buffer, 24, sequence);
        Write(buffer, ContainerDesktopFrame.WriterPidOffset, writerPid);
        Write(buffer, ContainerDesktopFrame.ModeOffset, (int)ContainerDesktopMode.File);
        if (hasPixels)
        {
            buffer[ContainerDesktopFrame.PixelOffset] = 0xff;
        }
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, buffer);
        File.Move(temporary, path, overwrite: true);
    }

    private static int ReadWriterPid(string frame)
        => BitConverter.ToInt32(File.ReadAllBytes(frame), ContainerDesktopFrame.WriterPidOffset);

    private static Task AdvanceWriter(
        string frame, int writerPid, long initialSequence, CancellationToken cancellationToken,
        bool hasPixels = false)
        => Task.Run(async () =>
        {
            var sequence = initialSequence;
            try
            {
                while (true)
                {
                    await Task.Delay(40, cancellationToken);
                    sequence++;
                    WriteFrame(frame, writerPid, sequence, hasPixels);
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
