using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;

namespace Wsm.Hcs.Process.Internal;

/// <summary>
/// Reserved implementation of <see cref="IComputeProcessExecutor"/>. Every member
/// throws until the capability is implemented.
/// </summary>
[ExposeServices(typeof(IComputeProcessExecutor))]
internal sealed class ComputeProcessExecutor : IComputeProcessExecutor, ISingletonDependency
{
    private const string ReservedMessage =
        "Wsm.Hcs.Process is reserved / not implemented yet: in-guest process execution will be implemented in a later phase.";

    public Task<int> ExecuteAsync(
        string computeSystemId,
        string commandLine,
        string? workingDirectory = null,
        CancellationToken ct = default)
        => throw new SandboxNotSupportedException(ReservedMessage);
}
