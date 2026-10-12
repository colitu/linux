using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace v2rayN.Desktop.Services;

/// <summary>
/// Server names in the app language. The panel sends a node's display name and city in Turkish
/// ("Almanya", "Londra", "Letonya (Köprü)"). They are matched case- and diacritic-insensitively
/// against the Turkish, English and Russian names (and a few aliases) and shown in the app
/// language; text that matches nothing stays exactly as the panel sent it. The tables are shared by
/// all Colitu apps (sunucu-adlari-ceviri.json, 2026-10-12; iOS lib/colitu/l10n/server_names.dart,
/// Android l10n/ColituServerNames.kt). Without an explicit language the app language is used.
/// </summary>
public static partial class ColituServerNames
{
    /// <summary>Languages in table order.</summary>
    private static readonly string[] Languages = ["ru", "tr", "en"];

    /// <summary>Cities: [ru, tr, en, aliases...].</summary>
    private static readonly string[][] Cities =
    [
        ["Тирана", "Tiran", "Tirana", "Tirana"],
        ["Мельбурн", "Melbourne", "Melbourne"],
        ["Сан-Паулу", "São Paulo", "São Paulo", "Sao Paulo"],
        ["Франкфурт", "Frankfurt", "Frankfurt"],
        ["Берлин", "Berlin", "Berlin"],
        ["Таллин", "Tallinn", "Tallinn"],
        ["Тарту", "Tartu", "Tartu"],
        ["Мадрид", "Madrid", "Madrid"],
        ["Хельсинки", "Helsinki", "Helsinki"],
        ["Париж", "Paris", "Paris"],
        ["Лондон", "Londra", "London"],
        ["Ковентри", "Coventry", "Coventry"],
        ["Салоники", "Selanik", "Thessaloniki"],
        ["Тель-Авив", "Tel Aviv", "Tel Aviv", "Tel-Aviv"],
        ["Токио", "Tokyo", "Tokyo"],
        ["Алматы", "Almatı", "Almaty"],
        ["Вильнюс", "Vilnius", "Vilnius"],
        ["Рига", "Riga", "Riga"],
        ["Керкраде", "Kerkrade", "Kerkrade"],
        ["Дронтен", "Dronten", "Dronten"],
        ["Варшава", "Varşova", "Warsaw"],
        ["Москва", "Moskova", "Moscow"],
        ["Сингапур", "Singapur", "Singapore"],
        ["Бурса", "Bursa", "Bursa"],
        ["Киев", "Kiev", "Kyiv", "Kyiv"],
        ["Даллас", "Dallas", "Dallas"],
        ["Амстердам", "Amsterdam", "Amsterdam"],
        ["Стамбул", "İstanbul", "Istanbul"],
        ["Анкара", "Ankara", "Ankara"],
        ["Стокгольм", "Stockholm", "Stockholm"],
        ["Вена", "Viyana", "Vienna"],
        ["Цюрих", "Zürih", "Zurich"],
        ["Прага", "Prag", "Prague"],
        ["Бухарест", "Bükreş", "Bucharest"],
        ["София", "Sofya", "Sofia"],
        ["Будапешт", "Budapeşte", "Budapest"],
        ["Афины", "Atina", "Athens"],
        ["Белград", "Belgrad", "Belgrade"],
        ["Ереван", "Erivan", "Yerevan"],
        ["Тбилиси", "Tiflis", "Tbilisi"],
        ["Баку", "Bakü", "Baku"],
        ["Дубай", "Dubai", "Dubai"],
        ["Копенгаген", "Kopenhag", "Copenhagen"],
        ["Осло", "Oslo", "Oslo"],
        ["Милан", "Milano", "Milan"],
        ["Рим", "Roma", "Rome"],
        ["Мюнхен", "Münih", "Munich"],
        ["Дюссельдорф", "Düsseldorf", "Düsseldorf"],
        ["Нюрнберг", "Nürnberg", "Nuremberg"],
        ["Нью-Йорк", "New York", "New York"],
        ["Лос-Анджелес", "Los Angeles", "Los Angeles"],
        ["Майами", "Miami", "Miami"],
        ["Торонто", "Toronto", "Toronto"],
        ["Монреаль", "Montreal", "Montreal"],
        ["Сидней", "Sidney", "Sydney"],
        ["Гонконг", "Hong Kong", "Hong Kong"],
        ["Сеул", "Seul", "Seoul"],
    ];

    /// <summary>Suffixes in "(...)" after a country name: [ru, tr, en].</summary>
    private static readonly string[][] Suffixes =
    [
        ["Мост", "Köprü", "Bridge"],
    ];

    // code → [ru, tr, en]
    private static readonly Dictionary<string, string[]> Countries = new()
    {
        ["AE"] = ["ОАЭ", "BAE", "United Arab Emirates"],
        ["AL"] = ["Албания", "Arnavutluk", "Albania"],
        ["AM"] = ["Армения", "Ermenistan", "Armenia"],
        ["AT"] = ["Австрия", "Avusturya", "Austria"],
        ["AU"] = ["Австралия", "Avustralya", "Australia"],
        ["AZ"] = ["Азербайджан", "Azerbaycan", "Azerbaijan"],
        ["BE"] = ["Бельгия", "Belçika", "Belgium"],
        ["BG"] = ["Болгария", "Bulgaristan", "Bulgaria"],
        ["BR"] = ["Бразилия", "Brezilya", "Brazil"],
        ["BY"] = ["Беларусь", "Belarus", "Belarus"],
        ["CA"] = ["Канада", "Kanada", "Canada"],
        ["CH"] = ["Швейцария", "İsviçre", "Switzerland"],
        ["CY"] = ["Кипр", "Kıbrıs", "Cyprus"],
        ["CZ"] = ["Чехия", "Çekya", "Czechia"],
        ["DE"] = ["Германия", "Almanya", "Germany"],
        ["DK"] = ["Дания", "Danimarka", "Denmark"],
        ["EE"] = ["Эстония", "Estonya", "Estonia"],
        ["ES"] = ["Испания", "İspanya", "Spain"],
        ["FI"] = ["Финляндия", "Finlandiya", "Finland"],
        ["FR"] = ["Франция", "Fransa", "France"],
        ["GB"] = ["Великобритания", "Birleşik Krallık", "United Kingdom"],
        ["GE"] = ["Грузия", "Gürcistan", "Georgia"],
        ["GR"] = ["Греция", "Yunanistan", "Greece"],
        ["HK"] = ["Гонконг", "Hong Kong", "Hong Kong"],
        ["HU"] = ["Венгрия", "Macaristan", "Hungary"],
        ["IE"] = ["Ирландия", "İrlanda", "Ireland"],
        ["IL"] = ["Израиль", "İsrail", "Israel"],
        ["IN"] = ["Индия", "Hindistan", "India"],
        ["IS"] = ["Исландия", "İzlanda", "Iceland"],
        ["IT"] = ["Италия", "İtalya", "Italy"],
        ["JP"] = ["Япония", "Japonya", "Japan"],
        ["KR"] = ["Южная Корея", "Güney Kore", "South Korea"],
        ["KZ"] = ["Казахстан", "Kazakistan", "Kazakhstan"],
        ["LT"] = ["Литва", "Litvanya", "Lithuania"],
        ["LU"] = ["Люксембург", "Lüksemburg", "Luxembourg"],
        ["LV"] = ["Латвия", "Letonya", "Latvia"],
        ["MD"] = ["Молдова", "Moldova", "Moldova"],
        ["NL"] = ["Нидерланды", "Hollanda", "Netherlands"],
        ["NO"] = ["Норвегия", "Norveç", "Norway"],
        ["PL"] = ["Польша", "Polonya", "Poland"],
        ["PT"] = ["Португалия", "Portekiz", "Portugal"],
        ["RO"] = ["Румыния", "Romanya", "Romania"],
        ["RS"] = ["Сербия", "Sırbistan", "Serbia"],
        ["RU"] = ["Россия", "Rusya", "Russia"],
        ["SE"] = ["Швеция", "İsveç", "Sweden"],
        ["SG"] = ["Сингапур", "Singapur", "Singapore"],
        ["SK"] = ["Словакия", "Slovakya", "Slovakia"],
        ["TR"] = ["Турция", "Türkiye", "Türkiye"],
        ["UA"] = ["Украина", "Ukrayna", "Ukraine"],
        ["US"] = ["США", "ABD", "United States"],
        ["UZ"] = ["Узбекистан", "Özbekistan", "Uzbekistan"],
    };

    private static readonly Lazy<Dictionary<string, string[]>> CityIndex = new(() => Index(Cities));
    private static readonly Lazy<Dictionary<string, string[]>> SuffixIndex = new(() => Index(Suffixes));
    private static readonly Lazy<Dictionary<string, string>> CountryIndex = new(() =>
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, names) in Countries)
        {
            foreach (var name in names)
            {
                index.TryAdd(Normalize(name), code);
            }
        }
        return index;
    });

    private static Dictionary<string, string[]> Index(string[][] rows)
    {
        var index = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            foreach (var name in row)
            {
                index.TryAdd(Normalize(name), row);
            }
        }
        return index;
    }

    [GeneratedRegex(@"^(.*?)\s*\(([^()]*)\)$")]
    private static partial Regex Suffixed();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>Lower case without diacritics and with single spaces: the key names are compared by.</summary>
    public static string Normalize(string? text)
    {
        // Marks off first ("İ" is I + a dot above), then lower case.
        var lower = (text ?? "").Trim().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }
            builder.Append(ch switch
            {
                'ı' => "i",
                'ß' => "ss",
                'ø' => "o",
                'đ' => "d",
                'ł' => "l",
                _ => ch.ToString()
            });
        }
        return Spaces().Replace(builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant(), " ");
    }

    private static int Slot(string? language)
    {
        var index = Array.IndexOf(Languages, language ?? Loc.I.Language);
        return index < 0 ? 2 : index;
    }

    /// <summary>Country name in <paramref name="language"/> (the app language by default); English from the OS for the rest, null without a code.</summary>
    public static string? CountryName(string? code, string? language = null)
    {
        code = (code ?? "").Trim().ToUpperInvariant();
        if (code == "UK") code = "GB";
        if (code.Length != 2)
        {
            return null;
        }
        if (Countries.TryGetValue(code, out var names))
        {
            return names[Slot(language)];
        }
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return code;
        }
    }

    /// <summary>A country code for a country name in any of the three languages; null when unknown.</summary>
    public static string? CountryCodeForName(string? name) =>
        CountryIndex.Value.TryGetValue(Normalize(name), out var code) ? code : null;

    /// <summary>A city in <paramref name="language"/> (the app language by default); unknown text as it is.</summary>
    public static string City(string? raw, string? language = null)
    {
        var text = raw?.Trim() ?? "";
        return text.Length > 0 && CityIndex.Value.TryGetValue(Normalize(text), out var row) ? row[Slot(language)] : text;
    }

    /// <summary>
    /// A display name: "Letonya (Köprü)" → "Latvia (Bridge)". The part before a trailing "(...)" is
    /// shown as the country name when it is <paramref name="countryCode"/> or any known country name;
    /// the suffix is translated when known. Otherwise the text stays as it is (a city name is translated).
    /// </summary>
    public static string DisplayName(string? raw, string? countryCode = null, string? language = null)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            return text;
        }
        var match = Suffixed().Match(text);
        var head = (match.Success ? match.Groups[1].Value : text).Trim();
        var suffix = match.Success ? match.Groups[2].Value.Trim() : null;
        string localizedHead;
        if (CountryCodeOf(head, countryCode) is { } code)
        {
            localizedHead = CountryName(code, language) ?? head;
        }
        else if (CityIndex.Value.TryGetValue(Normalize(head), out var cityRow))
        {
            localizedHead = cityRow[Slot(language)];
        }
        else if (!match.Success)
        {
            return text;
        }
        else
        {
            localizedHead = head;
        }
        if (suffix == null)
        {
            return localizedHead;
        }
        if (head.Length == 0)
        {
            return text;
        }
        var localizedSuffix = SuffixIndex.Value.TryGetValue(Normalize(suffix), out var suffixRow) ? suffixRow[Slot(language)] : suffix;
        return $"{localizedHead} ({localizedSuffix})";
    }

    /// <summary>A multihop route name, "Varşova → Frankfurt": each end as a city or a display name.</summary>
    public static string Route(string? raw, string? language = null)
    {
        var text = raw?.Trim() ?? "";
        if (!text.Contains('→'))
        {
            return DisplayName(text, null, language);
        }
        return string.Join(" → ", text.Split('→').Select(part => DisplayName(part, null, language)));
    }

    /// <summary>
    /// The text in every language the tables know (itself included), for search: "London" finds a
    /// server the panel named "Londra".
    /// </summary>
    public static IEnumerable<string> Variants(string? raw, string? countryCode = null)
    {
        var text = raw?.Trim() ?? "";
        if (text.Length == 0)
        {
            yield break;
        }
        yield return text;
        foreach (var language in Languages)
        {
            yield return DisplayName(text, countryCode, language);
        }
        if (CityIndex.Value.TryGetValue(Normalize(text), out var row))
        {
            foreach (var name in row)
            {
                yield return name;
            }
        }
    }

    /// <summary>True when <paramref name="query"/> is part of any of the texts, case- and diacritic-insensitively.</summary>
    public static bool Matches(string query, IEnumerable<string?> texts)
    {
        var needle = Normalize(query);
        return needle.Length == 0 || texts.Any(text => text != null && Normalize(text).Contains(needle, StringComparison.Ordinal));
    }

    private static string? CountryCodeOf(string name, string? countryCode)
    {
        if (name.Length == 0)
        {
            return null;
        }
        var code = (countryCode ?? "").Trim().ToUpperInvariant();
        if (code.Length == 2 && Normalize(name) == Normalize(code))
        {
            return code;
        }
        if (name.Length == 2 && Countries.ContainsKey(name.ToUpperInvariant()))
        {
            return name.ToUpperInvariant();
        }
        return CountryCodeForName(name);
    }
}
