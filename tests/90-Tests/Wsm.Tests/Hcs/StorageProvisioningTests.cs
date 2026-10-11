using System.Text;
using Wsm.Hcs.Interop;
using Wsm.Hcs.Layout;
using Wsm.Hcs.Storage;
using Wsm.Hcs.Storage.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Tests for the user-data storage provisioning logic. HCS and the Win32
/// <c>VirtDisk</c> API both need an elevated process, so they are replaced with
/// fakes; the template discovery, path layout and provisioning strategy are all
/// exercised without touching the host.
/// </summary>
public sealed class StorageProvisioningTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wsm-storage-tests", Guid.NewGuid().ToString("N"));

    public StorageProvisioningTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // best effort
        }
    }

    private string ContainerStoragesFolder => Path.Combine(_root, "ContainerStorages");

    private string NewDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Touch(string directory, string fileName, string content = "x")
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), content);
    }

    // ---- template discovery -------------------------------------------------

    [Fact]
    public void Locator_finds_donor_storage_that_holds_a_disk()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, SandboxStorageTemplateLocator.DiskFileName);

        var locator = new SandboxStorageTemplateLocator(ContainerStoragesFolder);

        Assert.Equal(donor, locator.FindDonorStorage());
    }

    [Fact]
    public void Locator_prefers_the_smallest_donor_disk()
    {
        var big = NewDirectory(Path.Combine("ContainerStorages", "big"));
        Touch(big, SandboxStorageTemplateLocator.DiskFileName, new string('x', 4096));
        var small = NewDirectory(Path.Combine("ContainerStorages", "small"));
        Touch(small, SandboxStorageTemplateLocator.DiskFileName, "x");

        var locator = new SandboxStorageTemplateLocator(ContainerStoragesFolder);

        Assert.Equal(small, locator.FindDonorStorage());
    }

    [Fact]
    public void Locator_returns_null_when_no_source_exists()
    {
        Directory.CreateDirectory(ContainerStoragesFolder);
        var locator = new SandboxStorageTemplateLocator(ContainerStoragesFolder);

        Assert.Null(locator.FindDonorStorage());
        Assert.Null(locator.FindTemplateStorage());
        Assert.Null(locator.FindSourceStorage());
    }

    [Fact]
    public void Locator_finds_template_disk_at_the_template_root()
    {
        var template = NewDirectory("templates");
        Touch(template, SandboxStorageTemplateLocator.DiskFileName);

        var locator = new SandboxStorageTemplateLocator(ContainerStoragesFolder, template);

        Assert.Null(locator.FindDonorStorage());
        Assert.Equal(template, locator.FindTemplateStorage());
        Assert.Equal(template, locator.FindSourceStorage());
    }

    // ---- provisioning -------------------------------------------------------

    [Fact]
    public void Provision_creates_blank_disk_and_guest_state_when_no_donor_exists()
    {
        Directory.CreateDirectory(ContainerStoragesFolder);
        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);
        var target = NewDirectory("target");

        var layout = provisioner.Provision(target);

        Assert.Equal(Path.Combine(target, "sandbox.vhdx"), layout.DiskPath);
        Assert.Equal(Path.Combine(target, "sandbox.vmgs"), layout.GuestStatePath);
        Assert.True(File.Exists(layout.DiskPath));
        Assert.True(File.Exists(layout.GuestStatePath));

        Assert.Equal(1, factory.Calls);
        Assert.Equal(0UL, factory.LastMaximumSizeBytes); // 0 => platform default
        Assert.Equal(1, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_honours_the_requested_disk_size()
    {
        Directory.CreateDirectory(ContainerStoragesFolder);
        var factory = new FakeVirtualDiskFactory();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        provisioner.Provision(NewDirectory("target"), diskSizeMb: 4096);

        Assert.Equal(4096UL * 1024 * 1024, factory.LastMaximumSizeBytes);
    }

    [Fact]
    public void Provision_copies_donor_disk_and_guest_state_and_never_creates_a_blank_disk()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "disk");
        Touch(donor, "sandbox.vmgs", "vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);
        var target = NewDirectory("target");

        var layout = provisioner.Provision(target);

        Assert.Equal("disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal("vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_reuses_existing_files_without_touching_the_host()
    {
        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-disk");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal("existing-vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_creates_guest_state_via_hcs_when_donor_has_only_a_disk()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "disk");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);

        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal(0, factory.Calls);
        Assert.Equal(1, hcs.GuestStateCalls);
        Assert.True(File.Exists(layout.GuestStatePath));
    }

    [Fact]
    public void Provision_prefers_donor_over_template()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "donor-disk");
        Touch(donor, "sandbox.vmgs", "donor-vmgs");

        var template = NewDirectory("templates");
        Touch(template, "sandbox.vhdx", "template-disk");

        var factory = new FakeVirtualDiskFactory();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal("donor-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal(0, factory.Calls);
    }

    // ---- donor robustness / fallback ---------------------------------------

    [Fact]
    public void Provision_falls_back_to_blank_disk_when_the_donor_disk_is_locked()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "disk");
        Touch(donor, "sandbox.vmgs", "vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);
        var target = NewDirectory("target");

        using (File.Open(Path.Combine(donor, "sandbox.vhdx"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var layout = provisioner.Provision(target);

            // The locked donor is skipped: a blank disk is created instead and
            // the donor's guest state is not paired with it.
            Assert.Equal("blank-vhdx", File.ReadAllText(layout.DiskPath));
            Assert.Equal("empty-vmgs", File.ReadAllText(layout.GuestStatePath));
        }

        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_falls_back_to_template_when_the_donor_disk_is_locked()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "donor-disk");

        var template = NewDirectory("templates");
        Touch(template, "sandbox.vhdx", "template-disk");

        var factory = new FakeVirtualDiskFactory();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        using var templateScope = new TemplateRootScope(template);
        using var donorLock = File.Open(
            Path.Combine(donor, "sandbox.vhdx"), FileMode.Open, FileAccess.Read, FileShare.None);

        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal("template-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_skips_a_used_donor_and_creates_a_blank_disk()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "used"));
        CreateSizedFile(
            Path.Combine(donor, "sandbox.vhdx"),
            SandboxStorageProvisioner.BlankDonorSizeThresholdBytes + 1);
        Touch(donor, "sandbox.vmgs", "vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);

        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal("blank-vhdx", File.ReadAllText(layout.DiskPath));
        Assert.Equal("empty-vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(1, factory.Calls);
        Assert.Equal(1, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_skips_a_used_donor_in_favour_of_the_template()
    {
        var donor = NewDirectory(Path.Combine("ContainerStorages", "used"));
        CreateSizedFile(
            Path.Combine(donor, "sandbox.vhdx"),
            SandboxStorageProvisioner.BlankDonorSizeThresholdBytes + 1);

        var template = NewDirectory("templates");
        Touch(template, "sandbox.vhdx", "template-disk");

        var factory = new FakeVirtualDiskFactory();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        using var templateScope = new TemplateRootScope(template);
        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal("template-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_does_not_pair_donor_guest_state_with_a_preexisting_target_disk()
    {
        // Target already has a disk (so the disk step is skipped) but no vmgs;
        // a donor vmgs must not be copied onto it.
        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-disk");

        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "donor-disk");
        Touch(donor, "sandbox.vmgs", "donor-vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal("empty-vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(1, hcs.GuestStateCalls);
        Assert.Equal(0, factory.Calls);
    }

    // ---- uncompressed differencing chain ------------------------------------

    [Fact]
    public void Locator_enumerates_all_donor_storages_smallest_first()
    {
        var big = NewDirectory(Path.Combine("ContainerStorages", "big"));
        Touch(big, SandboxStorageTemplateLocator.DiskFileName, new string('x', 4096));
        var small = NewDirectory(Path.Combine("ContainerStorages", "small"));
        Touch(small, SandboxStorageTemplateLocator.DiskFileName, "x");

        var locator = new SandboxStorageTemplateLocator(ContainerStoragesFolder);

        Assert.Equal(new[] { small, big }, locator.EnumerateDonorStorages());
    }

    [Fact]
    public void Provision_references_the_platform_base_disk_in_place_without_copying_it()
    {
        var baseDir = NewDirectory(Path.Combine("ContainerStorages", "base"));
        Touch(baseDir, "sandbox.vhdx", "base-disk");

        var childDir = NewDirectory(Path.Combine("ContainerStorages", "child"));
        Touch(childDir, "sandbox.vhdx", "child-disk");
        Touch(childDir, "sandbox.vmgs", "child-vmgs");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(childDir, "sandbox.vhdx")] =
            Path.Combine(baseDir, "sandbox.vhdx");

        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);
        var target = NewDirectory("target");

        var layout = provisioner.Provision(target);

        var platformBase = Path.Combine(baseDir, "sandbox.vhdx");

        // The child points straight at the platform's base OS disk; the base is
        // never copied into the sandbox folder (it is the platform's shared image
        // that Windows Update maintains).
        Assert.Equal(1, factory.DifferencingCalls);
        Assert.Equal(platformBase, factory.LastParentPath);
        Assert.Equal(platformBase, layout.BaseDiskPath);
        Assert.False(File.Exists(Path.Combine(target, SandboxStorageProvisioner.BaseDiskFileName)));
        Assert.Equal("diff-vhdx", File.ReadAllText(layout.DiskPath));
        Assert.Equal("child-vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_reuses_an_existing_chain_whose_parent_is_in_the_storage_directory()
    {
        var target = NewDirectory("target");
        var baseCopy = Path.Combine(target, SandboxStorageProvisioner.BaseDiskFileName);
        Touch(target, SandboxStorageProvisioner.BaseDiskFileName, "existing-base");
        Touch(target, "sandbox.vhdx", "existing-child");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] = baseCopy;

        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-child", File.ReadAllText(layout.DiskPath));
        Assert.Equal("existing-base", File.ReadAllText(baseCopy));
        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_reuses_an_existing_child_over_the_external_platform_base()
    {
        var baseDir = NewDirectory(Path.Combine("ContainerStorages", "base"));
        Touch(baseDir, "sandbox.vhdx", "base-disk");

        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-child");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var factory = new FakeVirtualDiskFactory();
        // The normal case: the child's parent is the platform's shared base OS
        // disk, which lives outside the sandbox folder and is referenced, not
        // copied. The chain is intact, so the disk is reused as-is.
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] =
            Path.Combine(baseDir, "sandbox.vhdx");

        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient());

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-child", File.ReadAllText(layout.DiskPath));
        Assert.Equal(Path.Combine(baseDir, "sandbox.vhdx"), layout.BaseDiskPath);
        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_falls_back_to_blank_when_the_chain_parent_is_missing()
    {
        var childDir = NewDirectory(Path.Combine("ContainerStorages", "child"));
        Touch(childDir, "sandbox.vhdx", "child-disk");
        Touch(childDir, "sandbox.vmgs", "child-vmgs");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(childDir, "sandbox.vhdx")] =
            Path.Combine(ContainerStoragesFolder, "gone", "sandbox.vhdx");

        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs);

        var layout = provisioner.Provision(NewDirectory("target"));

        Assert.Equal("blank-vhdx", File.ReadAllText(layout.DiskPath));
        Assert.Equal(1, factory.Calls);
        Assert.Equal(0, factory.DifferencingCalls);
    }

    // ---- VHDX parent locator parsing ----------------------------------------

    [Fact]
    public void Vhdx_parent_locator_reads_the_absolute_parent_path()
    {
        var path = Path.Combine(_root, "child.vhdx");
        File.WriteAllBytes(path, BuildVhdxWithParent(@"C:\base\sandbox.vhdx"));

        Assert.Equal(@"C:\base\sandbox.vhdx", VhdxParentLocator.TryGetAbsoluteParentPath(path));
    }

    [Fact]
    public void Vhdx_parent_locator_returns_null_for_a_base_disk()
    {
        var path = Path.Combine(_root, "base.vhdx");
        File.WriteAllBytes(path, BuildVhdxWithParent(null));

        Assert.Null(VhdxParentLocator.TryGetAbsoluteParentPath(path));
    }

    [Fact]
    public void Vhdx_parent_locator_returns_null_for_a_non_vhdx_file()
    {
        var path = Path.Combine(_root, "not-a-vhdx.vhdx");
        File.WriteAllText(path, "this is definitely not a VHDX file");

        Assert.Null(VhdxParentLocator.TryGetAbsoluteParentPath(path));
    }

    private static readonly Guid MetadataRegionId = new("8B7CA206-4790-4B9A-B8FE-575F050F886E");
    private static readonly Guid ParentLocatorItemId = new("A8D35F2D-B30B-454D-ABF7-D3D84834AB0C");
    private static readonly Guid ParentLocatorTypeId = new("B04AEFB7-D19E-4A81-B789-25B8E9445913");

    /// <summary>
    /// Builds a minimal but structurally valid VHDX byte image (file identifier,
    /// region table, metadata region, optional parent locator) so the parser can
    /// be exercised without a real virtual disk.
    /// </summary>
    private static byte[] BuildVhdxWithParent(string? absoluteParentPath)
    {
        const long metadataOffset = 0x100000;
        const uint metadataLength = 0x100000;
        const uint itemOffset = 0x1000;
        const uint itemLength = 0x200;

        var buffer = new byte[metadataOffset + metadataLength];

        // File identifier ("vhdxfile", ASCII) at offset 0.
        Encoding.ASCII.GetBytes("vhdxfile").CopyTo(buffer, 0);

        // Region table ("regi") at 0x30000: header (16) + one 32-byte entry.
        var regionTable = 0x30000;
        Encoding.ASCII.GetBytes("regi").CopyTo(buffer, regionTable);
        BitConverter.GetBytes(1).CopyTo(buffer, regionTable + 8); // entry count
        var entry = regionTable + 16;
        MetadataRegionId.ToByteArray().CopyTo(buffer, entry);
        BitConverter.GetBytes(metadataOffset).CopyTo(buffer, entry + 16); // FileOffset
        BitConverter.GetBytes(metadataLength).CopyTo(buffer, entry + 24); // Length

        // Metadata region header ("metadata") at metadataOffset.
        var metadata = (int)metadataOffset;
        Encoding.ASCII.GetBytes("metadata").CopyTo(buffer, metadata);
        BitConverter.GetBytes((ushort)1).CopyTo(buffer, metadata + 10); // entry count

        var metadataEntry = metadata + 32;
        if (absoluteParentPath is null)
        {
            // A non-parent item so the parser finds no parent locator.
            Guid.Empty.ToByteArray().CopyTo(buffer, metadataEntry);
            return buffer;
        }

        ParentLocatorItemId.ToByteArray().CopyTo(buffer, metadataEntry);
        BitConverter.GetBytes(itemOffset).CopyTo(buffer, metadataEntry + 16);
        BitConverter.GetBytes(itemLength).CopyTo(buffer, metadataEntry + 20);

        // Parent locator data.
        var data = metadata + (int)itemOffset;
        ParentLocatorTypeId.ToByteArray().CopyTo(buffer, data);
        BitConverter.GetBytes((ushort)2).CopyTo(buffer, data + 18); // key/value count

        var key0 = Encoding.Unicode.GetBytes("absolute_win32_path\0");
        var value0 = Encoding.Unicode.GetBytes(absoluteParentPath + "\0");
        var key1 = Encoding.Unicode.GetBytes("parent_linkage\0");
        var value1 = Encoding.Unicode.GetBytes("{50BC4941-B71D-42F7-9BCF-62DD4874B212}\0");

        const int stringsStart = 20 + (2 * 12);
        var key0Offset = stringsStart;
        var value0Offset = key0Offset + key0.Length;
        var key1Offset = value0Offset + value0.Length;
        var value1Offset = key1Offset + key1.Length;

        WriteLocatorEntry(data + 20, key0Offset, value0Offset, key0.Length, value0.Length);
        WriteLocatorEntry(data + 32, key1Offset, value1Offset, key1.Length, value1.Length);
        key0.CopyTo(buffer, data + key0Offset);
        value0.CopyTo(buffer, data + value0Offset);
        key1.CopyTo(buffer, data + key1Offset);
        value1.CopyTo(buffer, data + value1Offset);

        return buffer;

        void WriteLocatorEntry(int offset, int keyOffset, int valueOffset, int keyLength, int valueLength)
        {
            BitConverter.GetBytes(keyOffset).CopyTo(buffer, offset);
            BitConverter.GetBytes(valueOffset).CopyTo(buffer, offset + 4);
            BitConverter.GetBytes((ushort)keyLength).CopyTo(buffer, offset + 8);
            BitConverter.GetBytes((ushort)valueLength).CopyTo(buffer, offset + 10);
        }
    }

    private static void CreateSizedFile(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        stream.SetLength(length);
    }

    /// <summary>
    /// Sets <c>WSM_HCS_STORAGE_TEMPLATE</c> for the duration of a test and
    /// restores the previous value on dispose. Tests in this class run
    /// sequentially and no other test reads this variable.
    /// </summary>
    private sealed class TemplateRootScope : IDisposable
    {
        private readonly string? _original;

        public TemplateRootScope(string root)
        {
            _original = Environment.GetEnvironmentVariable(
                SandboxStorageProvisioner.TemplateRootEnvironmentVariable);
            Environment.SetEnvironmentVariable(
                SandboxStorageProvisioner.TemplateRootEnvironmentVariable, root);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(
            SandboxStorageProvisioner.TemplateRootEnvironmentVariable, _original);
    }

    // ---- fakes --------------------------------------------------------------

    private sealed class FakeLayout : IHcsHostLayout
    {
        public FakeLayout(string containerStoragesFolder) => PlatformContainerStoragesFolder = containerStoragesFolder;

        public string PlatformContainersFolder => Path.GetDirectoryName(PlatformContainerStoragesFolder) ?? PlatformContainerStoragesFolder;

        public string LayersFolder => Path.Combine(PlatformContainersFolder, "Layers");

        public string BaseImagesFolder => Path.Combine(PlatformContainersFolder, "BaseImages");

        public string PlatformContainerStoragesFolder { get; }

        public string SnapshotsFolder => Path.Combine(PlatformContainersFolder, "Snapshots");

        public string SandboxDataFolder => PlatformContainerStoragesFolder;

        public string GetStorageDirectory(string id) => Path.Combine(SandboxDataFolder, id);

        public IReadOnlyList<HcsLayerInfo> ListLayers() => Array.Empty<HcsLayerInfo>();

        public string? ResolveBaseLayerPath() => null;
    }

    private sealed class FakeVirtualDiskFactory : IVirtualDiskFactory
    {
        public int Calls { get; private set; }

        public ulong LastMaximumSizeBytes { get; private set; }

        public int DifferencingCalls { get; private set; }

        public string? LastParentPath { get; private set; }

        /// <summary>Maps a disk path to its configured parent path (null = base).</summary>
        public Dictionary<string, string> Parents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void CreateDynamicVhdx(string path, ulong maximumSizeBytes)
        {
            Calls++;
            LastMaximumSizeBytes = maximumSizeBytes;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "blank-vhdx");
        }

        public void CreateDifferencingVhdx(string path, string parentPath)
        {
            DifferencingCalls++;
            LastParentPath = parentPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "diff-vhdx");
        }

        public string? TryGetParentPath(string vhdxPath) =>
            Parents.TryGetValue(vhdxPath, out var parent) ? parent : null;
    }

    private sealed class FakeHcsClient : IHcsClient
    {
        public int GuestStateCalls { get; private set; }

        public void CreateEmptyGuestStateFile(string path)
        {
            GuestStateCalls++;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "empty-vmgs");
        }

        public IReadOnlyList<HcsComputeSystemInfo> List(string? query = null) => Array.Empty<HcsComputeSystemInfo>();

        public HcsComputeSystemInfo? Get(string id) => null;

        public IDisposable Create(string id, string configuration) => throw new NotSupportedException();

        public void Start(string id) => throw new NotSupportedException();

        public void Shutdown(string id) => throw new NotSupportedException();

        public void Terminate(string id) => throw new NotSupportedException();

        public string? Save(string id, string options) => throw new NotSupportedException();

        public void CreateEmptyRuntimeStateFile(string path) => throw new NotSupportedException();

        public void GrantVmAccess(string id, string path) => throw new NotSupportedException();

        public HcsProcessResult RunProcess(string id, string commandLine, string? user = null, uint timeoutMs = 120_000) => throw new NotSupportedException();
    }
}
