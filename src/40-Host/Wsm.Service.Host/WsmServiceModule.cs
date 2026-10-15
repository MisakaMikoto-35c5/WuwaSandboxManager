using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Volo.Abp.Autofac;
using Volo.Abp.Modularity;
using Wsm.Application;
using Wsm.Application.EngineSelection;
using Wsm.Engine.Hcs;
using Wsm.Engine.WindowsContainer;
using Wsm.Engine.WindowsSandbox;
using Wsm.Connection.ContainerDesktop;
using Wsm.Localization;
using Wsm.Service.Host.Internal;

namespace Wsm.Service.Host;

/// <summary>
/// Root ABP startup module for the resident WSM agent service. It composes the
/// same graph as the CLI host — the application layer (engine selection and
/// persistence), all three sandbox engines and the non-UI localization resource —
/// so the service is able to create and manage sandbox VMs itself.
/// <para>
/// <see cref="AbpAutofacModule"/> is listed explicitly because the host is built
/// with an external (Kestrel) service provider; unlike
/// <c>AbpApplicationFactory.CreateAsync(..., o =&gt; o.UseAutofac())</c> it is not
/// added automatically, and the application assemblies expect the Autofac
/// conventional registrars to be present.
/// </para>
/// </summary>
[DependsOn(
    typeof(AbpAutofacModule),
    typeof(SandboxApplicationModule),
    typeof(EngineSelectionModule),
    typeof(HcsEngineModule),
    typeof(WindowsSandboxEngineModule),
    typeof(WindowsContainerEngineModule),
    typeof(WsmConnectionContainerDesktopModule),
    typeof(WsmLocalizationModule))]
public sealed class WsmServiceModule : AbpModule
{
    /// <summary>
    /// Configures console logging: Information by default so the resident service
    /// reports its startup (e.g. the listening pipe), overridable with the
    /// <c>WSM_LOG_LEVEL</c> environment variable (e.g. <c>Debug</c>).
    /// </summary>
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        var level = Enum.TryParse<LogLevel>(
                Environment.GetEnvironmentVariable("WSM_LOG_LEVEL"),
                ignoreCase: true,
                out var parsed)
            ? parsed
            : LogLevel.Information;

        context.Services.AddLogging(builder => builder
            .AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(level));

        // The gRPC implementation is resolved by MapGrpcService per call. It does
        // not implement an ABP dependency interface, so register it explicitly.
        context.Services.AddSingleton<WsmServiceGrpc>();
    }
}
