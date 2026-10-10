using WindowsSandboxManager.Application.Dtos;
using WindowsSandboxManager.Domain;

namespace WindowsSandboxManager.Application.Mapping;

internal static class SandboxMapper
{
    public static SandboxInstanceDto ToDto(this SandboxInstance instance) => new(
        instance.Id.Value,
        instance.Name.Value,
        instance.State,
        instance.Recipe,
        instance.Runtime,
        instance.FaultReason,
        instance.CreatedUtc,
        instance.UpdatedUtc);
}
