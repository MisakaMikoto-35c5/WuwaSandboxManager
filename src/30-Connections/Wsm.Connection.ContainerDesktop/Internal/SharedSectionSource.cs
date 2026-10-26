using System.Runtime.InteropServices;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// The read side of a desktop channel's frame source, shared by the viewers: given
/// the header file path (<c>--frame</c>), produce a read-only mapping of the bytes the
/// writer publishes. In <see cref="ContainerDesktopMode.File"/> mode that is the file
/// itself; in <see cref="ContainerDesktopMode.Pagefile"/> mode the file carries only
/// the header and the frames are reached through <see cref="SectionPassthrough"/>.
/// </summary>
internal static class SharedSectionSource
{
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint AttributeNormal = 0x80;
    private const uint PageReadOnly = 0x02;
    private const uint FileMapRead = 0x0004;

    /// <summary>A mapped frame source: either the <c>.shm</c> file or a passed-through section.</summary>
    internal sealed class Mapping : IDisposable
    {
        private readonly SectionPassthrough.View? _passthrough;
        private IntPtr _file = new(-1);
        private IntPtr _section;
        private IntPtr _baseAddress;

        private Mapping(SectionPassthrough.View? passthrough)
        {
            _passthrough = passthrough;
            if (passthrough is not null)
            {
                Length = passthrough.Length;
            }
        }

        /// <summary>Wraps a passed-through section view (pagefile mode).</summary>
        internal static Mapping FromPassthrough(SectionPassthrough.View view) => new(view);

        /// <summary>Base address of the mapped view.</summary>
        public IntPtr BaseAddress => _passthrough?.BaseAddress ?? _baseAddress;

        /// <summary>Mapped length in bytes.</summary>
        public long Length { get; private set; }

        /// <summary>True when the frames come from the writer's page-file-backed section.</summary>
        public bool IsPassthrough => _passthrough is not null;

        /// <summary>Maps the whole file read-only (file mode).</summary>
        public static Mapping OpenFile(string path)
        {
            var mapping = new Mapping(null);
            try
            {
                mapping._file = Native.CreateFileW(
                    path, GenericRead, ShareAll, IntPtr.Zero, OpenExisting, AttributeNormal, IntPtr.Zero);
                if (mapping._file == new IntPtr(-1))
                {
                    throw new IOException($"Cannot open '{path}' (error {Marshal.GetLastWin32Error()}).");
                }

                if (!Native.GetFileSizeEx(mapping._file, out var size))
                {
                    throw new IOException($"Cannot size '{path}' (error {Marshal.GetLastWin32Error()}).");
                }

                mapping._section = Native.CreateFileMappingW(mapping._file, IntPtr.Zero, PageReadOnly, 0, 0, null);
                if (mapping._section == IntPtr.Zero)
                {
                    throw new IOException($"Cannot map '{path}' (error {Marshal.GetLastWin32Error()}).");
                }

                mapping._baseAddress = Native.MapViewOfFile(mapping._section, FileMapRead, 0, 0, 0);
                if (mapping._baseAddress == IntPtr.Zero)
                {
                    throw new IOException($"Cannot view '{path}' (error {Marshal.GetLastWin32Error()}).");
                }

                mapping.Length = size;
                return mapping;
            }
            catch
            {
                mapping.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _passthrough?.Dispose();
            if (_baseAddress != IntPtr.Zero)
            {
                _ = Native.UnmapViewOfFile(_baseAddress);
                _baseAddress = IntPtr.Zero;
            }

            if (_section != IntPtr.Zero)
            {
                _ = Native.CloseHandle(_section);
                _section = IntPtr.Zero;
            }

            if (_file != new IntPtr(-1))
            {
                _ = Native.CloseHandle(_file);
                _file = new IntPtr(-1);
            }
        }
    }

    /// <summary>
    /// Opens the frame source behind a header file. If pagefile passthrough fails, asks
    /// the existing writer to switch to file mode and waits for its complete file buffer.
    /// </summary>
    /// <param name="headerPath">Path of the <c>.shm</c> file (the header in both modes).</param>
    /// <param name="magic">Magic the source must carry at offset 0.</param>
    /// <param name="capacityFor">
    /// Computes the expected total size of the source from the mapped header.
    /// </param>
    /// <exception cref="IOException">
    /// The file cannot be mapped, or the writer does not publish a complete file-backed
    /// buffer after the mode change request.
    /// </exception>
    public static Mapping Open(string headerPath, int magic, Func<IntPtr, long> capacityFor)
    {
        var file = Mapping.OpenFile(headerPath);
        try
        {
            if (Marshal.ReadInt32(file.BaseAddress, 0) != magic)
            {
                throw new IOException("The frame buffer does not carry the container desktop header.");
            }

            var mode = (ContainerDesktopMode)Marshal.ReadInt32(file.BaseAddress, ContainerDesktopFrame.ModeOffset);
            if (mode != ContainerDesktopMode.Pagefile)
            {
                return file;
            }

            var writerPid = Marshal.ReadInt32(file.BaseAddress, ContainerDesktopFrame.WriterPidOffset);
            var expectedSize = capacityFor(file.BaseAddress);
            var view = SectionPassthrough.TryOpenReadOnly(writerPid, expectedSize, magic, out var failure);
            if (view is not null)
            {
                file.Dispose();
                return Mapping.FromPassthrough(view);
            }

            file.Dispose();
            return OpenFileFallback(headerPath, magic, capacityFor, writerPid, expectedSize, failure);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static Mapping OpenFileFallback(
        string headerPath,
        int magic,
        Func<IntPtr, long> capacityFor,
        int writerPid,
        long expectedSize,
        string passthroughFailure)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(headerPath))
            ?? throw new IOException($"The frame buffer '{headerPath}' has no agent directory.");
        try
        {
            ShmModeNegotiation.WriteModeFile(directory, ContainerDesktopMode.File);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Pagefile passthrough failed ({passthroughFailure}) and file mode could not be requested.",
                ex);
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try
            {
                var candidate = Mapping.OpenFile(headerPath);
                try
                {
                    if (candidate.Length >= expectedSize
                        && candidate.Length >= ContainerDesktopFrame.PixelOffset
                        && Marshal.ReadInt32(candidate.BaseAddress, 0) == magic
                        && Marshal.ReadInt32(candidate.BaseAddress, ContainerDesktopFrame.ModeOffset)
                            == (int)ContainerDesktopMode.File
                        && Marshal.ReadInt32(candidate.BaseAddress, ContainerDesktopFrame.WriterPidOffset) == writerPid
                        && capacityFor(candidate.BaseAddress) == expectedSize)
                    {
                        return candidate;
                    }
                }
                catch
                {
                    candidate.Dispose();
                    throw;
                }

                candidate.Dispose();
            }
            catch (IOException)
            {
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new IOException(
                    $"Pagefile passthrough failed ({passthroughFailure}). File mode was requested from writer " +
                    $"pid {writerPid}, but it did not publish the complete {expectedSize}-byte file buffer.");
            }

            Thread.Sleep(50);
        }
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr attributes,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect,
            uint maximumHigh, uint maximumLow, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, ulong length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetFileSizeEx(IntPtr file, out long size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
