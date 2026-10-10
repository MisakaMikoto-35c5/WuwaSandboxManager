using Volo.Abp.DependencyInjection;
using WindowsSandboxManager.Domain.Exceptions;
using WindowsSandboxManager.Domain.ValueObjects;
using Wsm.Engines.Abstractions;

namespace Wsm.Engine.WindowsContainer;

/// <summary>
/// Reserved Windows container (lightweight isolation) sandbox engine.
/// <para>
/// It advertises the <b>planned</b> capability matrix from the modularization
/// plan so the engine selector can reason about it, but it is not yet usable:
/// <see cref="IsAvailable"/> is always <c>false</c> and every runtime member
/// throws <see cref="SandboxNotSupportedException"/>.
/// </para>
/// </summary>
[ExposeServices(typeof(ISandboxEngine))]
internal sealed class WindowsContainerEngine : ISandboxEngine, ISingletonDependency
{
    private const string ReservedMessage =
        "The Windows Container engine is reserved / not implemented yet.";

    public string Name => "Windows Container (reserved)";

    public SandboxEngineKind Kind => SandboxEngineKind.WindowsContainer;

    public bool IsAvailable => false;

    /// <summary>Planned capability matrix (all capabilities are targeted; see plan §13.3).</summary>
    public SandboxEngineCapabilities Capabilities => new()
    {
        SupportsMultipleInstances = true,
        SupportsCpuConfiguration = true,
        SupportsCustomStorageLocation = true,
        SupportsCustomBaseImage = true,
        SupportsCheckpoints = true,
        SupportsRuntimeFolderMapping = true,
        SupportsProcessExecution = true,
        SupportsEnumeration = true,
        RequiresElevation = true,
    };

    public Task<IReadOnlyList<SandboxInstanceId>> EnumerateAsync(CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(ReservedMessage);

    public Task<SandboxRuntimeInfo> StartAsync(SandboxStartRequest request, CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(ReservedMessage);

    public Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(ReservedMessage);

    public Task<SandboxRuntimeInfo?> GetRuntimeAsync(SandboxInstanceId id, CancellationToken cancellationToken = default)
        => throw new SandboxNotSupportedException(ReservedMessage);
}
