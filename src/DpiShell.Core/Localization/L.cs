using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace DpiShell.Core.Localization;

/// <summary>
/// Строки интерфейса (docs/localization.md). Встроенные языки ru (основной, источник правды) и en — ресурсы сборки,
/// каждый собран из файлов Lang/&lt;код&gt;/&lt;область&gt;.json; пользовательский перевод — один файл &lt;код&gt;.json в папке lang.
/// Цепочка запасных: выбранный → en → ru → сам ключ (пустой строки не бывает).
/// Лежит в Core: строки ядра (ошибки, причины) тоже идут через L; помощник (служба) пока всегда на русском — языка окна не знает.
/// Смена языка — сразу: окно подписано на <see cref="Changed"/> (привязки {l:T} и модели).
/// </summary>
public static class L
{
    public const string Ru = "ru";
    public const string En = "en";
    /// <summary>Длина строки пользовательского перевода (длиннее — строка не берётся).</summary>
    public const int MaxLength = 2000;
    /// <summary>Пользовательский файл больше этого — не читается.</summary>
    public const long MaxFileBytes = 2 * 1024 * 1024;

    static readonly object Gate = new();
    static Dictionary<string, LangPack>? _builtIn;
    static readonly Dictionary<string, LangPack> _user = new(StringComparer.OrdinalIgnoreCase);
    static readonly List<string> _problems = [];
    static LangPack[] _chain = [];
    static string _current = Ru;
    static CultureInfo _culture = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Язык сменился (уже на потоке, вызвавшем <see cref="Set"/>, — в окне это поток окна).</summary>
    public static event Action? Changed;

    /// <summary>Код выбранного языка.</summary>
    public static string Current => _current;

    /// <summary>Культура выбранного языка: даты, время, числа в окне.</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>Почему пользовательские файлы перевода не взяты (целиком или частью) — для «Диагностики».</summary>
    public static IReadOnlyList<string> Problems { get { lock (Gate) return _problems.ToArray(); } }

    /// <summary>Встроенные языки: код → пакет (сливается из всех файлов области).</summary>
    public static IReadOnlyDictionary<string, LangPack> BuiltIn => _builtIn ??= LoadBuiltIn();

    /// <summary>Встроенные и найденные пользовательские языки — для выбора.</summary>
    public static IReadOnlyList<LangInfo> Available
    {
        get
        {
            lock (Gate)
                return BuiltIn.Values.Select(p => p.Info).OrderBy(i => i.Code == Ru ? 0 : 1)
                    .Concat(_user.Values.Select(p => p.Info).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)).ToList();
        }
    }

    /// <summary>Язык по умолчанию: русский для ru/uk/be/kk Windows, иначе английский.</summary>
    public static string DefaultFor(CultureInfo ui) => ui.TwoLetterISOLanguageName is "ru" or "uk" or "be" or "kk" ? Ru : En;

    /// <summary>Сменить язык (неизвестный код — по умолчанию для Windows). Поднимает <see cref="Changed"/>, если язык другой.</summary>
    public static void Set(string? code)
    {
        bool changed;
        lock (Gate)
        {
            if (code is null || (!BuiltIn.ContainsKey(code) && !_user.ContainsKey(code))) code = DefaultFor(CultureInfo.CurrentUICulture);
            code = BuiltIn.ContainsKey(code) ? code : _user[code].Info.Code;
            changed = code != _current || _chain.Length == 0;
            _current = code;
            _chain = BuildChain(code);
            _culture = CultureFor(code);
        }
        if (changed) Changed?.Invoke();
    }

    static LangPack[] BuildChain(string code)
    {
        var list = new List<LangPack>();
        if (_user.TryGetValue(code, out var u)) list.Add(u);
        foreach (var c in new[] { code, En, Ru })
            if (BuiltIn.TryGetValue(c, out var b) && !list.Contains(b)) list.Add(b);
        return [.. list];
    }

    static CultureInfo CultureFor(string code)
    {
        // Язык Windows тот же — его культура (en-GB у англичанина, en-US у американца); иначе — основная для языка.
        if (string.Equals(CultureInfo.CurrentCulture.TwoLetterISOLanguageName, code, StringComparison.OrdinalIgnoreCase)
            && !(code == Ru && CultureInfo.CurrentCulture.Name == "ru-RU"))   // ru-RU — без пользовательских правок формата: одинаково в тестах и у всех
            return CultureInfo.CurrentCulture;
        try { return CultureInfo.CreateSpecificCulture(code); }
        catch (CultureNotFoundException) { return CultureInfo.InvariantCulture; }
    }

    static LangPack[] Chain
    {
        get
        {
            var c = _chain;
            if (c.Length > 0) return c;
            lock (Gate) return _chain = BuildChain(_current);
        }
    }

    // ---------- строки ----------

    /// <summary>Строка по ключу: выбранный язык → en → ru → сам ключ.</summary>
    public static string T(string key) => T(key, null);

    /// <summary>Строка с подстановками {name}: args — анонимный объект или словарь.</summary>
    public static string T(string key, object? args)
    {
        foreach (var p in Chain)
            if (p.Strings.TryGetValue(key, out var e))
                return Format(e.Text ?? e.Forms!.GetValueOrDefault("other") ?? e.Forms!.Values.First(), args);
        return key;
    }

    /// <summary>Строка с формой числа по правилам языка (CLDR): {n} подставляется само.</summary>
    public static string Plural(string key, long n, object? args = null)
    {
        var values = Args(args);
        values["n"] = n;
        foreach (var p in Chain)
            if (p.Strings.TryGetValue(key, out var e))
            {
                if (e.Text is { } t) return Format(t, values);
                var form = PluralRules.Select(p.Info.Code, n);
                return Format(e.Forms!.GetValueOrDefault(form) ?? e.Forms!.GetValueOrDefault("other") ?? e.Forms!.Values.First(), values);
            }
        return key;
    }

    /// <summary>Есть ли строка (в любом языке цепочки).</summary>
    public static bool Has(string key) => Chain.Any(p => p.Strings.ContainsKey(key));

    static readonly Regex Placeholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);

    /// <summary>Имена подстановок в строке.</summary>
    public static IEnumerable<string> Placeholders(string text) => Placeholder.Matches(text).Select(m => m.Groups[1].Value);

    static Dictionary<string, object?> Args(object? args)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        switch (args)
        {
            case null: break;
            case IDictionary<string, object?> dict: foreach (var (k, v) in dict) d[k] = v; break;
            case IDictionary<string, string> sd: foreach (var (k, v) in sd) d[k] = v; break;
            default:
                foreach (var p in args.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.GetIndexParameters().Length == 0) d[p.Name] = p.GetValue(args);
                break;
        }
        return d;
    }

    static string Format(string text, object? args)
    {
        if (args is null || text.IndexOf('{') < 0) return text;
        var values = args as Dictionary<string, object?> ?? Args(args);
        return Placeholder.Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v)
            ? v is IFormattable f ? f.ToString(null, _culture) : v?.ToString() ?? ""
            : m.Value);
    }

    // ---------- даты и время ----------

    /// <summary>Дата коротко: 20.10.2026 / 10/20/2026.</summary>
    public static string Date(DateTime d) => d.ToString(_culture.DateTimeFormat.ShortDatePattern, _culture);
    /// <summary>Время: 14:05 / 2:05 PM.</summary>
    public static string Time(DateTime d) => d.ToString(_culture.DateTimeFormat.ShortTimePattern, _culture);
    /// <summary>Число и месяц без года: 20.10 / 10/20.</summary>
    public static string DayMonth(DateTime d) => d.ToString(DayMonthPattern(_culture), _culture);
    /// <summary>Дата словами: 20 октября 2026 / 20 October 2026.</summary>
    public static string LongDate(DateTime d) => d.ToString("d MMMM yyyy", _culture);

    static string DayMonthPattern(CultureInfo c)
    {
        // dd.MM.yyyy → dd.MM, M/d/yyyy → M/d, yyyy-MM-dd → MM-dd: год и один разделитель рядом с ним.
        var s = Regex.Replace(c.DateTimeFormat.ShortDatePattern, @"^y+[^dMy]*|[^dMy]*y+$", "");
        return s.Contains('d') && s.Contains('M') ? s : "dd.MM";
    }

    // ---------- загрузка ----------

    static Dictionary<string, LangPack> LoadBuiltIn()
    {
        var asm = typeof(L).Assembly;
        const string prefix = "DpiShell.Core.Lang.";
        var files = new Dictionary<string, List<(string Area, string Json)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal)))
        {
            var rest = name[prefix.Length..^".json".Length];   // ru.common
            var dot = rest.IndexOf('.');
            if (dot <= 0) continue;
            using var s = asm.GetManifestResourceStream(name)!;
            using var r = new StreamReader(s);
            if (!files.TryGetValue(rest[..dot], out var list)) files[rest[..dot]] = list = [];
            list.Add((rest[(dot + 1)..], r.ReadToEnd()));
        }
        var result = new Dictionary<string, LangPack>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, list) in files)
            result[code] = LangFiles.MergeBuiltIn(code, list);
        return result;
    }

    /// <summary>Папки пользовательских переводов: lang рядом с программой (portable) и %AppData%\DpiShell\lang.</summary>
    public static IReadOnlyList<string> UserDirs =>
    [
        Path.Combine(AppContext.BaseDirectory, "lang"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DpiShell", "lang"),
    ];

    /// <summary>
    /// Найти пользовательские переводы. Битый или неверный файл пропускается, причина — в <see cref="Problems"/>.
    /// Выбранный язык остаётся (пропал его файл — по умолчанию).
    /// </summary>
    public static void LoadUser(IEnumerable<string> dirs)
    {
        lock (Gate)
        {
            _user.Clear();
            _problems.Clear();
            var ru = BuiltIn[Ru];
            foreach (var dir in dirs)
            {
                string[] found;
                try { found = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : []; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _problems.Add($"{dir}: {e.Message}"); continue; }
                foreach (var file in found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var pack = LangFiles.LoadUser(file, ru, BuiltIn.Keys, _problems);
                    if (pack is null) continue;
                    if (_user.ContainsKey(pack.Info.Code)) { _problems.Add(T("common.lang.problem.duplicate", new { file = Path.GetFileName(file), code = pack.Info.Code })); continue; }
                    _user[pack.Info.Code] = pack;
                }
            }
        }
        Set(_user.ContainsKey(_current) || BuiltIn.ContainsKey(_current) ? _current : null);
    }

    /// <summary>Только для тестов: вернуть русский без пользовательских файлов.</summary>
    public static void ResetForTests()
    {
        lock (Gate) { _user.Clear(); _problems.Clear(); }
        Set(Ru);
    }
}
