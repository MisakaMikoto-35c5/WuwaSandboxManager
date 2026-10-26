using System.Globalization;
using Wsm.Application;
using Wsm.Connection.ContainerDesktop;
using Wsm.Domain;
using Wsm.Domain.ValueObjects;
using Wsm.Ui.WinUI.Localization;
using Wsm.Ui.WinUI.ViewModels;
using Xunit;

namespace Wsm.Tests.ViewModels;

public sealed class SandboxInstanceViewModelTests
{
    [Fact]
    public void Editing_an_instance_preserves_its_container_image_and_desktop_plan()
    {
        var original = SandboxRecipe.Default with
        {
            ContainerImagePath = @"C:\wsm-images\server-full-ltsc2022",
            Desktop = new SandboxDesktopPlan
            {
                Kind = ContainerDesktopKind.WindowMirror,
                InteractiveSession = true,
                Provider = ContainerDesktopProviderIds.WindowMirror,
            },
        };
        var viewModel = new SandboxInstanceViewModel(
            null!,
            null!,
            new SandboxInstanceDto(
                Guid.NewGuid(),
                "rail",
                SandboxState.Created,
                original,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                SandboxEngineKind.WindowsContainer,
                "test.wsm"),
            new TestLocalizer());

        var rebuilt = viewModel.BuildRecipe();

        Assert.Equal(original.ContainerImagePath, rebuilt.ContainerImagePath);
        Assert.Equal(original.Desktop, rebuilt.Desktop);
    }

    private sealed class TestLocalizer : IAppLocalizer
    {
        public CultureInfo CurrentCulture { get; } = CultureInfo.InvariantCulture;
        public string this[string key] => key;
        public string Get(string key, params object[] args) => string.Format(this[key], args);
        public event EventHandler? CultureChanged { add { } remove { } }
        public Task SetCultureAsync(string cultureName) => Task.CompletedTask;
    }
}
