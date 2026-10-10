using Wsm.Domain.Exceptions;

namespace Wsm.Domain.ValueObjects;

/// <summary>
/// A host folder shared into the sandbox. Maps to a <c>&lt;MappedFolder&gt;</c>
/// entry of the .wsb recipe and to the runtime <c>MapFolder</c> gRPC call.
/// </summary>
public sealed record MappedFolder
{
    public string HostFolder { get; }

    /// <summary>Destination inside the guest. Empty means "container user's desktop".</summary>
    public string SandboxFolder { get; }

    public bool ReadOnly { get; init; }

    public bool AllowWrite => !ReadOnly;

    public MappedFolder(string hostFolder, string sandboxFolder = "", bool readOnly = true)
    {
        if (string.IsNullOrWhiteSpace(hostFolder))
        {
            throw new SandboxValidationException("Host folder must not be empty.");
        }

        HostFolder = hostFolder.Trim();
        SandboxFolder = sandboxFolder?.Trim() ?? string.Empty;
        ReadOnly = readOnly;
    }

    public MappedFolder WithWriteAccess() => this with { ReadOnly = false };

    public override string ToString()
        => string.IsNullOrEmpty(SandboxFolder)
            ? $"{HostFolder} -> (desktop){(ReadOnly ? " [ro]" : " [rw]")}"
            : $"{HostFolder} -> {SandboxFolder}{(ReadOnly ? " [ro]" : " [rw]")}";
}
