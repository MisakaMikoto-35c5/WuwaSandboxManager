using System.Runtime.InteropServices;

namespace Wsm.Connection.ContainerDesktop.Internal;

/// <summary>
/// Maps another process's <b>page-file-backed section</b> read-only into this process.
/// <para>
/// Named sections do not cross a container's silo boundary, but a process-isolated
/// container shares the host's PID space, so an elevated host can
/// <c>OpenProcess(PROCESS_DUP_HANDLE, writerPid)</c>, find the writer's section handle
/// by enumerating the system's handle table, <c>DuplicateHandle</c> it and
/// <c>MapViewOfFile</c> the result (measured: docs/investigation/13 §13.6A).
/// </para>
/// <para>
/// The section is identified by <b>size and magic</b>: candidates are the writer's
/// handles whose object type is Section (the type index is resolved, not assumed) and
/// whose <c>MaximumSize</c> matches the expected capacity; among those, the one whose
/// first bytes carry the expected magic wins. The writer publishes the header (magic
/// first) before it announces itself through the header file, so a validated header
/// file implies an initialised section.
/// </para>
/// </summary>
internal static class SectionPassthrough
{
    private const uint ProcessDupHandle = 0x0040;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SectionMapRead = 0x0004;
    private const uint SectionQuery = 0x0001;
    private const uint PageReadWrite = 0x04;
    private const uint FileMapRead = 0x0004;
    private const int SystemExtendedHandleInformation = 64;
    private const int ObjectTypeInformation = 2;
    private const int SectionBasicInformation = 0;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <summary>One row of the system handle table (the pure part, unit-tested).</summary>
    internal readonly record struct HandleEntry(int ProcessId, int ObjectTypeIndex, long HandleValue);

    /// <summary>A read-only view of a section obtained through handle passthrough.</summary>
    internal sealed class View : IDisposable
    {
        private IntPtr _section;
        private IntPtr _baseAddress;

        internal View(IntPtr section, IntPtr baseAddress, long length)
        {
            _section = section;
            _baseAddress = baseAddress;
            Length = length;
        }

        /// <summary>Base address of the mapped view (valid until <see cref="Dispose"/>).</summary>
        public IntPtr BaseAddress => _baseAddress;

        /// <summary>Size of the section in bytes.</summary>
        public long Length { get; }

        public void Dispose()
        {
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
        }
    }

    /// <summary>
    /// Whether a section's <c>MaximumSize</c> can be the expected source. The kernel
    /// rounds a page-file-backed section up to whole pages (measured: an 8256-byte
    /// section reports 12288), so the match is a one-page window; the magic check
    /// afterwards is the real discriminator.
    /// </summary>
    internal static bool SectionSizeMatches(long maximumSize, long expectedSize)
        => maximumSize >= expectedSize && maximumSize < expectedSize + 4096;

    /// <summary>
    /// Resolves and maps the writer's page-file-backed section, or returns
    /// <c>null</c> with a human-readable <paramref name="failure"/>.
    /// </summary>
    /// <param name="writerPid">Process id of the in-container writer (from the header).</param>
    /// <param name="expectedSize">Expected section size (the full layout capacity).</param>
    /// <param name="expectedMagic">Magic the section must carry at offset 0.</param>
    public static View? TryOpenReadOnly(int writerPid, long expectedSize, int expectedMagic, out string failure)
    {
        if (writerPid <= 0 || expectedSize <= 0)
        {
            failure = $"invalid passthrough request (pid={writerPid}, size={expectedSize})";
            return null;
        }

        var process = Native.OpenProcess(
            ProcessDupHandle | ProcessQueryLimitedInformation, false, writerPid);
        if (process == IntPtr.Zero)
        {
            failure = $"OpenProcess(pid {writerPid}) failed with {Marshal.GetLastWin32Error()} " +
                "(PROCESS_DUP_HANDLE on a container process needs an elevated reader)";
            return null;
        }

        // A section of our own, created before the enumeration: its row in the handle
        // table tells us this build's Section object type index without ever querying
        // a foreign handle (NtQueryObject can park on e.g. a pipe with pending I/O).
        var probe = Native.CreateFileMappingW(InvalidHandleValue, IntPtr.Zero, PageReadWrite, 0, 4096, null);
        if (probe == IntPtr.Zero)
        {
            failure = $"could not create the probe section ({Marshal.GetLastWin32Error()})";
            _ = Native.CloseHandle(process);
            return null;
        }

        try
        {
            if (!TryReadHandleTable(out var table, out failure))
            {
                return null;
            }

            var entries = ParseHandleTable(table);
            var sectionType = ResolveSectionTypeIndex(entries, probe, out var probeDetail);
            if (sectionType < 0)
            {
                failure = $"the Section object type index could not be resolved ({probeDetail})";
                return null;
            }

            foreach (var entry in entries)
            {
                if (entry.ProcessId != writerPid || entry.ObjectTypeIndex != sectionType)
                {
                    continue;
                }

                if (TryDuplicateMatchingSection(
                        process, entry.HandleValue, expectedSize, expectedMagic, out var view))
                {
                    failure = string.Empty;
                    return view;
                }
            }

            failure = $"pid {writerPid} has no section of {expectedSize} bytes carrying the expected magic " +
                "(wrong writer, or the agent is still starting)";
            return null;
        }
        finally
        {
            _ = Native.CloseHandle(probe);
            _ = Native.CloseHandle(process);
        }
    }

    /// <summary>
    /// Parses a <c>SYSTEM_HANDLE_INFORMATION_EX</c> buffer (class 64,
    /// SystemExtendedHandleInformation): a pointer-sized count plus reserved (16 bytes
    /// total), then 40-byte entries -
    /// Object(8) UniqueProcessId(8) HandleValue(8) GrantedAccess(4)
    /// CreatorBackTraceIndex(2) ObjectTypeIndex(2) HandleAttributes(4) Reserved(4).
    /// The extended class is required: class 16 truncates the pid and the handle value
    /// to 16 bits, which silently breaks on a busy machine (pids over 65535 exist).
    /// </summary>
    internal static IReadOnlyList<HandleEntry> ParseHandleTable(byte[] buffer)
    {
        var entries = new List<HandleEntry>();
        if (buffer.Length < 16)
        {
            return entries;
        }

        var count = BitConverter.ToInt32(buffer, 0);
        const int entrySize = 40;
        for (int i = 0, offset = 16;
             i < count && offset + entrySize <= buffer.Length;
             i++, offset += entrySize)
        {
            entries.Add(new HandleEntry(
                (int)BitConverter.ToInt64(buffer, offset + 8),
                BitConverter.ToUInt16(buffer, offset + 30),
                BitConverter.ToInt64(buffer, offset + 16)));
        }

        return entries;
    }

    private static int _sectionTypeIndex = -1;

    internal static bool ProbeHandleMatches(IntPtr probe, long handleValue)
        => handleValue == probe.ToInt64();

    /// <summary>
    /// Resolves the object type index of Section handles on this build by finding our
    /// own probe section in the handle table and confirming the type name. Cached: the
    /// index is stable for the lifetime of the boot.
    /// </summary>
    private static int ResolveSectionTypeIndex(
        IReadOnlyList<HandleEntry> entries, IntPtr probe, out string detail)
    {
        if (_sectionTypeIndex >= 0)
        {
            detail = "cached";
            return _sectionTypeIndex;
        }

        var ownPid = Environment.ProcessId;
        var probeValue = probe.ToInt64();
        var ownRows = 0;
        foreach (var entry in entries)
        {
            if (entry.ProcessId != ownPid)
            {
                continue;
            }

            ownRows++;
            if (!ProbeHandleMatches(probe, entry.HandleValue))
            {
                continue;
            }

            var typeName = GetTypeName(probe);
            if (typeName == "Section")
            {
                _sectionTypeIndex = entry.ObjectTypeIndex;
                detail = $"index {_sectionTypeIndex}";
                return _sectionTypeIndex;
            }

            detail = $"probe type name is '{typeName ?? "(null)"}'";
            return -1;
        }

        detail = $"probe handle 0x{probeValue:X} not among {entries.Count} entries ({ownRows} own)";
        return -1;
    }

    private static string? GetTypeName(IntPtr handle)
    {
        var buffer = Marshal.AllocHGlobal(1024);
        try
        {
            if (Native.NtQueryObject(handle, ObjectTypeInformation, buffer, 1024, out _) != 0)
            {
                return null;
            }

            // OBJECT_TYPE_INFORMATION starts with a UNICODE_STRING TypeName.
            var length = Marshal.ReadInt16(buffer, 0);
            var text = Marshal.ReadIntPtr(buffer, 8);
            return text == IntPtr.Zero || length <= 0 ? null : Marshal.PtrToStringUni(text, length / 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryDuplicateMatchingSection(
        IntPtr process, long handleValue, long expectedSize, int expectedMagic, out View? view)
    {
        view = null;
        if (!Native.DuplicateHandle(
                process, new IntPtr(handleValue), Native.GetCurrentProcess(),
                out var section, SectionMapRead | SectionQuery, false, 0))
        {
            return false;
        }

        try
        {
            if (!TryGetSectionSize(section, out var size) || !SectionSizeMatches(size, expectedSize))
            {
                return false;
            }

            var baseAddress = Native.MapViewOfFile(section, FileMapRead, 0, 0, 0);
            if (baseAddress == IntPtr.Zero)
            {
                return false;
            }

            if (Marshal.ReadInt32(baseAddress, 0) != expectedMagic)
            {
                _ = Native.UnmapViewOfFile(baseAddress);
                return false;
            }

            view = new View(section, baseAddress, expectedSize);
            section = IntPtr.Zero; // the view owns the handle now
            return true;
        }
        finally
        {
            if (section != IntPtr.Zero)
            {
                _ = Native.CloseHandle(section);
            }
        }
    }

    private static bool TryGetSectionSize(IntPtr section, out long size)
    {
        size = 0;
        var buffer = Marshal.AllocHGlobal(24);
        try
        {
            // SECTION_BASIC_INFORMATION: BaseAddress(8) Attributes(4+4 pad) MaximumSize(8)
            if (Native.NtQuerySection(section, SectionBasicInformation, buffer, 24, out _) != 0)
            {
                return false;
            }

            size = Marshal.ReadInt64(buffer, 16);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static bool TryReadHandleTable(out byte[] buffer, out string failure)
    {
        var size = 1 << 20;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            buffer = new byte[size];
            var status = Native.NtQuerySystemInformation(
                SystemExtendedHandleInformation, buffer, size, out var needed);
            if (status == 0)
            {
                failure = string.Empty;
                return true;
            }

            if (status != StatusInfoLengthMismatch)
            {
                failure = $"NtQuerySystemInformation(SystemExtendedHandleInformation) failed: 0x{status:X8}";
                return false;
            }

            size = Math.Max(needed, size * 2);
        }

        failure = "NtQuerySystemInformation(SystemExtendedHandleInformation) never fit its buffer";
        buffer = Array.Empty<byte>();
        return false;
    }

    private static class Native
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr attributes, uint protect,
            uint maximumHigh, uint maximumLow, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr sourceHandle,
            IntPtr targetProcess, out IntPtr targetHandle, uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, ulong length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnmapViewOfFile(IntPtr address);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int needed);

        [DllImport("ntdll.dll")]
        public static extern int NtQueryObject(IntPtr handle, int infoClass, IntPtr buffer, int length, out int needed);

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySection(IntPtr section, int infoClass, IntPtr buffer, int length, out int needed);
    }
}
