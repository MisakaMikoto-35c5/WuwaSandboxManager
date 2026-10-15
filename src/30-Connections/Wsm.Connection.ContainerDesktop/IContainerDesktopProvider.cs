using Wsm.Domain.ValueObjects;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Everything a provider needs to start on a container: the sandbox, the
/// compute-system id, the host-visible root of the container's filesystem and the
/// recipe (so provider-specific tuning comes from the manifest).
/// </summary>
/// <param name="SandboxId">The sandbox this desktop belongs to.</param>
/// <param name="ComputeSystemId">HCS compute-system id (the sandbox id, as a string).</param>
/// <param name="ContainerRoot">Host path that is the container's <c>C:\</c>.</param>
/// <param name="Recipe">The sandbox recipe that selected this provider.</param>
public sealed record ContainerDesktopRequest(
    SandboxInstanceId SandboxId,
    string ComputeSystemId,
    string ContainerRoot,
    SandboxRecipe Recipe);

/// <summary>
/// One way of producing a container desktop. Providers are the replaceable part of
/// this feature: the core talks to <see cref="IContainerDesktopHost"/>, the host
/// resolves a provider from the sandbox's manifest, and a better mechanism can be
/// added (or swapped in) without touching callers.
/// <para>
/// Implementations are registered through ABP (implement
/// <c>ISingletonDependency</c>) and picked by
/// <see cref="IContainerDesktopProviderRegistry"/>.
/// </para>
/// </summary>
public interface IContainerDesktopProvider
{
    /// <summary>Stable id, as referenced by <c>SandboxDesktopPlan.Provider</c>.</summary>
    string Id { get; }

    /// <summary>The kind of desktop this provider produces.</summary>
    ContainerDesktopKind Kind { get; }

    /// <summary>Whether the provider can run on this host right now.</summary>
    bool IsSupported { get; }

    /// <summary>All live sessions owned by this provider.</summary>
    IReadOnlyList<ContainerDesktopSession> Sessions { get; }

    /// <summary>Gets a live session, or <c>null</c>.</summary>
    ContainerDesktopSession? Get(SandboxInstanceId id);

    /// <summary>Starts (or adopts) a desktop for the request.</summary>
    Task<ContainerDesktopSession> StartAsync(ContainerDesktopRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adopts a desktop that is already running; never starts one.</summary>
    Task<ContainerDesktopSession?> AttachAsync(ContainerDesktopRequest request, CancellationToken cancellationToken = default);

    /// <summary>Shows the session in the interactive session (see the host launcher).</summary>
    Task<ContainerDesktopSession> PresentAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);

    /// <summary>Sends one input command to the container.</summary>
    Task SendInputAsync(SandboxInstanceId id, string command, CancellationToken cancellationToken = default);

    /// <summary>Stops the session.</summary>
    Task StopAsync(SandboxInstanceId id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves a provider from a sandbox's manifest. Selection order is:
/// <c>SandboxDesktopPlan.Provider</c> (explicit id) → <c>SandboxDesktopPlan.Kind</c>
/// → the default provider.
/// </summary>
public interface IContainerDesktopProviderRegistry
{
    /// <summary>Every registered provider.</summary>
    IReadOnlyList<IContainerDesktopProvider> Providers { get; }

    /// <summary>Finds a provider by its id.</summary>
    IContainerDesktopProvider? Find(string id);

    /// <summary>The provider a recipe asks for.</summary>
    IContainerDesktopProvider Resolve(SandboxRecipe recipe);
}
