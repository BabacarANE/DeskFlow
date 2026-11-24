using Desk.Core.Rules;

namespace Desk.UnitTests;

public class ColorContrastTests
{
    [Fact]
    public void Black_on_white_is_21_to_1_and_a_color_on_itself_is_1_to_1()
    {
        Assert.Equal(21.0, ColorContrast.Ratio("#000000", "#FFFFFF"), 6);
        Assert.Equal(1.0, ColorContrast.Ratio("#7CC4EE", "#7CC4EE"), 6);
    }

    [Theory]
    [InlineData("#000000", 0.0)]
    [InlineData("#FFFFFF", 1.0)]
    [InlineData("#FF0000", 0.2126)]
    [InlineData("#00FF00", 0.7152)]
    [InlineData("#0000FF", 0.0722)]
    public void Relative_luminance_follows_the_WCAG_weights(string color, double expected) =>
        Assert.Equal(expected, ColorContrast.RelativeLuminance(color), 4);

    [Fact]
    public void The_ratio_is_symmetric_and_ignores_the_case_of_the_hex_digits()
    {
        Assert.Equal(ColorContrast.Ratio("#D63A14", "#F4F7FA"), ColorContrast.Ratio("#F4F7FA", "#D63A14"), 12);
        Assert.Equal(ColorContrast.Ratio("#d63a14", "#f4f7fa"), ColorContrast.Ratio("#D63A14", "#F4F7FA"), 12);
    }

    [Fact]
    public void Known_reference_ratios_match_the_WCAG_formula()
    {
        // #777777 on white is the classic 4.48:1 example; the default accent on the default surface stays above 3:1.
        Assert.Equal(4.48, ColorContrast.Ratio("#777777", "#FFFFFF"), 2);
        Assert.True(ColorContrast.Ratio(Desk.Core.Domain.ThemeDefaults.ColorAccent, Desk.Core.Domain.ThemeDefaults.ColorSurface) >= ColorContrast.MinimumAccentRatio);
        Assert.True(ColorContrast.Ratio("#EEEEEE", "#FFFFFF") < ColorContrast.MinimumAccentRatio);
    }

    [Theory]
    [InlineData("#123456", true)]
    [InlineData("#abcdef", true)]
    [InlineData("#ABCDEF", true)]
    [InlineData("123456", false)]
    [InlineData("#12345", false)]
    [InlineData("#1234567", false)]
    [InlineData("#GGGGGG", false)]
    [InlineData(" #123456", false)]
    [InlineData("#123456\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_the_strict_RRGGBB_format_is_accepted(string? value, bool expected) =>
        Assert.Equal(expected, ColorContrast.IsHex(value));

    [Fact]
    public void A_malformed_color_is_refused_by_the_calculation() =>
        Assert.Throws<ArgumentException>(() => ColorContrast.RelativeLuminance("red"));
}

public class ArticleRulesTests
{
    [Fact]
    public void Tags_are_trimmed_deduplicated_ignoring_case_and_keep_their_order()
    {
        Assert.Equal(["VPN", "réseau", "Wifi"], ArticleRules.NormalizeTags([" VPN ", "réseau", "vpn", "Wifi", "RÉSEAU"]));
        Assert.Equal("VPN,réseau", ArticleRules.JoinTags(["VPN", "réseau"]));
        Assert.Equal(["a", "b"], ArticleRules.SplitTags("a, b,"));
        Assert.Empty(ArticleRules.SplitTags(""));
    }

    [Theory]
    [InlineData("vpn", true)]
    [InlineData("  vpn  ", true)]
    [InlineData("a,b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void A_tag_has_no_comma_and_is_not_empty(string? tag, bool expected) =>
        Assert.Equal(expected, ArticleRules.IsValidTag(tag));

    [Fact]
    public void A_tag_is_at_most_30_characters()
    {
        Assert.True(ArticleRules.IsValidTag(new string('x', 30)));
        Assert.False(ArticleRules.IsValidTag(new string('x', 31)));
    }

    [Fact]
    public void The_excerpt_is_200_characters_at_most_and_never_cuts_a_surrogate_pair()
    {
        Assert.Equal("court", ArticleRules.Excerpt("court"));
        Assert.Equal(200, ArticleRules.Excerpt(new string('a', 500)).Length);
        var emoji = new string('a', 199) + "\U0001F600" + "suite";
        Assert.Equal(199, ArticleRules.Excerpt(emoji).Length);
    }

    [Fact]
    public void Search_words_ignore_single_characters_and_duplicates()
    {
        Assert.Equal(["vpn", "lent"], ArticleRules.SearchWords("  a vpn  VPN\tlent x "));
        Assert.Empty(ArticleRules.SearchWords("a b c"));
        Assert.Empty(ArticleRules.SearchWords(null));
        Assert.Empty(ArticleRules.SearchWords("   "));
    }

    [Fact]
    public void Like_wildcards_and_the_escape_character_are_escaped()
    {
        Assert.Equal("100\\%", ArticleRules.EscapeLike("100%"));
        Assert.Equal("a\\_b", ArticleRules.EscapeLike("a_b"));
        Assert.Equal("C:\\\\temp", ArticleRules.EscapeLike("C:\\temp"));
        Assert.Equal("\\\\\\%\\_", ArticleRules.EscapeLike("\\%_"));
    }
}
