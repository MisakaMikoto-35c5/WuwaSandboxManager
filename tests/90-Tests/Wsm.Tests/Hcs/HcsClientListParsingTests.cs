using Wsm.Hcs.Interop;
using Wsm.Hcs.Interop.Internal;
using Xunit;

namespace Wsm.Tests.Hcs;

/// <summary>
/// Tests for <see cref="HcsClient.ParseList"/>: the HCS enumerate result can be
/// either an object with a <c>ComputeSystems</c> array or a bare JSON array, and
/// the parser must never throw on a valid-but-unexpected shape (regression for
/// the "target element has type 'Array'" failure seen from <c>wsm running</c>).
/// </summary>
public sealed class HcsClientListParsingTests
{
    [Fact]
    public void Parses_object_with_ComputeSystems_array()
    {
        const string document = """
            {
              "ComputeSystems": [
                { "Id": "abc", "Owner": "user", "SystemType": "Container", "State": "Running", "Name": "one" },
                { "Id": "def", "SystemType": "VirtualMachine", "State": "Stopped" }
              ]
            }
            """;

        var systems = HcsClient.ParseList(document);

        Assert.Equal(2, systems.Count);
        Assert.Equal("abc", systems[0].Id);
        Assert.True(systems[0].IsRunning);
        Assert.True(systems[0].IsContainer);
        Assert.Equal("one", systems[0].Name);
        Assert.Equal("def", systems[1].Id);
        Assert.True(systems[1].IsVirtualMachine);
    }

    [Fact]
    public void Parses_bare_array_root()
    {
        const string document = """
            [
              { "Id": "abc", "State": "Running" },
              { "Id": "def", "State": "Stopped" }
            ]
            """;

        var systems = HcsClient.ParseList(document);

        Assert.Equal(2, systems.Count);
        Assert.Equal("abc", systems[0].Id);
        Assert.Equal("def", systems[1].Id);
    }

    [Fact]
    public void Empty_bare_array_yields_no_systems()
    {
        Assert.Empty(HcsClient.ParseList("[]"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Blank_input_yields_no_systems(string? document)
    {
        Assert.Empty(HcsClient.ParseList(document));
    }

    [Fact]
    public void Malformed_json_yields_no_systems_instead_of_throwing()
    {
        Assert.Empty(HcsClient.ParseList("{ not json"));
    }

    [Fact]
    public void Object_without_ComputeSystems_yields_no_systems()
    {
        Assert.Empty(HcsClient.ParseList("""{ "SomethingElse": 1 }"""));
    }

    [Fact]
    public void ComputeSystems_that_is_not_an_array_yields_no_systems()
    {
        Assert.Empty(HcsClient.ParseList("""{ "ComputeSystems": "nope" }"""));
    }

    [Fact]
    public void Bare_non_system_scalar_yields_no_systems()
    {
        Assert.Empty(HcsClient.ParseList("42"));
    }
}
