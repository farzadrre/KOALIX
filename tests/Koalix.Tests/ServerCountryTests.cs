using Koalix.Infrastructure;

namespace Koalix.Tests;

/// <summary>
/// v0.4.23 — the server-map country detection («نقشهٔ سرورها»): names from real panels (flag emoji, ISO tokens,
/// mixed words) must resolve to the right ISO-2 — and ambiguous names must stay unknown rather than guess.
/// </summary>
public class ServerCountryTests
{
    [Theory]
    [InlineData("ZEUS|🇺🇸|man", "US")]
    [InlineData("🇩🇪 Frankfurt 01", "DE")]
    [InlineData("«🇹🇷» Istanbul", "TR")]
    [InlineData("DE-01 Hetzner", "DE")]
    [InlineData("us #3 — clean ip", "US")]
    [InlineData("NL_Amsterdam", "NL")]
    public void Detects_flag_emoji_and_iso_tokens(string name, string expected)
    {
        Assert.Equal(expected, ServerCountry.FromName(name));
    }

    [Theory]
    [InlineData("ZEUS|man|edge")]          // no flag, no ISO token
    [InlineData("FRANKFURT")]              // a country NAME is not an ISO token
    [InlineData("VLESS - Clean IP : 1.2.3.4")]
    [InlineData("")]
    [InlineData(null)]
    public void Ambiguous_names_stay_unknown(string? name)
    {
        Assert.Null(ServerCountry.FromName(name));
    }

    [Fact]
    public void Lone_words_never_match_inside_larger_tokens()
    {
        // "man" sits inside "MANAGEMENT-1" — no separator-isolated 2-letter token may be invented from it.
        Assert.Null(ServerCountry.FromName("MANAGEMENT-1"));
        // a lone "IT" IS a valid token (Italy) — but only when it stands alone:
        Assert.Equal("IT", ServerCountry.FromName("IT"));
    }
}
