using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;

namespace Wsm.Hcs.Json.Container.Internal;

/// <summary>
/// Reserved implementation of <see cref="IContainerJsonBuilder"/>. Every member
/// throws until the Windows container engine is implemented.
/// </summary>
[ExposeServices(typeof(IContainerJsonBuilder))]
internal sealed class ContainerJsonBuilder : IContainerJsonBuilder, ISingletonDependency
{
    private const string ReservedMessage =
        "Wsm.Hcs.Json.Container is reserved / not implemented yet: the Windows container HCS JSON builder will be implemented in a later phase.";

    public string Build(ContainerDefinition definition) => throw new SandboxNotSupportedException(ReservedMessage);
}
