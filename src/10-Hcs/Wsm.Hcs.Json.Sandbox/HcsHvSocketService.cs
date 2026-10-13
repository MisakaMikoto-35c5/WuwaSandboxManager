namespace Wsm.Hcs.Json.Sandbox;

/// <summary>
/// One entry of the <c>VirtualMachine.Devices.HvSocket.HvSocketConfig.ServiceTable</c>.
/// The Windows Sandbox guest expects a fixed set of Hyper-V socket services — the
/// host-side session/UI infrastructure binds and connects them — so the entries
/// (and their security descriptors) must match the platform's own configuration
/// byte for byte, not just the LSM/NLM pair.
/// </summary>
public sealed record HcsHvSocketService
{
    /// <summary>The service GUID (the service table key).</summary>
    public required Guid Id { get; init; }

    /// <summary>Discretionary ACL protecting the guest's bind endpoint.</summary>
    public required string BindSecurityDescriptor { get; init; }

    /// <summary>Discretionary ACL protecting a connection to the service.</summary>
    public required string ConnectSecurityDescriptor { get; init; }

    /// <summary>Allow a wildcard (<c>*</c>) service id on bind.</summary>
    public bool AllowWildcardBinds { get; init; }
}
