using System.Globalization;

namespace Koalix.Infrastructure;

/// <summary>
/// v0.4.23 (suggestion #6, user: «نقشهٔ جغرافیایی سرورها — پرچم‌ها که داریم؛ یک نگاه کلی روی موقعیت‌ها») —
/// derives a server's country from its NAME. Panel exports overwhelmingly carry the flag emoji
/// («ZEUS|🇺🇸|man») or an explicit ISO/national token («DE-01», «US #3», «FR Paris»). Only unambiguous
/// evidence counts: a flag emoji (two regional-indicator symbols) or a 2-letter ISO token standing alone
/// between separators — never a bare substring match. Unknown names map to null; the UI groups them under
/// «نامشخص».
/// </summary>
public static class ServerCountry
{
    /// <summary>Flag emoji → ISO-2 ("🇩🇪" → "DE"). Regional indicators are astral-plane (U+1F1E6..U+1F1FF),
    /// so the scan walks UTF-16 surrogate pairs.</summary>
    public static string? FromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // 1) Flag emoji — two consecutive regional-indicator symbols (surrogate pairs).
        var flags = new List<char>(2);
        for (var i = 0; i < name.Length; i++)
        {
            var isIndicator = i + 1 < name.Length
                && char.IsHighSurrogate(name[i]) && char.IsLowSurrogate(name[i + 1])
                && char.ConvertToUtf32(name[i], name[i + 1]) is >= 0x1F1E6 and <= 0x1F1FF;
            if (isIndicator)
            {
                flags.Add((char)(char.ConvertToUtf32(name[i], name[i + 1]) - 0x1F1E6 + 'A'));
                i++;
                if (flags.Count == 2) break;
            }
            else if (flags.Count > 0) break; // the pair was interrupted — not a flag, fall through to tokens
        }
        if (flags.Count == 2) return new string([flags[0], flags[1]]);
        // 2) An explicit ISO token: "US", "de" — alone between separators (start/end/space/punct), never inside a word.
        foreach (var token in Tokenize(name))
        {
            if (token.Length == 2 && token.All(char.IsAsciiLetter) && Iso2.Contains(token.ToUpperInvariant()))
                return token.ToUpperInvariant();
        }
        return null;
    }

    /// <summary>Alphanumeric runs only — "-"/"_"/"."/"/#" and whitespace are all separators, so "DE-01" and
    /// "NL_Amsterdam" yield "DE"/"NL" while "MANAGEMENT-1" can never grow a fake two-letter token.</summary>
    private static IEnumerable<string> Tokenize(string name)
    {
        var start = -1;
        for (var i = 0; i <= name.Length; i++)
        {
            var ok = i < name.Length && char.IsAsciiLetterOrDigit(name[i]);
            if (ok && start < 0) start = i;
            else if (!ok && start >= 0) { yield return name[start..i]; start = -1; }
        }
    }

    /// <summary>ISO-3166 alpha-2 codes the packed flag set actually covers (Assets/Flags + flagcdn fallback is
    /// universal, so this is the full official list — kept as a set so random 2-letter words like "man", "it"
    /// in a sentence can only match when they genuinely stand alone as a token… "IT" and "AM" included on purpose).</summary>
    private static readonly HashSet<string> Iso2 = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD","AE","AF","AG","AI","AL","AM","AO","AR","AS","AT","AU","AW","AX","AZ","BA","BB","BD","BE","BF","BG","BH","BI","BJ","BL","BM","BN","BO","BQ","BR","BS","BT","BV","BW","BY","BZ",
        "CA","CC","CD","CF","CG","CH","CI","CK","CL","CM","CN","CO","CR","CU","CV","CW","CX","CY","CZ","DE","DJ","DK","DM","DO","DZ","EC","EE","EG","EH","ER","ES","ET","EU","FI","FJ","FK","FM","FO","FR",
        "GA","GB","GD","GE","GF","GG","GH","GI","GL","GM","GN","GP","GQ","GR","GS","GT","GU","GW","GY","HK","HM","HN","HR","HT","HU","ID","IE","IL","IM","IN","IO","IQ","IR","IS","IT","JE","JM","JO","JP",
        "KE","KG","KH","KI","KM","KN","KP","KR","KW","KY","KZ","LA","LB","LC","LI","LK","LR","LS","LT","LU","LV","LY","MA","MC","MD","ME","MF","MG","MH","MK","ML","MM","MN","MO","MP","MQ","MR","MS","MT","MU","MV","MW","MX","MY","MZ",
        "NA","NC","NE","NF","NG","NI","NL","NO","NP","NR","NU","NZ","OM","PA","PE","PF","PG","PH","PK","PL","PM","PN","PR","PS","PT","PW","PY","QA","RE","RO","RS","RU","RW","SA","SB","SC","SD","SE","SG","SH","SI","SJ","SK","SL","SM","SN","SO","SR","SS","ST","SV","SX","SY","SZ",
        "TC","TD","TF","TG","TH","TJ","TK","TL","TM","TN","TO","TR","TT","TV","TW","TZ","UA","UG","UM","US","UY","UZ","VA","VC","VE","VG","VI","VN","VU","WF","WS","YE","YT","ZA","ZM","ZW",
    };
}
