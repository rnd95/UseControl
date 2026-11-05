using System.Text.Json;
using UseControl.Core.Browsers;
using UseControl.Core.Configuration;

namespace UseControl.Core.Tests;

public class PolicyValuesTests
{
    private static Dictionary<string, JsonElement> Policies(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void Numbers_and_booleans_become_DWORD_text_stays_text_null_is_skipped()
    {
        var v = PolicyValues.FromConfig(Policies("""{ "A": 2, "B": true, "C": false, "D": "off", "E": null }"""));
        Assert.Equal(4, v.Count);
        Assert.Equal(new PolicyValue("A", PolicyValueKind.DWord, 2), v[0]);
        Assert.Equal(1, v[1].Data);
        Assert.Equal(0, v[2].Data);
        Assert.Equal(new PolicyValue("D", PolicyValueKind.Text, "off"), v[3]);
    }

    [Theory]
    [InlineData("""{ "A": [1, 2] }""")]
    [InlineData("""{ "A": { "x": 1 } }""")]
    [InlineData("""{ "A": 1.5 }""")]
    public void Lists_objects_and_fractions_are_refused(string json) =>
        Assert.Equal(ErrorCodes.InvalidPolicyValue, Assert.Throws<UcException>(() => PolicyValues.FromConfig(Policies(json))).Code);

    [Fact]
    public void Firefox_extension_settings_block_everything_but_the_allowlist()
    {
        var f = new FirefoxSettings { ExtensionsAllowed = ["uBlock0@raymondhill.net"] };
        using var doc = JsonDocument.Parse(PolicyValues.FirefoxExtensionSettings(f));
        Assert.Equal("blocked", doc.RootElement.GetProperty("*").GetProperty("installation_mode").GetString());
        Assert.Equal("allowed", doc.RootElement.GetProperty("uBlock0@raymondhill.net").GetProperty("installation_mode").GetString());
    }

    [Fact]
    public void Default_browser_policies_are_all_convertible()
    {
        var c = ConfigSerializer.LoadDefault();
        Assert.NotEmpty(PolicyValues.FromConfig(c.Browsers.Chrome.Policies));
        Assert.NotEmpty(PolicyValues.FromConfig(c.Browsers.Edge.Policies));
    }
}
