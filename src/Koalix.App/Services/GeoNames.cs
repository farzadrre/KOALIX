using System.Globalization;

namespace Koalix.App.Services;

/// <summary>
/// Persian and English names for countries that show up as tunnel exits (v0.4.6, expanded in v0.4.41),
/// plus geographic region grouping for the dashboard server map.
/// Keyed by ISO-3166 alpha-2; anything missing falls back to <see cref="RegionInfo"/> or the ISO code.
/// </summary>
public static class GeoNames
{
    public static readonly IReadOnlyDictionary<string, string> Fa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "آلمان", ["NL"] = "هلند", ["FI"] = "فنلاند", ["TR"] = "ترکیه", ["AE"] = "امارات",
        ["US"] = "آمریکا", ["GB"] = "انگلیس", ["UK"] = "انگلیس", ["FR"] = "فرانسه", ["SE"] = "سوئد", ["CH"] = "سوئیس",
        ["AT"] = "اتریش", ["AM"] = "ارمنستان", ["GE"] = "گرجستان", ["RU"] = "روسیه", ["PL"] = "لهستان",
        ["IT"] = "ایتالیا", ["ES"] = "اسپانیا", ["PT"] = "پرتغال", ["IE"] = "ایرلند", ["BE"] = "بلژیک",
        ["NO"] = "نروژ", ["DK"] = "دانمارک", ["CZ"] = "چک", ["SK"] = "اسلواکی", ["HU"] = "مجارستان",
        ["RO"] = "رومانی", ["BG"] = "بلغارستان", ["GR"] = "یونان", ["UA"] = "اوکراین", ["MD"] = "مولداوی",
        ["LV"] = "لتونی", ["LT"] = "لیتوانی", ["EE"] = "استونی", ["IS"] = "ایسلند", ["LU"] = "لوکزامبورگ",
        ["CA"] = "کانادا", ["BR"] = "برزیل", ["JP"] = "ژاپن", ["SG"] = "سنگاپور", ["HK"] = "هنگ‌کنگ",
        ["KR"] = "کره جنوبی", ["IN"] = "هند", ["MY"] = "مالزی", ["AU"] = "استرالیا", ["NZ"] = "نیوزیلند",
        ["ZA"] = "آفریقای جنوبی", ["IL"] = "اسرائیل", ["SA"] = "عربستان", ["QA"] = "قطر", ["KW"] = "کویت",
        ["OM"] = "عمان", ["IQ"] = "عراق", ["AZ"] = "آذربایجان", ["KZ"] = "قزاقستان", ["CN"] = "چین",
        ["CY"] = "قبرس", ["MT"] = "مالت", ["HR"] = "کرواسی", ["RS"] = "صربستان", ["MX"] = "مکزیک",
        ["AR"] = "آرژانتین", ["CL"] = "شیلی", ["TH"] = "تایلند", ["VN"] = "ویتنام", ["ID"] = "اندونزی",
        ["IR"] = "ایران", ["LB"] = "لبنان", ["JO"] = "اردن", ["EG"] = "مصر", ["MA"] = "مراکش",
        ["TW"] = "تایوان", ["PH"] = "فیلیپین", ["PK"] = "پاکستان", ["AF"] = "افغانستان", ["BD"] = "بنگلادش",
        ["BH"] = "بحرین", ["AL"] = "آلبانی", ["BA"] = "بوسنی", ["MK"] = "مقدونیه", ["ME"] = "مونته‌نگرو",
        ["SI"] = "اسلوونی", ["BY"] = "بلاروس", ["UZ"] = "ازبکستان", ["KG"] = "قرقیزستان", ["TJ"] = "تاجیکستان",
        ["TM"] = "ترکمنستان", ["CO"] = "کلمبیا", ["PE"] = "پرو", ["NG"] = "نیجریه", ["KE"] = "کنیا",
        ["TN"] = "تونس", ["DZ"] = "الجزایر", ["EU"] = "اروپا",
    };

    public static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["DE"] = "Germany", ["NL"] = "Netherlands", ["FI"] = "Finland", ["TR"] = "Turkey", ["AE"] = "UAE",
        ["US"] = "United States", ["GB"] = "United Kingdom", ["UK"] = "United Kingdom", ["FR"] = "France", ["SE"] = "Sweden", ["CH"] = "Switzerland",
        ["AT"] = "Austria", ["AM"] = "Armenia", ["GE"] = "Georgia", ["RU"] = "Russia", ["PL"] = "Poland",
        ["IT"] = "Italy", ["ES"] = "Spain", ["PT"] = "Portugal", ["IE"] = "Ireland", ["BE"] = "Belgium",
        ["NO"] = "Norway", ["DK"] = "Denmark", ["CZ"] = "Czechia", ["SK"] = "Slovakia", ["HU"] = "Hungary",
        ["RO"] = "Romania", ["BG"] = "Bulgaria", ["GR"] = "Greece", ["UA"] = "Ukraine", ["MD"] = "Moldova",
        ["LV"] = "Latvia", ["LT"] = "Lithuania", ["EE"] = "Estonia", ["IS"] = "Iceland", ["LU"] = "Luxembourg",
        ["CA"] = "Canada", ["BR"] = "Brazil", ["JP"] = "Japan", ["SG"] = "Singapore", ["HK"] = "Hong Kong",
        ["KR"] = "South Korea", ["IN"] = "India", ["MY"] = "Malaysia", ["AU"] = "Australia", ["NZ"] = "New Zealand",
        ["ZA"] = "South Africa", ["IL"] = "Israel", ["SA"] = "Saudi Arabia", ["QA"] = "Qatar", ["KW"] = "Kuwait",
        ["OM"] = "Oman", ["IQ"] = "Iraq", ["AZ"] = "Azerbaijan", ["KZ"] = "Kazakhstan", ["CN"] = "China",
        ["CY"] = "Cyprus", ["MT"] = "Malta", ["HR"] = "Croatia", ["RS"] = "Serbia", ["MX"] = "Mexico",
        ["AR"] = "Argentina", ["CL"] = "Chile", ["TH"] = "Thailand", ["VN"] = "Vietnam", ["ID"] = "Indonesia",
        ["IR"] = "Iran", ["LB"] = "Lebanon", ["JO"] = "Jordan", ["EG"] = "Egypt", ["MA"] = "Morocco",
        ["TW"] = "Taiwan", ["PH"] = "Philippines", ["PK"] = "Pakistan", ["AF"] = "Afghanistan", ["BD"] = "Bangladesh",
        ["BH"] = "Bahrain", ["AL"] = "Albania", ["BA"] = "Bosnia", ["MK"] = "North Macedonia", ["ME"] = "Montenegro",
        ["SI"] = "Slovenia", ["BY"] = "Belarus", ["UZ"] = "Uzbekistan", ["KG"] = "Kyrgyzstan", ["TJ"] = "Tajikistan",
        ["TM"] = "Turkmenistan", ["CO"] = "Colombia", ["PE"] = "Peru", ["NG"] = "Nigeria", ["KE"] = "Kenya",
        ["TN"] = "Tunisia", ["DZ"] = "Algeria", ["EU"] = "Europe",
    };

    private static readonly HashSet<string> EuropeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AD", "AL", "AT", "AX", "BA", "BE", "BG", "BY", "CH", "CY", "CZ", "DE", "DK", "EE", "ES", "EU",
        "FI", "FO", "FR", "GB", "GG", "GI", "GR", "HR", "HU", "IE", "IM", "IS", "IT", "JE", "LI", "LT",
        "LU", "LV", "MC", "MD", "ME", "MK", "MT", "NL", "NO", "PL", "PT", "RO", "RS", "RU", "SE", "SI",
        "SJ", "SK", "SM", "TR", "UA", "UK", "VA",
    };

    private static readonly HashSet<string> AmericasCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AG", "AI", "AR", "AW", "BB", "BL", "BM", "BO", "BQ", "BR", "BS", "BZ", "CA", "CL", "CO", "CR",
        "CU", "CW", "DM", "DO", "EC", "FK", "GD", "GF", "GL", "GP", "GT", "GY", "HN", "HT", "JM", "KN",
        "KY", "LC", "MF", "MQ", "MS", "MX", "NI", "PA", "PE", "PM", "PR", "PY", "SR", "SV", "SX", "TC",
        "TT", "US", "UY", "VC", "VE", "VG", "VI",
    };

    /// <summary>Localized country name for <paramref name="code"/> in <paramref name="language"/> ("fa" or "en").</summary>
    public static string Title(string? code, string language)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        var key = code.Trim().ToUpperInvariant();
        if (language == "fa" && Fa.TryGetValue(key, out var fa)) return fa;
        if (En.TryGetValue(key, out var en)) return en;
        try { return new RegionInfo(key).EnglishName; }
        catch { return key; }
    }

    /// <summary>Broad region bucket for the server map filter: "EU" (Europe), "AM" (Americas), "AS" (Asia / Middle East / Oceania / Africa).</summary>
    public static string RegionOf(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "Other";
        var key = code.Trim().ToUpperInvariant();
        if (EuropeCodes.Contains(key)) return "EU";
        if (AmericasCodes.Contains(key)) return "AM";
        return "AS";
    }
}
