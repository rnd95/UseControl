using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using UseControl.Core;
using UseControl.Localization;

namespace UseControl.Cli.Tests;

[Collection("Culture")]
public partial class LocalizationTests
{
    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex Placeholder();

    private static IReadOnlyCollection<string> English => Loc.Keys(CultureInfo.InvariantCulture);

    public static TheoryData<string> Languages()
    {
        var d = new TheoryData<string>();
        foreach (var c in Loc.Available.Where(c => c.Name != "en")) d.Add(c.Name);
        return d;
    }

    [Fact]
    public void Every_error_code_has_a_text()
    {
        var codes = typeof(ErrorCodes).GetFields(BindingFlags.Public | BindingFlags.Static).Select(f => (string)f.GetValue(null)!);
        Assert.All(codes, c => Assert.Contains("Error." + c, English));
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Every_language_has_every_text(string language)
    {
        var translated = Loc.Keys(CultureInfo.GetCultureInfo(language));
        Assert.Empty(English.Except(translated));
        Assert.Empty(translated.Except(English));   // no leftovers of removed texts
    }

    [Theory]
    [MemberData(nameof(Languages))]
    public void Translations_keep_the_placeholders(string language)
    {
        var lang = CultureInfo.GetCultureInfo(language);
        var previous = Loc.Culture;
        try
        {
            foreach (var key in English)
            {
                Loc.Culture = CultureInfo.GetCultureInfo("en");
                var en = Placeholder().Matches(Loc.T(key)).Select(m => m.Groups[1].Value).Distinct().Order();
                Loc.Culture = lang;
                var tr = Placeholder().Matches(Loc.T(key)).Select(m => m.Groups[1].Value).Distinct().Order();
                Assert.True(en.SequenceEqual(tr), $"{language}: {key} has different placeholders");
            }
        }
        finally
        {
            Loc.Culture = previous;
        }
    }

    [Theory]
    [InlineData("uk-UA", "uk")]
    [InlineData("uk", "uk")]
    [InlineData("en-GB", "en")]
    [InlineData("de-DE", "de")]
    [InlineData("pl", "pl")]
    [InlineData("fr-FR", "en")]
    [InlineData("", "en")]
    [InlineData("not a culture", "en")]
    public void The_closest_available_language_is_picked(string requested, string expected) =>
        Assert.Equal(expected, Loc.Pick(requested).Name);

    [Fact]
    public void Errors_are_translated_with_their_arguments()
    {
        var previous = Loc.Culture;
        try
        {
            Loc.Culture = Loc.Pick("uk");
            Assert.Equal(@"D:\X уже є в списку.", Loc.Error(new UcException(ErrorCodes.AlreadyInList, @"D:\X")));
            Loc.Culture = Loc.Pick("en");
            Assert.Equal(@"D:\X is already in the list.", Loc.Error(new UcException(ErrorCodes.AlreadyInList, @"D:\X")));
            Assert.Equal("fallback", Loc.Error("NoSuchCode", [], "fallback"));
        }
        finally
        {
            Loc.Culture = previous;
        }
    }
}
