using Microsoft.Extensions.Logging;
using Volo.Abp.DependencyInjection;
using WindowsUdk.Security.Isolation;

namespace Wsm.Udk.Sandbox.Internal;

/// <summary>
/// Default <see cref="IUdkSandboxHost"/>. Wraps
/// <c>WindowsUdk.Security.Isolation.ManagedWindowsVM</c> — the exact WinRT type
/// Windows Sandbox's own <c>WindowsSandboxServer</c> uses (via the CsWinRT
/// projection <c>SandboxUDK.dll</c>) to build the VM.
/// </summary>
[ExposeServices(typeof(IUdkSandboxHost))]
internal sealed class ManagedWindowsVmHost : IUdkSandboxHost, ISingletonDependency
{
    // The HvSocket services the platform registers on every sandbox VM.
    private static readonly Guid LsmServiceId = new("F58797F6-C9F3-4D63-9BD4-E52AC020E586");
    private static readonly Guid NlmServiceId = new("A715AC94-B745-4889-9A0F-772D85A3CFA4");

    private readonly ILogger<ManagedWindowsVmHost> _logger;

    public ManagedWindowsVmHost(ILogger<ManagedWindowsVmHost> logger)
    {
        _logger = logger;
    }

    public bool IsAvailable => true;

    public IUdkVirtualMachine Create(UdkSandboxOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            var vmOptions = new VMOptions
            {
                DisplayName = options.DisplayName,
                MaxMemoryMB = options.MemoryMb,
                NetworkingMode = options.EnableNetworking ? VMNetworkingMode.Nat : VMNetworkingMode.None,
            };

            if (options.StorageMb > 0)
            {
                vmOptions.MaxStorageSizeMB = options.StorageMb;
            }

            if (options.CpuCount > 0)
            {
                vmOptions.VirtualProcessorCount = options.CpuCount;
            }

            if (options.EnableVirtualGpu)
            {
                vmOptions.VirtualGpuOptions.MirroringKind = VMVirtualGpuMirroringKind.AllGpus;
                vmOptions.VirtualGpuOptions.VendorExtensionsEnabled = true;
            }

            vmOptions.HyperVSocketServiceConfigurations.Add(new HyperVSocketServiceConfiguration(LsmServiceId));
            vmOptions.HyperVSocketServiceConfigurations.Add(new HyperVSocketServiceConfiguration(NlmServiceId));

            var vm = new ManagedWindowsVM(vmOptions);
            _logger.LogInformation(
                "Created UDK ManagedWindowsVM {VmId} ({DisplayName}, {MemoryMb}MB, {CpuCount} vCPU, networking={Networking}).",
                vm.Id, options.DisplayName, options.MemoryMb, options.CpuCount, options.EnableNetworking);

            return new ManagedWindowsVm(vm, _logger);
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException(
                $"Failed to create the UDK VM '{options.DisplayName}' (0x{ex.HResult:X8}): {ex.Message}",
                ex);
        }
    }

    public IUdkVirtualMachine Open(Guid id)
    {
        try
        {
            return new ManagedWindowsVm(ManagedWindowsVM.Open(id), _logger);
        }
        catch (Exception ex) when (ex is not UdkSandboxException)
        {
            throw new UdkSandboxException($"Failed to open UDK VM {id:D}: {ex.Message}", ex);
        }
    }
}
