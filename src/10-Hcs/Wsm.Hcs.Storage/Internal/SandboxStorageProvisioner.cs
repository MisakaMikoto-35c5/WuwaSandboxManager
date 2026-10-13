using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Layout;

namespace Wsm.Hcs.Storage.Internal;

/// <summary>
/// Default <see cref="ISandboxStorageProvisioner"/>.
/// <para>
/// Strategy, in order:
/// </para>
/// <list type="number">
///   <item>reuse an existing <c>sandbox.vhdx</c> in the target directory when its
///   chain is intact and lives inside that directory (an external/compressed
///   parent is rebuilt rather than mutated);</item>
///   <item>reference the platform's <b>prepared base image</b> in place: the
///   platform never boots a sandbox on a layer's storage, it first materialises a
///   base image (a child of the layer storage that has completed the guest's
///   first-boot/specialization) and clones every sandbox from it. A donor's
///   immediate parent is that base image, and a differencing child
///   <c>sandbox.vhdx</c> is created over it. The base is never copied — it is the
///   shared, Windows Update-maintained image, so a per-sandbox copy would freeze
///   a stale OS and waste gigabytes. Only an explicitly configured base image is
///   materialised inside the sandbox folder;</item>
///   <item>copy a <b>blank/small standalone donor</b> container storage
///   (fast path — an existing Windows Sandbox storage under
///   <c>ContainerStorages\*</c> that is <i>not</i> a differencing child and has
///   not grown past <see cref="BlankDonorSizeThresholdBytes"/>);</item>
///   <item>copy a platform <b>template</b> disk if one is configured;</item>
///   <item>otherwise create a <b>blank dynamic VHDX</b> from scratch.</item>
/// </list>
/// <para>
/// Why the chain matters: HCS's synthetic storage refuses any disk in the
/// parent chain that carries the NTFS <c>Compressed</c> attribute
/// (<c>Start</c> fails with <c>0xC03A001A</c>,
/// <c>STATUS_VIRTUAL_DISK_LIMITATION</c>). The host's
/// <c>...\Windows\Containers</c> tree is compressed, so a copied differencing
/// child still points at a compressed parent. Copying the base <b>uncompressed</b>
/// and building our own child breaks that dependency.
/// </para>
/// Every copy step is best-effort: a donor that is locked (e.g. the disk of a
/// <i>running</i> sandbox), that has been used, or that fails for any IO reason
/// is skipped so provisioning falls through to the next strategy rather than
/// surfacing a raw <see cref="IOException"/>.
/// <para>
/// The guest-state file is taken from the same source that supplied the disk,
/// when present, otherwise created with <c>HcsCreateEmptyGuestStateFile</c>. A
/// donor's <c>sandbox.vmgs</c> is <b>never</b> paired with a pre-existing target
/// disk.
/// </para>
/// </summary>
[ExposeServices(typeof(ISandboxStorageProvisioner))]
internal sealed class SandboxStorageProvisioner : ISandboxStorageProvisioner, ISingletonDependency
{
    /// <summary>Environment override pointing at a platform storage template.</summary>
    public const string TemplateRootEnvironmentVariable = "WSM_HCS_STORAGE_TEMPLATE";

    /// <summary>
    /// File name used for a base OS disk that is <b>materialised inside the
    /// sandbox folder</b> when the user explicitly configured one. It is
    /// <b>not</b> produced by default: the default path references the platform's
    /// own base OS disk in place, so the platform keeps it current through
    /// Windows Update and no gigabytes are duplicated per sandbox.
    /// </summary>
    public const string BaseDiskFileName = "sandbox.base.vhdx";

    /// <summary>
    /// Largest on-disk size (in bytes) a donor <c>sandbox.vhdx</c> may have and
    /// still be treated as <b>blank</b>. A freshly-created Windows Sandbox disk
    /// is ~4&#160;MB; once a sandbox has run it grows to tens or hundreds of MB
    /// (observed 88&#160;MB / 874&#160;MB / 917&#160;MB). Donors above this
    /// threshold have been used and would leak another sandbox's data, so they
    /// are rejected in favour of the template or a fresh blank disk.
    /// </summary>
    public const long BlankDonorSizeThresholdBytes = 16L * 1024 * 1024;

    /// <summary>Guard against a corrupt/looping parent chain.</summary>
    private const int MaxChainDepth = 16;

    private readonly IHcsHostLayout _layout;
    private readonly IVirtualDiskFactory _diskFactory;
    private readonly IHcsClient _hcs;

    public SandboxStorageProvisioner(
        IHcsHostLayout layout,
        IVirtualDiskFactory diskFactory,
        IHcsClient hcs)
    {
        _layout = layout;
        _diskFactory = diskFactory;
        _hcs = hcs;
    }

    public StorageLayout Provision(string storageDirectory, uint diskSizeMb = 0)
    {
        if (string.IsNullOrWhiteSpace(storageDirectory))
        {
            throw new SandboxValidationException("The sandbox storage directory must not be empty.");
        }

        Directory.CreateDirectory(storageDirectory);

        // A directory created under the (compressed) container tree inherits the
        // attribute, and so would every file we write into it. Clear it first so
        // the VHDX files stay uncompressed and HCS's synthetic storage accepts
        // the chain.
        NtfsCompression.TryUncompress(storageDirectory);

        var disk = Path.Combine(storageDirectory, SandboxStorageTemplateLocator.DiskFileName);
        var guestState = Path.Combine(storageDirectory, SandboxStorageTemplateLocator.GuestStateFileName);

        var locator = new SandboxStorageTemplateLocator(
            _layout.PlatformContainerStoragesFolder,
            Environment.GetEnvironmentVariable(TemplateRootEnvironmentVariable));

        // The source that actually produced the disk during this call. The
        // guest-state file is only copied from the same source: a donor's vmgs
        // must never be paired with a pre-existing (foreign) target disk.
        string? diskSource = null;

        // The parent of the (differencing) user-data disk. By default this is the
        // platform's own base OS disk, referenced in place — never copied.
        string? parentPath = null;

        if (!IsReusableExistingDisk(disk, storageDirectory))
        {
            // Discard an unusable leftover (e.g. from a previous failed start)
            // before rebuilding.
            TryDeletePartialCopy(disk);

            if (TryCreateChildOverPlatformBase(locator, storageDirectory, disk, out parentPath, out var chainSource))
            {
                diskSource = chainSource;
            }
            else if (TryCopyStandaloneDisk(locator.FindDonorStorage(), disk, requireBlank: true))
            {
                diskSource = locator.FindDonorStorage();
            }
            else if (TryCopyStandaloneDisk(locator.FindTemplateStorage(), disk, requireBlank: false))
            {
                diskSource = locator.FindTemplateStorage();
            }
            else
            {
                CreateBlankDisk(disk, diskSizeMb);
            }
        }
        else
        {
            // A reusable existing disk: remember its parent so the engine can
            // grant access to it when it lives inside the sandbox folder.
            try
            {
                parentPath = _diskFactory.TryGetParentPath(disk);
            }
            catch
            {
                parentPath = null;
            }
        }

        EnsureGuestState(guestState, diskSource);

        return new StorageLayout(storageDirectory, disk, guestState, parentPath);
    }

    /// <summary>
    /// True when an existing <paramref name="disk"/> can be kept: a standalone
    /// disk, or a differencing child whose parent still exists. The parent is
    /// normally the platform's prepared base image (outside the sandbox folder)
    /// and is left untouched; a parent inside the folder is ours and is kept
    /// uncompressed. A missing parent means the chain is broken and the disk is
    /// rebuilt.
    /// <para>
    /// A chain whose direct parent is the platform's <b>raw layer storage</b> (a
    /// no-parent disk under the platform container-storages folder) is also
    /// rebuilt: that is the shape produced before the prepared base image was
    /// used, and it boots an unspecialized guest (the shell hangs, the Start menu
    /// never opens). Rebuilding repairs an existing sandbox on its next start.
    /// </para>
    /// </summary>
    private bool IsReusableExistingDisk(string disk, string storageDirectory)
    {
        if (!File.Exists(disk))
        {
            return false;
        }

        try
        {
            var parent = _diskFactory.TryGetParentPath(disk);
            if (parent is null)
            {
                NtfsCompression.TryUncompress(disk);
                return true;
            }

            if (!File.Exists(parent))
            {
                return false;
            }

            // The platform's raw layer storage is a no-parent disk under the
            // platform's container-storages folder; a disk chained to it boots an
            // unspecialized guest, so it is rebuilt over the prepared base image.
            if (IsWithinDirectory(parent, _layout.PlatformContainerStoragesFolder) &&
                TryGetParent(parent) is null)
            {
                return false;
            }

            // Only a parent inside the sandbox folder is ours to keep
            // uncompressed; the platform's shared base is referenced, never
            // mutated.
            if (IsWithinDirectory(parent, storageDirectory))
            {
                NtfsCompression.TryUncompress(parent);
            }

            NtfsCompression.TryUncompress(disk);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Creates the user-data disk as a differencing child of the platform's
    /// <b>prepared base image</b>, <b>referencing it in place</b>.
    /// <para>
    /// A Windows Sandbox guest boots from a union of the read-only layer
    /// (<c>VirtualSmb.Shares["os"]</c>) and a writable OS disk. The platform never
    /// derives that writable disk from a layer's storage directly: it first
    /// materialises a <i>base image</i> — a differencing child of the layer
    /// storage that has completed the guest's first boot / specialization (the
    /// shell, the Start menu and the XAML/AppX packages are registered there) —
    /// and clones every sandbox from it. Chaining straight to the layer storage's
    /// no-parent disk therefore boots an <b>unspecialized</b> guest: the session
    /// comes up, but <c>explorer.exe</c> hangs and the Start menu never opens.
    /// </para>
    /// <para>
    /// The prepared base is the donor's <b>immediate parent</b> (which is itself a
    /// differencing child). When no prepared base exists, the previous behaviour —
    /// the chain root — is used as a fallback so a machine that has not prepared a
    /// base image yet still provisions.
    /// </para>
    /// Returns <see langword="false"/> when no donor chain resolves to an existing
    /// base disk, so the caller can fall through to the standalone-copy/blank
    /// strategies.
    /// </summary>
    private bool TryCreateChildOverPlatformBase(
        SandboxStorageTemplateLocator locator,
        string storageDirectory,
        string disk,
        out string? parentPath,
        out string? source)
    {
        parentPath = null;
        source = null;

        var donors = locator.EnumerateDonorStorages();

        // Preferred: the platform's prepared base image — a donor whose immediate
        // parent is itself a differencing child (its parent is the layer storage).
        foreach (var donor in donors)
        {
            var donorDisk = Path.Combine(donor, SandboxStorageTemplateLocator.DiskFileName);
            if (!File.Exists(donorDisk) || IsWithinDirectory(donorDisk, storageDirectory))
            {
                continue;
            }

            var prepared = ResolvePreparedBase(donorDisk);
            if (prepared is null)
            {
                continue;
            }

            if (TryChain(disk, prepared, donor, out parentPath, out source))
            {
                return true;
            }
        }

        // Fallback (previous behaviour): the no-parent chain root. This is the
        // layer's storage, which boots an unspecialized guest; it is only used
        // when the platform has not prepared a base image.
        foreach (var donor in donors)
        {
            var donorDisk = Path.Combine(donor, SandboxStorageTemplateLocator.DiskFileName);
            if (!File.Exists(donorDisk) || IsWithinDirectory(donorDisk, storageDirectory))
            {
                continue;
            }

            var root = ResolveChainRoot(donorDisk);
            if (root is null ||
                !File.Exists(root) ||
                string.Equals(root, donorDisk, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryChain(disk, root, donor, out parentPath, out source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a donor's prepared base image: its immediate parent when that
    /// parent is itself a differencing child (i.e. a base image derived from the
    /// layer storage), or <see langword="null"/> when the donor's parent is the
    /// layer storage's no-parent disk or is missing.
    /// </summary>
    private string? ResolvePreparedBase(string donorDisk)
    {
        var candidate = TryGetParent(donorDisk);
        if (candidate is null ||
            string.Equals(candidate, donorDisk, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(candidate))
        {
            return null;
        }

        // A prepared base image is itself a differencing child; the layer storage
        // is a no-parent disk and must not be treated as the base to clone.
        return TryGetParent(candidate) is null ? null : candidate;
    }

    /// <summary>Reads a disk's parent path, tolerating an unreadable chain.</summary>
    private string? TryGetParent(string disk)
    {
        try
        {
            return _diskFactory.TryGetParentPath(disk);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Uncompresses <paramref name="baseDisk"/> in place and creates
    /// <paramref name="disk"/> as its differencing child. A partial child is
    /// cleaned up on failure so the caller can try the next strategy. The base is
    /// only ever uncompressed (a metadata change), never copied: its content stays
    /// current with Windows Update and no gigabytes are duplicated per sandbox.
    /// </summary>
    private bool TryChain(
        string disk,
        string baseDisk,
        string sourceDirectory,
        out string? parentPath,
        out string? source)
    {
        parentPath = null;
        source = null;

        if (!File.Exists(baseDisk))
        {
            return false;
        }

        try
        {
            NtfsCompression.TryUncompress(baseDisk);
            _diskFactory.CreateDifferencingVhdx(disk, baseDisk);
            parentPath = baseDisk;
            source = sourceDirectory;
            return true;
        }
        catch (SandboxBackendException)
        {
            TryDeletePartialCopy(disk);
        }
        catch (IOException)
        {
            TryDeletePartialCopy(disk);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeletePartialCopy(disk);
        }

        return false;
    }

    /// <summary>
    /// Walks the parent locator chain from <paramref name="disk"/> up to the
    /// base (the disk whose parent is absent). Returns <see langword="null"/>
    /// when a parent is missing or the chain loops/exceeds
    /// <see cref="MaxChainDepth"/>.
    /// </summary>
    private string? ResolveChainRoot(string disk)
    {
        var current = disk;

        for (var depth = 0; depth < MaxChainDepth; depth++)
        {
            string? parent;
            try
            {
                parent = _diskFactory.TryGetParentPath(current);
            }
            catch
            {
                return null;
            }

            if (parent is null)
            {
                return current;
            }

            if (!File.Exists(parent))
            {
                return null;
            }

            current = parent;
        }

        return null;
    }

    /// <summary>
    /// Best-effort copy of a <b>standalone</b> (non-differencing)
    /// <c>sandbox.vhdx</c> from <paramref name="source"/>. Returns
    /// <see langword="false"/> (so the caller can try the next strategy) when the
    /// source is absent, is a differencing child (handled by the chain path), has
    /// been used (a donor above the blank-size threshold), is locked by a running
    /// sandbox, or any IO error occurs.
    /// </summary>
    private bool TryCopyStandaloneDisk(string? source, string disk, bool requireBlank)
    {
        if (source is null)
        {
            return false;
        }

        var sourceDisk = Path.Combine(source, SandboxStorageTemplateLocator.DiskFileName);

        try
        {
            if (!File.Exists(sourceDisk))
            {
                return false;
            }

            if (_diskFactory.TryGetParentPath(sourceDisk) is not null)
            {
                return false;
            }

            if (requireBlank && new FileInfo(sourceDisk).Length > BlankDonorSizeThresholdBytes)
            {
                return false;
            }

            File.Copy(sourceDisk, disk, overwrite: false);
            NtfsCompression.TryUncompress(disk);
            return true;
        }
        catch (IOException)
        {
            TryDeletePartialCopy(disk);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            TryDeletePartialCopy(disk);
            return false;
        }
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="directory"/>.</summary>
    private static bool IsWithinDirectory(string path, string directory)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var fullDirectory = Path.GetFullPath(directory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeletePartialCopy(string disk)
    {
        try
        {
            if (File.Exists(disk))
            {
                File.Delete(disk);
            }
        }
        catch
        {
            // best effort — a later strategy may still succeed
        }
    }

    private void CreateBlankDisk(string disk, uint diskSizeMb)
    {
        // 0 => the factory's platform default (80 GB, matching Windows Sandbox).
        var maximumSizeBytes = diskSizeMb == 0 ? 0UL : (ulong)diskSizeMb * 1024 * 1024;

        try
        {
            _diskFactory.CreateDynamicVhdx(disk, maximumSizeBytes);
            NtfsCompression.TryUncompress(disk);
        }
        catch (SandboxBackendException ex)
        {
            throw new SandboxBackendException(
                $"No user-data disk (sandbox.vhdx) template was found and a blank dynamic VHDX " +
                $"could not be created at '{disk}': {ex.Message}",
                ex.HResult,
                ex);
        }
    }

    private void EnsureGuestState(string guestState, string? source)
    {
        if (File.Exists(guestState))
        {
            NtfsCompression.TryUncompress(guestState);
            return;
        }

        if (source is not null)
        {
            var donorGuestState = Path.Combine(source, SandboxStorageTemplateLocator.GuestStateFileName);
            try
            {
                if (File.Exists(donorGuestState))
                {
                    File.Copy(donorGuestState, guestState);
                    NtfsCompression.TryUncompress(guestState);
                    return;
                }
            }
            catch (IOException)
            {
                // A locked/foreign guest state must not block provisioning;
                // fall through to a fresh empty guest-state file.
            }
            catch (UnauthorizedAccessException)
            {
                // ditto
            }
        }

        try
        {
            _hcs.CreateEmptyGuestStateFile(guestState);
            NtfsCompression.TryUncompress(guestState);
        }
        catch (SandboxDomainException ex)
        {
            throw new SandboxBackendException(
                $"Failed to create the guest-state file '{guestState}' with " +
                $"HcsCreateEmptyGuestStateFile: {ex.Message}",
                ex.HResult,
                ex);
        }
    }
}
