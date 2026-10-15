using Microsoft.Extensions.DependencyInjection;
using Volo.Abp.Modularity;
using Wsm.Connection.ContainerDesktop.Internal;
using Wsm.Engines.Abstractions;

namespace Wsm.Connection.ContainerDesktop;

/// <summary>
/// Registers the container desktop channel: the provider registry plus the built-in
/// providers. It depends only on the engine abstractions, so the service host and the
/// CLI compose it the same way.
/// <para>
/// Registration is explicit rather than conventional: the set of providers is the
/// <b>replaceable surface</b> of this feature, so it should be one visible list - swap
/// an entry, or add a provider for a recipe to select by id.
/// </para>
/// </summary>
[DependsOn(typeof(SandboxEngineAbstractionsModule))]
public sealed class WsmConnectionContainerDesktopModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        // The providers: exactly these two today ("frame-buffer" and "window-mirror").
        context.Services.AddSingleton<IContainerDesktopProvider, FrameBufferDesktopProvider>();
        context.Services.AddSingleton<IContainerDesktopProvider, WindowMirrorDesktopProvider>();

        // Their shared collaborators and the manifest-driven selector.
        context.Services.AddSingleton<ISessionProcessLauncher, SessionProcessLauncher>();
        context.Services.AddSingleton<IContainerDesktopProviderRegistry, ContainerDesktopProviderRegistry>();
        context.Services.AddSingleton<IContainerDesktopHost, ContainerDesktopHost>();
    }
}
