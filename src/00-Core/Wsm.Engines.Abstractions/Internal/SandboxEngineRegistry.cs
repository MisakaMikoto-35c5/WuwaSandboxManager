using Volo.Abp.DependencyInjection;
using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;

namespace Wsm.Engines.Abstractions.Internal;

/// <summary>
/// Default <see cref="ISandboxEngineRegistry"/>: resolves all
/// <see cref="ISandboxEngine"/> implementations via conventional DI and looks
/// them up by <see cref="SandboxEngineKind"/>.
/// <para>
/// Invariant (N3): engines MUST be registered as
/// <see cref="ISingletonDependency"/>. Engines hold native handles and
/// connections; registering one as transient would let it be captured as a
/// captive dependency by singleton consumers (this registry and the selector).
/// </para>
/// </summary>
[ExposeServices(typeof(ISandboxEngineRegistry))]
internal sealed class SandboxEngineRegistry : ISandboxEngineRegistry, ISingletonDependency
{
    private readonly IReadOnlyList<ISandboxEngine> _engines;

    public SandboxEngineRegistry(IEnumerable<ISandboxEngine> engines)
    {
        var resolved = engines.ToArray();

        var duplicateKind = resolved
            .GroupBy(engine => engine.Kind)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;

        if (duplicateKind is { } kind)
        {
            throw new SandboxDomainException(
                $"Duplicate sandbox engine registration for kind '{kind}'. " +
                "Each SandboxEngineKind may be registered at most once.");
        }

        _engines = resolved;
    }

    public IReadOnlyList<ISandboxEngine> Engines => _engines;

    public ISandboxEngine? Find(SandboxEngineKind kind)
        => _engines.FirstOrDefault(engine => engine.Kind == kind);
}
