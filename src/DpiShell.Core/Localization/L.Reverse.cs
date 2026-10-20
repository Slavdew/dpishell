using System.Text.RegularExpressions;

namespace DpiShell.Core.Localization;

/// <summary>
/// Обратный перевод готовых русских фраз ядра: то, что записано текстом (журнал изменений, история проверок) или пришло от помощника на русском,
/// показывается на языке окна. Работает по шаблонам ключей области <c>core.</c> из русского файла: «{code}: редирект на {host}» узнаётся в
/// «301: редирект на www.example.com» и собирается заново из текущего языка. Незнакомый текст возвращается как есть.
/// </summary>
public static partial class L
{
    const string ReversePrefix = "core.";

    static (Dictionary<string, string> Exact, List<(Regex Rx, string Key, string[] Names)> Templates)? _reverse;

    static (Dictionary<string, string> Exact, List<(Regex Rx, string Key, string[] Names)> Templates) Reverse()
    {
        if (_reverse is { } ready) return ready;
        var exact = new Dictionary<string, string>(StringComparer.Ordinal);
        var templates = new List<(Regex, string, string[], int)>();
        foreach (var (key, e) in BuiltIn[Ru].Strings)
        {
            if (!key.StartsWith(ReversePrefix, StringComparison.Ordinal) || e.Text is not { Length: > 0 } text) continue;
            var names = Placeholders(text).Distinct().ToArray();
            if (names.Length == 0) { exact.TryAdd(text, key); continue; }
            var pattern = "^" + Placeholder.Replace(Regex.Escape(text).Replace(@"\{", "{").Replace(@"\}", "}"),
                m => $"(?<{m.Groups[1].Value}>.*?)") + "$";
            // Длинный шаблон точнее короткого: сначала те, где больше постоянного текста.
            templates.Add((new Regex(pattern, RegexOptions.Compiled | RegexOptions.Singleline), key, names, Placeholder.Replace(text, "").Length));
        }
        return (_reverse = (exact, [.. templates.OrderByDescending(t => t.Item4).Select(t => (t.Item1, t.Item2, t.Item3))])).Value;
    }

    /// <summary>Русская фраза ядра на языке окна. Русский язык и незнакомый текст — без изменений.</summary>
    public static string FromRu(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if ((_scoped.Value ?? _current) == Ru) return text;
        var (exact, templates) = Reverse();
        if (exact.TryGetValue(text, out var k)) return T(k);
        foreach (var (rx, key, names) in templates)
            if (rx.Match(text) is { Success: true } m)
                return T(key, names.ToDictionary(n => n, n => (object?)FromRu(m.Groups[n].Value)));
        return text;
    }
}
