using Wsm.Domain.Exceptions;
using Wsm.Domain.ValueObjects;
using Xunit;

namespace Wsm.Tests.Domain;

/// <summary>Covers the <see cref="RailApp"/> launch-list entry value object.</summary>
public sealed class RailAppTests
{
    [Fact]
    public void A_name_defaults_to_the_program_path()
    {
        var app = new RailApp(string.Empty, @"C:\tools\app.exe");

        Assert.Equal(@"C:\tools\app.exe", app.Name);
        Assert.Equal(@"C:\tools\app.exe", app.Program);
        Assert.Null(app.Arguments);
    }

    [Fact]
    public void Blank_arguments_normalize_to_null()
    {
        Assert.Null(new RailApp("App", @"C:\app.exe", "   ").Arguments);
    }

    [Fact]
    public void An_empty_program_is_rejected()
    {
        Assert.Throws<SandboxValidationException>(() => new RailApp("App", "  "));
    }

    [Fact]
    public void Two_apps_with_the_same_content_are_equal()
    {
        Assert.Equal(
            new RailApp("App", @"C:\app.exe", "--flag"),
            new RailApp("App", @"C:\app.exe", "--flag"));
    }
}
