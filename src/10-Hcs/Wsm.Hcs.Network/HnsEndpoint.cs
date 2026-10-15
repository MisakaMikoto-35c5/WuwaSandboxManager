namespace Wsm.Hcs.Network;

/// <summary>
/// A named HNS endpoint: the id HNS assigned, the caller-chosen name, the MAC
/// address the attached guest NIC carries and the IPv4 address HNS recorded
/// for it (may be null when the address is assigned by ICS DHCP instead).
/// </summary>
public sealed record HnsEndpoint(string Id, string? Name, string? MacAddress, string? IpAddress);
