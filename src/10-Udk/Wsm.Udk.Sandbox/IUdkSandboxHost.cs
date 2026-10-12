namespace Wsm.Udk.Sandbox;

/// <summary>
/// Creates and opens UDK-managed Windows Sandbox VMs. This is the one seam the
/// engines use; the WinRT types never leak past it.
/// </summary>
public interface IUdkSandboxHost
{
    /// <summary>Whether the UDK host can be used on this machine.</summary>
    bool IsAvailable { get; }

    /// <summary>Creates (but does not start) a new VM.</summary>
    IUdkVirtualMachine Create(UdkSandboxOptions options);

    /// <summary>Opens an existing VM by its compute-system id.</summary>
    IUdkVirtualMachine Open(Guid id);
}
