using System.Text;
using Microsoft.Extensions.Logging;
using Wsm.Domain.Exceptions;
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

    /// <summary>Captures warning+ log entries emitted by the provisioner.</summary>
    private readonly ListLogger<SandboxStorageProvisioner> _logger = new();

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
    public void Provision_fails_fast_with_guidance_when_no_prepared_base_exists()
    {
        // No witness sandbox, no layer with a usable StorageId, no configured
        // template: provisioning must fail loudly instead of silently creating
        // a blank disk that boots an unspecialized guest.
        Directory.CreateDirectory(ContainerStoragesFolder);
        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);
        var target = NewDirectory("target");

        var ex = Assert.Throws<SandboxBackendException>(() => provisioner.Provision(target));

        Assert.Contains("prepared base image", ex.Message);
        Assert.Contains("Windows Sandbox", ex.Message);
        Assert.False(File.Exists(Path.Combine(target, "sandbox.vhdx")));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, factory.DifferencingCalls);
    }

    [Fact]
    public void Provision_does_not_fall_back_to_copying_a_standalone_donor()
    {
        // A parentless donor under ContainerStorages is either a layer storage
        // or another sandbox's blank scratch: chaining/copying it boots an
        // unspecialized guest. It is not a provisioning source anymore.
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "disk");
        Touch(donor, "sandbox.vmgs", "vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);

        Assert.Throws<SandboxBackendException>(() => provisioner.Provision(NewDirectory("target")));

        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, factory.DifferencingCalls);
    }

    [Fact]
    public void Provision_reuses_existing_files_without_touching_the_host()
    {
        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-disk");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var factory = new FakeVirtualDiskFactory();
        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-disk", File.ReadAllText(layout.DiskPath));
        Assert.Equal("existing-vmgs", File.ReadAllText(layout.GuestStatePath));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, hcs.GuestStateCalls);
    }

    [Fact]
    public void Provision_uses_the_configured_template_when_no_prepared_base_exists()
    {
        // The explicitly configured template (WSM_HCS_STORAGE_TEMPLATE) remains
        // the opt-in escape hatch when the platform has no prepared base image.
        var donor = NewDirectory(Path.Combine("ContainerStorages", "donor"));
        Touch(donor, "sandbox.vhdx", "donor-disk");

        var template = NewDirectory("templates");
        Touch(template, "sandbox.vhdx", "template-disk");

        var factory = new FakeVirtualDiskFactory();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

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
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);

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
    public void Provision_copies_the_guest_state_from_the_witnessed_base_directory()
    {
        // Platform layout: layer storage (no parent) -> prepared base image
        // (a differencing child) -> witness sandbox (a differencing child). The
        // new sandbox is chained to the prepared base, referenced in place; the
        // guest state is copied from the witness's directory (same source rule).
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", "layer"));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var baseDir = NewDirectory(Path.Combine("ContainerStorages", "base"));
        Touch(baseDir, "sandbox.vhdx", "base-disk");

        var childDir = NewDirectory(Path.Combine("ContainerStorages", "child"));
        Touch(childDir, "sandbox.vhdx", "child-disk");
        Touch(childDir, "sandbox.vmgs", "child-vmgs");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(baseDir, "sandbox.vhdx")] =
            Path.Combine(layerDir, "sandbox.vhdx");
        factory.Parents[Path.Combine(childDir, "sandbox.vhdx")] =
            Path.Combine(baseDir, "sandbox.vhdx");

        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);
        var target = NewDirectory("target");

        var layout = provisioner.Provision(target);

        var platformBase = Path.Combine(baseDir, "sandbox.vhdx");

        // The child points straight at the platform's prepared base disk; the
        // base is never copied into the sandbox folder (it is the platform's
        // shared image that Windows Update maintains).
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
    public void Provision_clones_the_prepared_base_image_not_the_layer_storage()
    {
        // Platform layout: layer storage (no parent) -> prepared base image
        // (a differencing child) -> sandbox (a differencing child).
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", "layer"));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var baseImageDir = NewDirectory(Path.Combine("ContainerStorages", "base-image"));
        Touch(baseImageDir, "sandbox.vhdx", "base-image");

        var sandboxDir = NewDirectory(Path.Combine("ContainerStorages", "sandbox"));
        Touch(sandboxDir, "sandbox.vhdx", "sandbox");

        var factory = new FakeVirtualDiskFactory();
        var layer = Path.Combine(layerDir, "sandbox.vhdx");
        var baseImage = Path.Combine(baseImageDir, "sandbox.vhdx");
        var platformSandbox = Path.Combine(sandboxDir, "sandbox.vhdx");
        factory.Parents[baseImage] = layer;
        factory.Parents[platformSandbox] = baseImage;

        var provisioner = new SandboxStorageProvisioner(
            new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

        var layout = provisioner.Provision(NewDirectory("target"));

        // The new sandbox is cloned from the prepared base image, not the raw
        // layer storage: the layer storage boots an unspecialized guest whose
        // explorer.exe hangs and whose Start menu never opens.
        Assert.Equal(1, factory.DifferencingCalls);
        Assert.Equal(baseImage, factory.LastParentPath);
        Assert.Equal(baseImage, layout.BaseDiskPath);
    }

    [Fact]
    public void Provision_rebuilds_a_legacy_chain_over_the_layer_storage()
    {
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", "layer"));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var baseImageDir = NewDirectory(Path.Combine("ContainerStorages", "base-image"));
        Touch(baseImageDir, "sandbox.vhdx", "base-image");

        var sandboxDir = NewDirectory(Path.Combine("ContainerStorages", "sandbox"));
        Touch(sandboxDir, "sandbox.vhdx", "sandbox");

        var factory = new FakeVirtualDiskFactory();
        var layer = Path.Combine(layerDir, "sandbox.vhdx");
        var baseImage = Path.Combine(baseImageDir, "sandbox.vhdx");
        var platformSandbox = Path.Combine(sandboxDir, "sandbox.vhdx");
        factory.Parents[baseImage] = layer;
        factory.Parents[platformSandbox] = baseImage;

        // The existing target chain points at the raw layer storage (the shape
        // produced before the prepared base image was used).
        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "stale-child");
        Touch(target, "sandbox.vmgs", "existing-vmgs");
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] = layer;

        var provisioner = new SandboxStorageProvisioner(
            new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

        var layout = provisioner.Provision(target);

        // The stale chain is rebuilt over the prepared base image.
        Assert.Equal(1, factory.DifferencingCalls);
        Assert.Equal(baseImage, factory.LastParentPath);
        Assert.Equal(baseImage, layout.BaseDiskPath);
        Assert.Equal("diff-vhdx", File.ReadAllText(layout.DiskPath));
    }

    [Fact]
    public void Provision_keeps_a_chain_over_the_prepared_base_image()
    {
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", "layer"));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var baseImageDir = NewDirectory(Path.Combine("ContainerStorages", "base-image"));
        Touch(baseImageDir, "sandbox.vhdx", "base-image");

        var factory = new FakeVirtualDiskFactory();
        var layer = Path.Combine(layerDir, "sandbox.vhdx");
        var baseImage = Path.Combine(baseImageDir, "sandbox.vhdx");
        factory.Parents[baseImage] = layer;

        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-child");
        Touch(target, "sandbox.vmgs", "existing-vmgs");
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] = baseImage;

        var provisioner = new SandboxStorageProvisioner(
            new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

        var layout = provisioner.Provision(target);

        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Equal("existing-child", File.ReadAllText(layout.DiskPath));
        Assert.Equal(baseImage, layout.BaseDiskPath);
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

        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-child", File.ReadAllText(layout.DiskPath));
        Assert.Equal("existing-base", File.ReadAllText(baseCopy));
        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_reuses_an_existing_child_over_the_external_platform_base()
    {
        // Platform layout: layer storage (no parent) -> prepared base image.
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", "layer"));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var baseImageDir = NewDirectory(Path.Combine("ContainerStorages", "base-image"));
        Touch(baseImageDir, "sandbox.vhdx", "base-image");

        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "existing-child");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var factory = new FakeVirtualDiskFactory();
        var layer = Path.Combine(layerDir, "sandbox.vhdx");
        var baseImage = Path.Combine(baseImageDir, "sandbox.vhdx");
        factory.Parents[baseImage] = layer;
        // The normal case: the child's parent is the platform's prepared base
        // image, which lives outside the sandbox folder and is referenced, not
        // copied. The chain is intact, so the disk is reused as-is.
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] = baseImage;

        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, new FakeHcsClient(), _logger);

        var layout = provisioner.Provision(target);

        Assert.Equal("existing-child", File.ReadAllText(layout.DiskPath));
        Assert.Equal(baseImage, layout.BaseDiskPath);
        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_throws_when_the_existing_chain_is_broken_and_no_prepared_base_exists()
    {
        // The existing chain is broken (its parent is gone), so it must be
        // rebuilt — but with no prepared base image anywhere the rebuild fails
        // fast instead of silently provisioning a blank disk.
        var childDir = NewDirectory(Path.Combine("ContainerStorages", "child"));
        Touch(childDir, "sandbox.vhdx", "child-disk");
        Touch(childDir, "sandbox.vmgs", "child-vmgs");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(childDir, "sandbox.vhdx")] =
            Path.Combine(ContainerStoragesFolder, "gone", "sandbox.vhdx");

        var hcs = new FakeHcsClient();
        var provisioner = new SandboxStorageProvisioner(new FakeLayout(ContainerStoragesFolder), factory, hcs, _logger);
        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "stale-child");
        factory.Parents[Path.Combine(target, "sandbox.vhdx")] =
            Path.Combine(ContainerStoragesFolder, "gone", "sandbox.vhdx");

        Assert.Throws<SandboxBackendException>(() => provisioner.Provision(target));

        // The broken leftover was discarded and nothing new was written.
        Assert.False(File.Exists(Path.Combine(target, "sandbox.vhdx")));
        Assert.Equal(0, factory.Calls);
        Assert.Equal(0, factory.DifferencingCalls);
    }

    // ---- prepared base resolution via the layer's StorageId -----------------

    [Fact]
    public void Provision_locates_the_prepared_base_via_the_layer_storage_id_without_a_witness()
    {
        // No sandbox has ever run (no witness donor). The current base layer's
        // registry StorageId names the layer's storage disk directly, and the
        // prepared base is its differencing child.
        var storageId = Guid.NewGuid();
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", storageId.ToString()));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var preparedDir = NewDirectory(Path.Combine("ContainerStorages", Guid.NewGuid().ToString()));
        Touch(preparedDir, "sandbox.vhdx", "prepared-base");

        var layerDisk = Path.Combine(layerDir, "sandbox.vhdx");
        var preparedDisk = Path.Combine(preparedDir, "sandbox.vhdx");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[preparedDisk] = layerDisk;

        var layout = new FakeLayout(ContainerStoragesFolder);
        layout.Layers.Add(UsableLayer(storageId));

        var provisioner = new SandboxStorageProvisioner(layout, factory, new FakeHcsClient(), _logger);

        var result = provisioner.Provision(NewDirectory("target"));

        Assert.Equal(1, factory.DifferencingCalls);
        Assert.Equal(preparedDisk, factory.LastParentPath);
        Assert.Equal(preparedDisk, result.BaseDiskPath);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void Provision_ignores_renamed_storage_directories_when_locating_the_prepared_base()
    {
        // A quarantined storage directory ("<guid>.quarantine") still holds a
        // disk chained to the layer storage, but it is not a platform storage
        // anymore and must never be cloned — even when it is the only match.
        var storageId = Guid.NewGuid();
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", storageId.ToString()));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var quarantineDir = NewDirectory(Path.Combine("ContainerStorages", Guid.NewGuid() + ".quarantine"));
        Touch(quarantineDir, "sandbox.vhdx", "poisoned-base");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(quarantineDir, "sandbox.vhdx")] =
            Path.Combine(layerDir, "sandbox.vhdx");

        var layout = new FakeLayout(ContainerStoragesFolder);
        layout.Layers.Add(UsableLayer(storageId));

        var provisioner = new SandboxStorageProvisioner(layout, factory, new FakeHcsClient(), _logger);

        Assert.Throws<SandboxBackendException>(() => provisioner.Provision(NewDirectory("target")));
        Assert.Equal(0, factory.DifferencingCalls);
    }

    [Fact]
    public void Provision_warns_when_reusing_a_parentless_legacy_disk()
    {
        // The platform has a prepared base image, but the target still holds a
        // parentless blank-fallback disk written by an older version: it boots
        // an unspecialized guest. The disk is kept (never auto-deleted) and a
        // loud warning tells the user how to repair it.
        var storageId = Guid.NewGuid();
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", storageId.ToString()));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var preparedDir = NewDirectory(Path.Combine("ContainerStorages", Guid.NewGuid().ToString()));
        Touch(preparedDir, "sandbox.vhdx", "prepared-base");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[Path.Combine(preparedDir, "sandbox.vhdx")] =
            Path.Combine(layerDir, "sandbox.vhdx");

        var layout = new FakeLayout(ContainerStoragesFolder);
        layout.Layers.Add(UsableLayer(storageId));

        var target = NewDirectory("target");
        Touch(target, "sandbox.vhdx", "legacy-blank");
        Touch(target, "sandbox.vmgs", "existing-vmgs");

        var provisioner = new SandboxStorageProvisioner(layout, factory, new FakeHcsClient(), _logger);

        var result = provisioner.Provision(target);

        Assert.Equal("legacy-blank", File.ReadAllText(result.DiskPath));
        Assert.Equal(0, factory.DifferencingCalls);
        Assert.Contains(_logger.Warnings, warning => warning.Contains("blank fallback"));
    }

    [Fact]
    public void Provision_logs_the_chain_failure_and_fails_fast()
    {
        // A failing VirtDisk chain call must leave a trace: it used to be
        // swallowed, dropping provisioning into the silent blank-disk fallback.
        var storageId = Guid.NewGuid();
        var layerDir = NewDirectory(Path.Combine("ContainerStorages", storageId.ToString()));
        Touch(layerDir, "sandbox.vhdx", "layer-storage");

        var preparedDir = NewDirectory(Path.Combine("ContainerStorages", Guid.NewGuid().ToString()));
        Touch(preparedDir, "sandbox.vhdx", "prepared-base");

        var layerDisk = Path.Combine(layerDir, "sandbox.vhdx");
        var preparedDisk = Path.Combine(preparedDir, "sandbox.vhdx");

        var factory = new FakeVirtualDiskFactory();
        factory.Parents[preparedDisk] = layerDisk;
        factory.ThrowOnDifferencing = new IOException("simulated VirtDisk failure");

        var layout = new FakeLayout(ContainerStoragesFolder);
        layout.Layers.Add(UsableLayer(storageId));

        var provisioner = new SandboxStorageProvisioner(layout, factory, new FakeHcsClient(), _logger);
        var target = NewDirectory("target");

        Assert.Throws<SandboxBackendException>(() => provisioner.Provision(target));

        Assert.Contains(_logger.Warnings, warning => warning.Contains(preparedDisk));

        // The partial child was cleaned up.
        Assert.False(File.Exists(Path.Combine(target, "sandbox.vhdx")));
    }

    private HcsLayerInfo UsableLayer(Guid storageId)
    {
        var layerRoot = NewDirectory(Path.Combine("Layers", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Combine(layerRoot, "Files", "Windows"));
        return new HcsLayerInfo { Id = Guid.NewGuid(), Path = layerRoot, StorageId = storageId };
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

        /// <summary>Scripted registry layers (the CmService hive content).</summary>
        public List<HcsLayerInfo> Layers { get; } = new();

        public string GetStorageDirectory(string id) => Path.Combine(SandboxDataFolder, id);

        public IReadOnlyList<HcsLayerInfo> ListLayers() => Layers;

        public string? ResolveBaseLayerPath() => null;
    }

    private sealed class FakeVirtualDiskFactory : IVirtualDiskFactory
    {
        public int Calls { get; private set; }

        public ulong LastMaximumSizeBytes { get; private set; }

        public int DifferencingCalls { get; private set; }

        public string? LastParentPath { get; private set; }

        /// <summary>When set, <see cref="CreateDifferencingVhdx"/> throws it.</summary>
        public Exception? ThrowOnDifferencing { get; set; }

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
            if (ThrowOnDifferencing is not null)
            {
                throw ThrowOnDifferencing;
            }

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

        public IDisposable Open(string id) => throw new NotSupportedException();

        public void Start(string id) => throw new NotSupportedException();

        public void Shutdown(string id, uint timeoutMs = 45_000) => throw new NotSupportedException();

        public void Terminate(string id) => throw new NotSupportedException();

        public string? Save(string id, string options) => throw new NotSupportedException();

        public void CreateEmptyRuntimeStateFile(string path) => throw new NotSupportedException();

        public void GrantVmAccess(string id, string path) => throw new NotSupportedException();

        public HcsProcessResult RunProcess(string id, string commandLine, string? user = null, uint timeoutMs = 120_000) => throw new NotSupportedException();
    }

    /// <summary>Captures warning-and-above log messages for assertions.</summary>
    private sealed class ListLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
