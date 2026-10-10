using Volo.Abp.Localization;
using Volo.Abp.Modularity;
using Volo.Abp.VirtualFileSystem;

namespace Wsm.Localization;

/// <summary>
/// ABP module that registers the application's non-UI localization resource and
/// the supported languages (en, zh-Hans). The culture texts live in embedded
/// virtual JSON files under <c>/Localization/WindowsSandboxManager</c>.
/// </summary>
[DependsOn(typeof(AbpLocalizationModule))]
public sealed class WsmLocalizationModule : AbpModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        Configure<AbpVirtualFileSystemOptions>(options =>
            options.FileSets.AddEmbedded<WsmLocalizationModule>("Wsm.Localization"));

        Configure<AbpLocalizationOptions>(options =>
        {
            options.Resources
                .Add<WindowsSandboxManagerResource>("en")
                .AddVirtualJson("/Localization/WindowsSandboxManager");

            options.DefaultResourceType = typeof(WindowsSandboxManagerResource);

            options.Languages.Add(new LanguageInfo("en", "en", "English"));
            options.Languages.Add(new LanguageInfo("zh-Hans", "zh-Hans", "简体中文"));
        });
    }
}
