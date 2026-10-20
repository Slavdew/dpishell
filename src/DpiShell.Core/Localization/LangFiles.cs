using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DpiShell.Core.Localization;

/// <summary>Язык: код, название на своём языке, флаг (ISO 3166, может не быть), автор и версия перевода.</summary>
public sealed record LangInfo(string Code, string Name, string? Flag, string Author = "", string Version = "", bool BuiltIn = true, string? File = null);

/// <summary>Строка языка: текст или формы числа (one/few/many/other…).</summary>
public sealed record LangEntry(string? Text, IReadOnlyDictionary<string, string>? Forms)
{
    /// <summary>Все тексты записи (одна строка или все формы).</summary>
    public IEnumerable<string> Texts => Text is not null ? [Text] : Forms!.Values;
    /// <summary>Подстановки записи; у форм числа — объединение, без {n} (он подставляется сам).</summary>
    public HashSet<string> Placeholders => Text is not null
        ? L.Placeholders(Text).ToHashSet()
        : Forms!.Values.SelectMany(L.Placeholders).Where(p => p != "n").ToHashSet();
}

/// <summary>Язык целиком: сведения и строки (порядок ключей — как в файлах).</summary>
public sealed class LangPack(LangInfo info)
{
    public LangInfo Info { get; set; } = info;
    public Dictionary<string, LangEntry> Strings { get; } = new(StringComparer.Ordinal);
    /// <summary>Ошибки сборки встроенного языка (повтор ключа, неверное значение) — тест требует пустой список.</summary>
    public List<string> Errors { get; } = [];
}

/// <summary>Чтение и проверка языковых файлов; шаблон перевода.</summary>
public static partial class LangFiles
{
    public static readonly string[] Forms = ["zero", "one", "two", "few", "many", "other"];

    static readonly JsonDocumentOptions Options = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex CodeRx();

    [GeneratedRegex(@"^[A-Za-z]{2}$")]
    private static partial Regex FlagRx();

    /// <summary>Встроенный язык из файлов областей. _meta — в common.json. Повтор ключа в двух файлах — ошибка.</summary>
    public static LangPack MergeBuiltIn(string code, IEnumerable<(string Area, string Json)> files)
    {
        var pack = new LangPack(new LangInfo(code, code, null));
        var where = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (area, json) in files.OrderBy(f => f.Area == "common" ? 0 : 1).ThenBy(f => f.Area, StringComparer.Ordinal))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json, Options); }
            catch (JsonException e) { pack.Errors.Add($"{code}/{area}.json: {e.Message}"); continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { pack.Errors.Add($"{code}/{area}.json: не объект"); continue; }
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    if (p.Name == "_meta")
                    {
                        pack.Info = new LangInfo(code, Str(p.Value, "language") ?? code, Str(p.Value, "flag")?.ToLowerInvariant(),
                            Str(p.Value, "author") ?? "", Str(p.Value, "version") ?? "");
                        continue;
                    }
                    if (p.Name.StartsWith('_')) continue;
                    if (where.TryGetValue(p.Name, out var first)) { pack.Errors.Add($"{code}: ключ {p.Name} — и в {first}.json, и в {area}.json"); continue; }
                    where[p.Name] = area;
                    if (Entry(p.Value) is { } e) pack.Strings[p.Name] = e;
                    else pack.Errors.Add($"{code}/{area}.json: {p.Name} — не строка и не формы числа");
                }
            }
        }
        return pack;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static LangEntry? Entry(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.String) return new LangEntry(v.GetString()!, null);
        if (v.ValueKind != JsonValueKind.Object) return null;
        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in v.EnumerateObject())
        {
            if (!Forms.Contains(f.Name) || f.Value.ValueKind != JsonValueKind.String) return null;
            forms[f.Name] = f.Value.GetString()!;
        }
        return forms.ContainsKey("other") ? new LangEntry(null, forms) : null;
    }

    /// <summary>
    /// Пользовательский перевод: один файл, только данные. Неверный файл — null и причина в problems; неверные строки
    /// (подстановки не как в ru, длиннее <see cref="L.MaxLength"/>, не тот вид) не берутся — вместо них запасной язык.
    /// </summary>
    public static LangPack? LoadUser(string file, LangPack ru, IEnumerable<string> builtIn, List<string> problems)
    {
        var name = Path.GetFileName(file);
        string json;
        try
        {
            if (new FileInfo(file).Length > L.MaxFileBytes) { problems.Add(L.T("common.lang.problem.big", new { file = name })); return null; }
            json = File.ReadAllText(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { problems.Add(L.T("common.lang.problem.read", new { file = name, error = e.Message })); return null; }
        return ParseUser(json, name, ru, builtIn, problems, file);
    }

    /// <summary>Разбор текста пользовательского перевода (см. <see cref="LoadUser"/>).</summary>
    public static LangPack? ParseUser(string json, string name, LangPack ru, IEnumerable<string> builtIn, List<string> problems, string? file = null)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, Options); }
        catch (JsonException e) { problems.Add(L.T("common.lang.problem.json", new { file = name, error = e.Message })); return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("_meta", out var meta)
                || Str(meta, "language") is not { Length: > 0 } language || Str(meta, "code") is not { } code)
            {
                problems.Add(L.T("common.lang.problem.meta", new { file = name }));
                return null;
            }
            if (!CodeRx().IsMatch(code)) { problems.Add(L.T("common.lang.problem.code", new { file = name, code })); return null; }
            if (builtIn.Contains(code, StringComparer.OrdinalIgnoreCase)) { problems.Add(L.T("common.lang.problem.builtin", new { file = name, code })); return null; }
            var flag = Str(meta, "flag") is { } f && FlagRx().IsMatch(f) ? f.ToLowerInvariant() : null;
            var pack = new LangPack(new LangInfo(code, language.Length > 60 ? language[..60] : language, flag,
                Str(meta, "author") ?? "", Str(meta, "version") ?? "", BuiltIn: false, File: file));
            var need = PluralRules.FormsOf(code);
            var bad = new List<string>();
            foreach (var p in root.EnumerateObject())
            {
                if (p.Name.StartsWith('_') || !ru.Strings.TryGetValue(p.Name, out var source)) continue;
                var e = Entry(p.Value);
                bool ok = e is not null
                    && (e.Text is null) == (source.Text is null)
                    && e.Texts.All(t => t.Length <= L.MaxLength)
                    && e.Placeholders.SetEquals(source.Placeholders)
                    && (e.Forms is null || need.All(e.Forms.ContainsKey));
                if (ok) pack.Strings[p.Name] = e!;
                else bad.Add(p.Name);
            }
            if (bad.Count > 0)
                problems.Add(L.Plural("common.lang.problem.strings", bad.Count, new { file = name, keys = string.Join(", ", bad.Take(5)) + (bad.Count > 5 ? ", …" : "") }));
            return pack;
        }
    }

    /// <summary>
    /// Шаблон перевода: все ключи; значение — английский текст (нет — русский), рядом «_ru.ключ» — русский для сверки.
    /// Ключи с «_» программа не читает, поэтому файл после правки значений и _meta — готовый перевод.
    /// </summary>
    public static string Template(LangPack ru, LangPack? en, string version)
    {
        var o = new JsonObject
        {
            ["_meta"] = new JsonObject { ["language"] = "", ["code"] = "", ["author"] = "", ["version"] = version, ["flag"] = "" },
            ["_help"] = "Fill _meta (language: name in that language, code: ISO 639-1 like \"de\", flag: ISO 3166 country like \"de\"). " +
                        "Translate the values; keep {placeholders} as they are. Keys starting with _ are ignored. Save as <code>.json into the lang folder.",
        };
        foreach (var (key, r) in ru.Strings)
        {
            o["_ru." + key] = Node(r);
            o[key] = Node(en is not null && en.Strings.TryGetValue(key, out var e) ? e : r);
        }
        return o.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    static JsonNode Node(LangEntry e)
    {
        if (e.Text is not null) return JsonValue.Create(e.Text)!;
        var o = new JsonObject();
        foreach (var (k, v) in e.Forms!) o[k] = v;
        return o;
    }
}

/// <summary>Формы числа по CLDR: для какого числа какая форма (целые числа).</summary>
public static class PluralRules
{
    static string Base(string code) => code.Split('-')[0].ToLowerInvariant();

    public static string Select(string code, long n)
    {
        n = Math.Abs(n);
        long d = n % 10, h = n % 100;
        switch (Base(code))
        {
            case "ru" or "uk" or "be":
                return d == 1 && h != 11 ? "one" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "few" : "many";
            case "pl":
                return n == 1 ? "one" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "few" : "many";
            case "cs" or "sk":
                return n == 1 ? "one" : n is >= 2 and <= 4 ? "few" : "other";
            case "fr" or "pt":
                return n is 0 or 1 ? "one" : "other";
            case "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms":
                return "other";
            default:
                return n == 1 ? "one" : "other";
        }
    }

    /// <summary>Какие формы нужны языку (для проверки пользовательского файла).</summary>
    public static string[] FormsOf(string code) => Base(code) switch
    {
        "ru" or "uk" or "be" or "pl" => ["one", "few", "many", "other"],
        "cs" or "sk" => ["one", "few", "other"],
        "ja" or "zh" or "ko" or "vi" or "th" or "id" or "ms" => ["other"],
        _ => ["one", "other"],
    };
}
