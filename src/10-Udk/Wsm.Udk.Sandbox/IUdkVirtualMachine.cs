namespace Wsm.Udk.Sandbox;

/// <summary>
/// A single live (or created) UDK-managed Windows Sandbox VM. This is a thin,
/// strongly-typed facade over <c>WindowsUdk.Security.Isolation.ManagedWindowsVM</c>
/// — the same object the platform's <c>WindowsSandboxServer</c> drives.
/// </summary>
public interface IUdkVirtualMachine
{
    /// <summary>The compute system identity (also the RDPBASE pipe name, <c>\\.\pipe\&lt;Id&gt;</c>).</summary>
    Guid Id { get; }

    /// <summary>The guest's default (pre-created) account name, e.g. <c>WDAGUtilityAccount</c>.</summary>
    string DefaultUserName { get; }

    /// <summary>Why the VM terminated (meaningful once it has stopped).</summary>
    UdkTerminationReason TerminatedReason { get; }

    /// <summary>Creates the running reference; the VM boots. Idempotent.</summary>
    void Start();

    /// <summary>Terminates the compute system and releases the running reference.</summary>
    void Terminate();

    /// <summary>Maps a host folder into the running guest.</summary>
    void ShareFolder(string hostFolder, string guestFolder, bool allowWrite);

    /// <summary>Removes a previously mapped folder.</summary>
    void UnshareFolder(string guestFolder);

    /// <summary>Runs a command in the guest and returns its exit code.</summary>
    int Execute(string commandLine, UdkProcessOptions? options = null);

    /// <summary>Returns the guest's IP addresses (empty when networking is off).</summary>
    IReadOnlyList<string> GetIpAddresses();
}

/// <summary>Why a UDK VM terminated (mirrors <c>VMTerminatedReason</c>).</summary>
public enum UdkTerminationReason
{
    None = 0,
    UserInitiated = 1,
    Crashed = 2,
    UnknownFailure = 3,
    UserInitiatedShutdown = 4,
    UserInitiatedShutdownForReboot = 5,
}
