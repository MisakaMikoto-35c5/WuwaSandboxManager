using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Volo.Abp;
using Volo.Abp.Modularity;
using Wsm.Localization;
using Xunit;

namespace Wsm.Tests.Localization;

/// <summary>Startup module for the localization tests; depends only on the resource module.</summary>
[DependsOn(typeof(WsmLocalizationModule))]
public sealed class LocalizationTestModule : AbpModule
{
}

/// <summary>
/// Covers the ABP localization resource: the <c>en</c> value is present and
/// switching <see cref="CultureInfo.CurrentUICulture"/> to <c>zh-Hans</c>
/// returns the Chinese text from the embedded virtual JSON.
/// </summary>
public sealed class LocalizationTests
{
    [Fact]
    public async Task Resource_returns_english_and_chinese_values()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en");

        using var app = await AbpApplicationFactory.CreateAsync<LocalizationTestModule>();
        await app.InitializeAsync();

        try
        {
            var localizer = app.ServiceProvider
                .GetRequiredService<IStringLocalizer<WindowsSandboxManagerResource>>();

            var english = localizer["EngineUnavailable"];
            Assert.False(english.ResourceNotFound);
            Assert.False(string.IsNullOrWhiteSpace(english.Value));

            CultureInfo.CurrentUICulture = new CultureInfo("zh-Hans");
            var chinese = localizer["EngineUnavailable"];
            Assert.False(chinese.ResourceNotFound);
            Assert.Equal(
                "此主机上没有可用的沙箱引擎。HCS 引擎需要提权进程：请以管理员身份运行（或加入 Hyper-V Administrators 组），或启用 Windows Sandbox 可选功能。",
                chinese.Value);
            Assert.NotEqual(english.Value, chinese.Value);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            await app.ShutdownAsync();
        }
    }
}
