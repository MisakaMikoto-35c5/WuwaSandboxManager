using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Application.EngineSelection.Internal;

/// <summary>
/// Default <see cref="ISandboxEngineSelector"/>. Selection order:
/// <list type="number">
///   <item>an explicitly preferred engine, when it is available;</item>
///   <item>only engines that are currently available (else fail with an elevation hint);</item>
///   <item>an engine whose capabilities satisfy the recipe's requirements;</item>
///   <item>the lowest <see cref="SandboxEngineKind"/> (HCS first) — the order is
///   computed explicitly and never taken from the DI enumeration.</item>
/// </list>
/// </summary>
[ExposeServices(typeof(ISandboxEngineSelector))]
internal sealed class EngineSelector : ISandboxEngineSelector, ISingletonDependency
{
    private readonly ISandboxEngineRegistry _registry;

    public EngineSelector(ISandboxEngineRegistry registry) => _registry = registry;

    public ISandboxEngine Select(SandboxStartRequest request)
    {
        var available = _registry.Engines
            .Where(engine => engine.IsAvailable)
            .OrderBy(engine => (int)engine.Kind)
            .ToArray();

        if (request.PreferredEngine is { } preferredKind)
        {
            var preferred = available.FirstOrDefault(engine => engine.Kind == preferredKind);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        if (available.Length == 0)
        {
            throw new SandboxNotSupportedException(
                "No sandbox engine is available on this host. The HCS engine requires an elevated " +
                "process: run WindowsSandboxManager as Administrator (or as a member of the Hyper-V " +
                "Administrators group), or enable the Windows Sandbox optional feature.");
        }

        var capable = SelectByCapability(request.Recipe, available);
        if (capable is not null)
        {
            return capable;
        }

        // Explicit ordering: never trust the DI enumeration order.
        return available.OrderBy(engine => (int)engine.Kind).First();
    }

    private static ISandboxEngine? SelectByCapability(SandboxRecipe recipe, IReadOnlyList<ISandboxEngine> available)
    {
        var requirements = RequiredCapabilities(recipe).ToArray();
        if (requirements.Length == 0)
        {
            return null;
        }

        return available
            .Where(engine => requirements.All(requirement => requirement(engine.Capabilities)))
            .OrderBy(engine => (int)engine.Kind)
            .FirstOrDefault();
    }

    private static IEnumerable<Func<SandboxEngineCapabilities, bool>> RequiredCapabilities(SandboxRecipe recipe)
    {
        if (recipe.Cpu != SandboxCpuConfiguration.Default())
        {
            yield return capabilities => capabilities.SupportsCpuConfiguration;
        }

        if (recipe.Storage.HasCustomLocation)
        {
            yield return capabilities => capabilities.SupportsCustomStorageLocation;
        }
    }
}
