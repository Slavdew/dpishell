using System.Text.RegularExpressions;

namespace DpiShell.Core.Localization;

/// <summary>
/// Перевод готовых фраз на язык окна. Сообщение, собранное на одном языке (ответ помощника, запись журнала, сообщение последнего
/// действия, показанное до смены языка), узнаётся по шаблону ключа любого встроенного языка и собирается заново из текущего:
/// «301: редирект на www.example.com» → “301: redirect to www.example.com”, и обратно. Незнакомый текст возвращается как есть.
/// Короткие шаблоны (меньше <see cref="MinLiteral"/> постоянных знаков) не берутся — чтобы «{ms} мс» не цеплялось к чужому тексту.
/// </summary>
public static partial class L
{
    /// <summary>Сколько знаков постоянного текста должно быть в шаблоне с подстановками, чтобы по нему узнавать фразу.</summary>
    const int MinLiteral = 14;

    /// <summary>Ключи, которыми пользуется ядро (и помощник): их фразы узнаются и по коротким шаблонам.</summary>
    static readonly string[] CorePrefixes = ["core.", "apps.core.", "diag.conflict.", "diag.find.", "proxy.chk.", "proxy.out.", "proxy.problem.", "sites.adv."];

    sealed record Template(Regex Rx, string Key, string[] Names, bool Plural, int Literal);

    /// <summary>Для собственных сообщений окна шаблоны берутся и короткие: фразы окна известны, чужого текста среди них нет.</summary>
    const int MinLiteralOwn = 5;

    sealed record Matcher(Dictionary<string, string> Exact, List<Template> Templates);

    static readonly Dictionary<string, Matcher> _matchers = new(StringComparer.OrdinalIgnoreCase);

    static Matcher MatcherFor(string lang, bool own)
    {
        lock (_matchers)
        {
            var cacheKey = own ? lang + "|own" : lang;
            if (_matchers.TryGetValue(cacheKey, out var ready)) return ready;
            var exact = new Dictionary<string, string>(StringComparer.Ordinal);
            var templates = new List<(Template T, int Literal)>();
            foreach (var (key, e) in BuiltIn[lang].Strings)
            {
                foreach (var text in e.Texts)
                {
                    if (text.Length == 0) continue;
                    var names = Placeholders(text).Distinct().ToArray();
                    bool plural = e.Forms is not null;
                    if (names.Length == 0 && !plural) { exact.TryAdd(text, key); continue; }
                    var literal = Placeholder.Replace(text, "").Length;
                    if (literal < (own ? MinLiteralOwn : MinLiteral) && !(CorePrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)) && literal >= 6)) continue;
                    var pattern = "^" + Placeholder.Replace(Regex.Escape(text).Replace(@"\{", "{").Replace(@"\}", "}"),
                        m => $"(?<{m.Groups[1].Value}>.*?)") + "$";
                    templates.Add((new Template(new Regex(pattern, RegexOptions.Singleline | RegexOptions.CultureInvariant), key, names, plural, literal), literal));
                }
            }
            // Длинный шаблон точнее короткого: сначала те, где больше постоянного текста.
            return _matchers[cacheKey] = new Matcher(exact, [.. templates.OrderByDescending(t => t.Literal).Select(t => t.T)]);
        }
    }

    /// <summary>Фраза (на любом встроенном языке) — на языке окна; своя или незнакомая — без изменений.</summary>
    public static string Relocalize(string? text, bool own = false)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var current = _scoped.Value ?? _current;
        // Лучшее совпадение среди встроенных языков — то, где больше постоянного текста (общий шаблон «{a} — {b}.» не перебивает точный).
        Hit? best = null; string? bestLang = null;
        foreach (var lang in BuiltIn.Keys)
            if (Matches(MatcherFor(lang, own), text) is { } hit && (best is null || hit.Literal > best.Literal)) { best = hit; bestLang = lang; }
        if (best is not null) return string.Equals(bestLang, current, StringComparison.OrdinalIgnoreCase) ? text : Render(best);
        // Список из нескольких фраз через запятую («настройки прокси, прокси для служб (WinHTTP)») — по частям.
        if (text.Contains(", "))
        {
            var parts = text.Split(", ");
            var translated = parts.Select(p => Relocalize(p, own)).ToArray();
            if (!translated.SequenceEqual(parts)) return string.Join(", ", translated);
        }
        return text;
    }

    /// <summary>То же, что <see cref="Relocalize"/>; прежнее имя — русская фраза на языке окна.</summary>
    public static string FromRu(string? text) => Relocalize(text);

    sealed record Hit(string Key, string[]? Names, Dictionary<string, string>? Values, bool Plural, int Literal);

    static string Render(Hit h)
    {
        if (h.Names is null) return T(h.Key);
        var args = h.Names.Where(n => n != "n" || !h.Plural).ToDictionary(n => n, n => (object?)Relocalize(h.Values![n]));
        if (h.Plural && h.Values!.TryGetValue("n", out var n) && long.TryParse(n, out var count)) return Plural(h.Key, count, args);
        return T(h.Key, args);
    }

    static Hit? Matches(Matcher m, string text)
    {
        if (m.Exact.TryGetValue(text, out var k)) return new(k, null, null, false, int.MaxValue);
        foreach (var t in m.Templates)
            if (t.Rx.Match(text) is { Success: true } match)
                return new(t.Key, t.Names, t.Names.ToDictionary(n => n, n => match.Groups[n].Value), t.Plural, t.Literal);
        return null;
    }
}
