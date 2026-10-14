using Microsoft.Extensions.Logging;
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
///   first-boot/specialization) and clones every sandbox from it. The base is
///   located directly — the current base layer's registry <c>StorageId</c> names
///   the layer's storage disk, and the prepared base is the differencing child
///   whose direct parent is that disk — with the historical donor-witness
///   resolution (a sandbox whose immediate parent is itself a differencing
///   child) kept as a backup. A differencing child <c>sandbox.vhdx</c> is created
///   over it. The base is never copied — it is the shared, Windows
///   Update-maintained image, so a per-sandbox copy would freeze a stale OS and
///   waste gigabytes;</item>
///   <item>copy an <b>explicitly configured template</b> disk
///   (<c>WSM_HCS_STORAGE_TEMPLATE</c>, an opt-in development/test hook);</item>
///   <item>otherwise <b>fail fast</b> with a <see cref="SandboxBackendException"/>
///   that names the remediation (start the official Windows Sandbox once so the
///   platform materialises its prepared base image).</item>
/// </list>
/// <para>
/// Why the chain matters: HCS's synthetic storage refuses any disk in the
/// parent chain that carries the NTFS <c>Compressed</c> attribute
/// (<c>Start</c> fails with <c>0xC03A001A</c>,
/// <c>STATUS_VIRTUAL_DISK_LIMITATION</c>). The host's
/// <c>...\Windows\Containers</c> tree is compressed, so a copied differencing
/// child still points at a compressed parent. Referencing the platform's
/// prepared base <b>uncompressed in place</b> and building our own child breaks
/// that dependency.
/// </para>
/// <para>
/// There is deliberately <b>no silent blank-disk fallback</b>: a blank dynamic
/// VHDX boots an unspecialized guest whose session comes up but whose
/// <c>explorer.exe</c> hangs and whose Start menu never opens — a sandbox that
/// looks started but is unusable. Provisioning fails loudly instead.
/// </para>
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

    private readonly IHcsHostLayout _layout;
    private readonly IVirtualDiskFactory _diskFactory;
    private readonly IHcsClient _hcs;
    private readonly ILogger<SandboxStorageProvisioner> _logger;

    public SandboxStorageProvisioner(
        IHcsHostLayout layout,
        IVirtualDiskFactory diskFactory,
        IHcsClient hcs,
        ILogger<SandboxStorageProvisioner> logger)
    {
        _layout = layout;
        _diskFactory = diskFactory;
        _hcs = hcs;
        _logger = logger;
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
            else if (TryCopyTemplateDisk(locator.FindTemplateStorage(), disk))
            {
                diskSource = locator.FindTemplateStorage();
            }
            else
            {
                throw new SandboxBackendException(
                    "The platform's prepared base image was not found under " +
                    $"'{_layout.PlatformContainerStoragesFolder}': the current base layer has no " +
                    "materialised base image to clone. Start the official Windows Sandbox once " +
                    "(or wait for it to finish its first-boot preparation) so the platform " +
                    "materialises the prepared base image, then retry. A blank disk is not " +
                    "created silently anymore because it boots an unspecialized guest whose " +
                    "explorer.exe hangs and whose Start menu never opens.",
                    0);
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
    /// <para>
    /// A <b>parentless</b> disk is the legacy blank-fallback shape. It boots an
    /// unspecialized guest too, but it may hold data the user wants, so it is
    /// kept (never auto-deleted): when the platform already has a prepared base
    /// image, a loud warning tells the user to delete the directory and
    /// re-create the container.
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
                if (ResolvePreparedBaseFromLayerStorage() is not null)
                {
                    _logger.LogWarning(
                        "The user-data disk '{Disk}' has no parent chain: it is a blank fallback disk " +
                        "left by an older version and boots an unspecialized guest (no taskbar, " +
                        "explorer.exe stops responding). It is kept because it may hold data. To " +
                        "repair the sandbox: stop it, delete the directory '{Directory}', and " +
                        "re-create the container so a differencing child of the platform's prepared " +
                        "base image is provisioned.",
                        disk, storageDirectory);
                }

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
    /// The prepared base is located directly from the current base layer's
    /// registry <c>StorageId</c> (no witness sandbox required); the historical
    /// donor-witness resolution is kept as a backup for hosts whose registry
    /// hive is not readable. When neither resolves a base image the caller falls
    /// through to the explicitly-configured template, then fails fast.
    /// </para>
    /// Returns <see langword="false"/> when no prepared base resolves to an
    /// existing base disk, so the caller can fall through to the template
    /// strategy.
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

        // Preferred: direct resolution through the base layer's StorageId. This
        // needs no witness sandbox, so the very first sandbox on a fresh machine
        // still chains to the prepared base image.
        var prepared = ResolvePreparedBaseFromLayerStorage();
        if (prepared is not null &&
            !IsWithinDirectory(prepared, storageDirectory) &&
            TryChain(disk, prepared, Path.GetDirectoryName(prepared), out parentPath, out source))
        {
            return true;
        }

        // Backup: a witness sandbox — a donor whose immediate parent is itself a
        // differencing child (that parent is the prepared base image).
        foreach (var donor in locator.EnumerateDonorStorages())
        {
            var donorDisk = Path.Combine(donor, SandboxStorageTemplateLocator.DiskFileName);
            if (!File.Exists(donorDisk) || IsWithinDirectory(donorDisk, storageDirectory))
            {
                continue;
            }

            var witnessed = ResolvePreparedBase(donorDisk);
            if (witnessed is null || string.Equals(witnessed, prepared, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryChain(disk, witnessed, donor, out parentPath, out source))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Locates the platform's prepared base image directly, without needing a
    /// witness sandbox: the current base layer's registry <c>StorageId</c> names
    /// the layer's storage disk
    /// (<c>ContainerStorages\&lt;StorageId&gt;\sandbox.vhdx</c>), and the prepared
    /// base image is the differencing child whose direct parent is that disk.
    /// Only GUID-named storage directories are considered (quarantined or
    /// otherwise renamed folders are ignored); when several children qualify the
    /// most recently written one wins. Returns <see langword="null"/> when no
    /// prepared base exists yet.
    /// </summary>
    private string? ResolvePreparedBaseFromLayerStorage()
    {
        var layer = _layout.ListLayers()
            .FirstOrDefault(l => l.StorageId is not null && l.IsUsableBaseImage);
        if (layer?.StorageId is not Guid storageId)
        {
            return null;
        }

        var layerStorageDisk = Path.Combine(
            _layout.PlatformContainerStoragesFolder,
            storageId.ToString(),
            SandboxStorageTemplateLocator.DiskFileName);
        if (!File.Exists(layerStorageDisk))
        {
            return null;
        }

        string? prepared = null;
        var preparedWrite = DateTime.MinValue;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(
                _layout.PlatformContainerStoragesFolder, "*", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return null;
        }

        foreach (var directory in directories)
        {
            // Quarantined/renamed folders ("<guid>.quarantine" and friends) are
            // not platform storages; only GUID-named directories qualify.
            if (!Guid.TryParse(Path.GetFileName(directory), out _))
            {
                continue;
            }

            var candidate = Path.Combine(directory, SandboxStorageTemplateLocator.DiskFileName);
            if (!File.Exists(candidate))
            {
                continue;
            }

            var parent = TryGetParent(candidate);
            if (parent is null || !PathEquals(parent, layerStorageDisk))
            {
                continue;
            }

            var written = File.GetLastWriteTimeUtc(candidate);
            if (written >= preparedWrite)
            {
                prepared = candidate;
                preparedWrite = written;
            }
        }

        return prepared;
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
    /// Failures are logged — a silently skipped base used to drop provisioning
    /// into the blank-disk fallback with no trace.
    /// </summary>
    private bool TryChain(
        string disk,
        string baseDisk,
        string? sourceDirectory,
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
        catch (SandboxBackendException ex)
        {
            _logger.LogWarning(ex, "Failed to chain '{Disk}' to the base image '{BaseDisk}'.", disk, baseDisk);
            TryDeletePartialCopy(disk);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to chain '{Disk}' to the base image '{BaseDisk}'.", disk, baseDisk);
            TryDeletePartialCopy(disk);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to chain '{Disk}' to the base image '{BaseDisk}'.", disk, baseDisk);
            TryDeletePartialCopy(disk);
        }

        return false;
    }

    /// <summary>
    /// Best-effort copy of the <b>explicitly configured</b> template
    /// <c>sandbox.vhdx</c> (<c>WSM_HCS_STORAGE_TEMPLATE</c>). Returns
    /// <see langword="false"/> (so the caller fails fast) when the template is
    /// absent, is a differencing child, is locked, or any IO error occurs.
    /// </summary>
    private bool TryCopyTemplateDisk(string? source, string disk)
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

            File.Copy(sourceDisk, disk, overwrite: false);
            NtfsCompression.TryUncompress(disk);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to copy the configured template disk '{SourceDisk}'.", sourceDisk);
            TryDeletePartialCopy(disk);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Failed to copy the configured template disk '{SourceDisk}'.", sourceDisk);
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

    /// <summary>Path equality that tolerates differing casing and separators.</summary>
    private static bool PathEquals(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(first),
                Path.GetFullPath(second),
                StringComparison.OrdinalIgnoreCase);
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
