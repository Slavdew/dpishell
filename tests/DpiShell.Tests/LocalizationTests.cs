using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DpiShell.App;

namespace DpiShell.Tests;

/// <summary>Тесты, которые меняют язык (общее состояние L), — не параллельно с остальными: те ждут русский текст.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LangCollection
{
    public const string Name = "lang";
}

/// <summary>
/// Каркас локализации (docs/localization.md): встроенные ru и en, ключи из кода и разметки, подстановки, формы числа,
/// пользовательский перевод (битый файл не ломает программу), цепочка запасных, шаблон перевода.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LocalizationTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static string Src => Path.Combine(Directory.GetParent(Repo.Data)!.FullName, "src");
    /// <summary>Языковые файлы — в Core (строки ядра тоже через L).</summary>
    static string LangDir => Path.Combine(Src, "DpiShell.Core", "Lang");
    /// <summary>Где ищутся ключи: окно и ядро.</summary>
    static readonly string[] CodeDirs = ["DpiShell.App", "DpiShell.Core"];

    /// <summary>Области: файлы Lang/&lt;язык&gt;/&lt;область&gt;.json (этап 2 — каждая вкладка в своём файле).</summary>
    static readonly string[] Areas = ["common", "home", "select", "sites", "apps", "hosts", "dns", "proxy", "net", "diag", "router", "settings", "about", "info", "shell", "core", "export", "web"];

    static LangPack Ru => L.BuiltIn[L.Ru];
    static LangPack En => L.BuiltIn[L.En];

    [Fact]
    public void BuiltIn_ru_and_en_load_without_errors()
    {
        Assert.Empty(Ru.Errors);
        Assert.Empty(En.Errors);
        Assert.Equal("Русский", Ru.Info.Name);
        Assert.Equal("English", En.Info.Name);
        Assert.Equal("ru", Ru.Info.Flag);
        Assert.Equal("gb", En.Info.Flag);
    }

    /// <summary>
    /// Фразы роутера (область web) — источник один: router/luci-app-dpishell/lang-src/web.json (значение — [ru, en]).
    /// Окно держит только нужные ему ключи; тексты обязаны совпадать дословно, иначе окно и страница роутера разойдутся.
    /// </summary>
    [Fact]
    public void Web_area_matches_router_source()
    {
        var src = Path.Combine(Directory.GetParent(Src)!.FullName, "router", "luci-app-dpishell", "lang-src", "web.json");
        using var router = JsonDocument.Parse(File.ReadAllText(src));
        foreach (var (lang, i) in new[] { ("ru", 0), ("en", 1) })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LangDir, lang, "web.json")));
            foreach (var p in doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
            {
                Assert.True(router.RootElement.TryGetProperty(p.Name, out var pair), $"{lang}/web.json: ключа {p.Name} нет в {src}");
                Assert.Equal(pair[i].GetRawText(), p.Value.GetRawText());
            }
        }
    }

    [Fact]
    public void Area_files_are_the_same_in_ru_and_en_and_keys_start_with_area()
    {
        foreach (var lang in new[] { "ru", "en" })
        {
            var files = Directory.GetFiles(Path.Combine(LangDir, lang), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToArray();
            Assert.Equal(Areas.Order().ToArray(), files);
            foreach (var area in files)
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(LangDir, lang, area + ".json")));
                foreach (var p in doc.RootElement.EnumerateObject().Where(p => !p.Name.StartsWith('_')))
                {
                    Assert.True(p.Name.StartsWith(area + "."), $"{lang}/{area}.json: ключ {p.Name} не начинается с «{area}.»");
                    Assert.Matches("^[a-z][a-z0-9_]*(\\.[a-z0-9_]+)+$", p.Name);
                }
            }
        }
    }

    [Fact]
    public void Duplicate_key_in_two_area_files_is_an_error()
    {
        var pack = LangFiles.MergeBuiltIn("ru", [("home", """{ "home.a": "1" }"""), ("sites", """{ "home.a": "2" }""")]);
        Assert.Single(pack.Errors);
        Assert.Contains("home.a", pack.Errors[0]);
    }

    [Fact]
    public void Ru_and_en_have_the_same_keys_placeholders_and_plural_forms()
    {
        Assert.Empty(Ru.Strings.Keys.Except(En.Strings.Keys));
        Assert.Empty(En.Strings.Keys.Except(Ru.Strings.Keys));
        foreach (var (key, ru) in Ru.Strings)
        {
            var en = En.Strings[key];
            Assert.True((ru.Text is null) == (en.Text is null), $"{key}: в одном языке строка, в другом — формы числа");
            Assert.True(ru.Placeholders.SetEquals(en.Placeholders), $"{key}: подстановки ru {{{string.Join(",", ru.Placeholders)}}} ≠ en {{{string.Join(",", en.Placeholders)}}}");
            if (ru.Forms is not null)
            {
                Assert.All(PluralRules.FormsOf("ru"), f => Assert.True(ru.Forms.ContainsKey(f), $"{key}: в ru нет формы {f}"));
                Assert.All(PluralRules.FormsOf("en"), f => Assert.True(en.Forms!.ContainsKey(f), $"{key}: в en нет формы {f}"));
            }
            Assert.All(ru.Texts.Concat(en.Texts), t => Assert.False(string.IsNullOrWhiteSpace(t), $"{key}: пустая строка"));
        }
    }

    // Ключи в коде: строки-литералы "<область>.<что>" внутри вызова L.T( / L.Plural( (до конца оператора — так ловятся и ключи
    // в тернарных выражениях), константы InfoTexts; в разметке — {l:T ключ}. Имена файлов вроде "core.zip" вне L.T не считаются ключами.
    static readonly Regex CodeKey = new("\"((?:" + string.Join("|", Areas) + ")\\.[a-z0-9_.]+)\"", RegexOptions.Compiled);
    static readonly Regex CallText = new(@"\bL\.(?:T|Plural)\([^;]*", RegexOptions.Compiled);
    static readonly Regex XamlKey = new(@"\{l:T\s+([^}\s,]+)", RegexOptions.Compiled);

    /// <summary>Ключи, которые код собирает из частей: заголовки (i) — тема + ".title" (InfoTexts.Title); «Безопасность роутера» —
    /// router.security.&lt;id&gt;[.&lt;detail&gt;|.fix] по ответу security_check (RouterSecurity.Rows).</summary>
    static bool Composed(string key) => key.StartsWith("info.") && key.EndsWith(".title") && Ru.Strings.ContainsKey(key[..^".title".Length])
        || key.StartsWith("router.security.");

    /// <summary>Ключи из кода и разметки. mentions — любой литерал-ключ (для поиска неиспользуемых: ключ мог уйти в переменную).</summary>
    static HashSet<string> UsedKeys(bool mentions = false)
    {
        var used = new HashSet<string>();
        static bool Source(string f) => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);
        foreach (var dir in CodeDirs.Select(d => Path.Combine(Src, d)))
        {
            foreach (var f in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(Source))
            {
                var text = File.ReadAllText(f);
                var scope = mentions || Path.GetFileName(f) == "InfoTexts.cs" ? [text] : CallText.Matches(text).Select(m => m.Value);
                foreach (var part in scope)
                    foreach (Match m in CodeKey.Matches(part)) used.Add(m.Groups[1].Value);
            }
            foreach (var f in Directory.GetFiles(dir, "*.xaml", SearchOption.AllDirectories).Where(Source))
                foreach (Match m in XamlKey.Matches(File.ReadAllText(f))) used.Add(m.Groups[1].Value);
        }
        return used;
    }

    [Fact]
    public void Every_key_used_in_code_and_xaml_exists_in_ru_and_en()
    {
        var used = UsedKeys();
        Assert.Contains("common.nav.home", used);   // разметка читается
        Assert.Contains("common.ready", used);      // код читается
        var missing = used.Where(k => !Ru.Strings.ContainsKey(k) || !En.Strings.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Нет в ru или en: " + string.Join(", ", missing));
    }

    [Fact]
    public void No_orphan_keys_in_ru()
    {
        var used = UsedKeys(mentions: true);
        var orphans = Ru.Strings.Keys.Where(k => !used.Contains(k) && !Composed(k)).ToList();
        Assert.True(orphans.Count == 0, "Ключи ru, которых нет в коде и разметке: " + string.Join(", ", orphans));
    }

    [Theory]
    [InlineData(1, "1 сайт"), InlineData(2, "2 сайта"), InlineData(5, "5 сайтов"), InlineData(21, "21 сайт"), InlineData(11, "11 сайтов"),
     InlineData(12, "12 сайтов"), InlineData(22, "22 сайта"), InlineData(0, "0 сайтов"), InlineData(111, "111 сайтов")]
    public void Russian_plural_rules(long n, string expected)
    {
        var forms = new Dictionary<string, string> { ["one"] = "сайт", ["few"] = "сайта", ["many"] = "сайтов" };
        Assert.Equal(expected, $"{n} {forms[PluralRules.Select("ru", n)]}");
    }

    [Fact]
    public void Plural_rules_other_languages()
    {
        Assert.Equal("one", PluralRules.Select("en", 1));
        Assert.Equal("other", PluralRules.Select("en", 0));
        Assert.Equal("other", PluralRules.Select("en", 21));
        Assert.Equal("one", PluralRules.Select("uk", 21));
        Assert.Equal("few", PluralRules.Select("be", 3));
        Assert.Equal("one", PluralRules.Select("xx", 1));      // неизвестный язык — one/other
        Assert.Equal("other", PluralRules.Select("xx", 2));
        Assert.Equal("one", PluralRules.Select("pt-BR", 0));
    }

    [Fact]
    public void Plural_and_placeholders_through_L()
    {
        L.Set(L.Ru);
        Assert.Equal("1 пункт", L.Plural("settings.changes.count", 1));
        Assert.Equal("2 пункта", L.Plural("settings.changes.count", 2));
        Assert.Equal("5 пунктов", L.Plural("settings.changes.count", 5));
        Assert.Equal("21 пункт", L.Plural("settings.changes.count", 21));
        Assert.Equal("11 пунктов", L.Plural("settings.changes.count", 11));
        Assert.Equal("Пояснение: DNS", L.T("common.info.name", new { topic = "DNS" }));
        Assert.Equal("Пояснение: DNS", L.T("common.info.name", new Dictionary<string, object?> { ["topic"] = "DNS" }));
        L.Set(L.En);
        Assert.Equal("1 item", L.Plural("settings.changes.count", 1));
        Assert.Equal("5 items", L.Plural("settings.changes.count", 5));
        Assert.Equal("Done.", L.T("common.ready"));
        Assert.Equal("no.such.key", L.T("no.such.key"));   // пустой строки не бывает: в худшем случае — сам ключ
    }

    [Fact]
    public void Default_language_follows_windows()
    {
        Assert.Equal("ru", L.DefaultFor(CultureInfo.GetCultureInfo("ru-RU")));
        Assert.Equal("ru", L.DefaultFor(CultureInfo.GetCultureInfo("uk-UA")));
        Assert.Equal("ru", L.DefaultFor(CultureInfo.GetCultureInfo("be-BY")));
        Assert.Equal("ru", L.DefaultFor(CultureInfo.GetCultureInfo("kk-KZ")));
        Assert.Equal("en", L.DefaultFor(CultureInfo.GetCultureInfo("de-DE")));
        Assert.Equal("en", L.DefaultFor(CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Dates_follow_language()
    {
        var d = new DateTime(2026, 10, 20, 14, 5, 0);
        L.Set(L.Ru);
        Assert.Equal("20.10.2026", L.Date(d));
        Assert.Equal("14:05", L.Time(d));
        Assert.Equal("20.10", L.DayMonth(d));
        Assert.Equal("20 октября 2026", L.LongDate(d));
        L.Set(L.En);
        Assert.Contains("October", L.LongDate(d));
    }

    static string UserFile(string code, string body, string? flag = null) =>
        "{ \"_meta\": { \"language\": \"Deutsch\", \"code\": \"" + code + "\", \"author\": \"t\", \"version\": \"1\"" + (flag is null ? "" : ", \"flag\": \"" + flag + "\"") + " }" + body + " }";

    [Fact]
    public void Broken_user_files_are_skipped_with_a_reason()
    {
        using var tmp = new TempDir();
        var dir = tmp.Sub("lang");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ это не json");
        File.WriteAllText(Path.Combine(dir, "nometa.json"), """{ "common.ready": "Fertig." }""");
        File.WriteAllText(Path.Combine(dir, "en.json"), UserFile("en", ""));          // встроенный код
        File.WriteAllText(Path.Combine(dir, "code.json"), UserFile("Deutsch!", ""));  // неверный код
        File.WriteAllText(Path.Combine(dir, "list.json"), "[1, 2]");
        L.LoadUser([dir, tmp.Sub("нет такой папки")]);
        Assert.Equal(5, L.Problems.Count);
        Assert.Contains(L.Problems, p => p.StartsWith("bad.json"));
        Assert.Contains(L.Problems, p => p.StartsWith("nometa.json"));
        Assert.Equal(2, L.Available.Count);   // только встроенные
        Assert.Equal("ru", L.Current);
    }

    [Fact]
    public void User_translation_with_fallback_and_rejected_lines()
    {
        using var tmp = new TempDir();
        var dir = tmp.Sub("lang");
        Directory.CreateDirectory(dir);
        var tooLong = new string('x', L.MaxLength + 1);
        File.WriteAllText(Path.Combine(dir, "de.json"), UserFile("de", $$"""
            , "common.ready": "Fertig.",
              "common.info.name": "Erklärung: {thema}",
              "common.retry": "{{tooLong}}",
              "settings.changes.count": { "one": "{n} Punkt" },
              "common.cancel": { "one": "a", "other": "b" },
              "common.close": "Schließen",
              "nicht.da": "egal",
              "_ru.common.close": "Закрыть"
            """, flag: "DE"));
        L.LoadUser([dir]);
        Assert.Single(L.Problems);   // одна строка о четырёх отвергнутых строках
        Assert.Contains("4", L.Problems[0]);
        var de = Assert.Single(L.Available, i => i.Code == "de");
        Assert.Equal("Deutsch", de.Name);
        Assert.Equal("de", de.Flag);
        Assert.False(de.BuiltIn);

        L.Set("de");
        Assert.Equal("de", L.Current);
        Assert.Equal("Fertig.", L.T("common.ready"));
        Assert.Equal("Schließen", L.T("common.close"));
        Assert.Equal("Explanation: DNS", L.T("common.info.name", new { topic = "DNS" }));   // подстановка не та — английская
        Assert.Equal("Retry", L.T("common.retry"));                                        // длиннее предела — английская
        Assert.Equal("Cancel", L.T("common.cancel"));                                      // формы вместо строки — английская
        Assert.Equal("2 items", L.Plural("settings.changes.count", 2));                    // нет формы other — английская
    }

    [Fact]
    public void Set_language_raises_changed_and_unknown_falls_back_to_default()
    {
        int changed = 0;
        void On() => changed++;
        L.Changed += On;
        try
        {
            L.Set(L.En);
            L.Set(L.En);
            Assert.Equal(1, changed);
            L.Set("zz");   // нет такого — по языку Windows
            Assert.Contains(L.Current, new[] { "ru", "en" });
        }
        finally { L.Changed -= On; }
    }

    [Fact]
    public void Translation_template_is_a_valid_user_translation_after_filling_meta()
    {
        using var tmp = new TempDir();
        var settings = WindowSettings.Load(Path.Combine(tmp.Sub("w"), "window.json"));
        var file = new LangViewModel(settings).ExportTemplate();
        var text = File.ReadAllText(file);
        Assert.Contains("\"_ru.common.ready\": \"Готово.\"", text);
        Assert.Contains("\"common.ready\": \"Done.\"", text);
        var filled = text.Replace("\"language\": \"\"", "\"language\": \"Test\"").Replace("\"code\": \"\"", "\"code\": \"tt\"");
        var problems = new List<string>();
        var pack = LangFiles.ParseUser(filled, "tt.json", Ru, L.BuiltIn.Keys, problems);
        Assert.Empty(problems);
        Assert.NotNull(pack);
        Assert.Equal(Ru.Strings.Count, pack!.Strings.Count);
    }

    /// <summary>Тексты (i): docs/info-texts.md — дословная копия ru/info.json (раздел ↔ info.&lt;тема&gt;.title).</summary>
    [Fact]
    public void Info_doc_matches_ru_info_json()
    {
        var doc = WindowSmokeTests.DocInfoTexts();
        Assert.Equal(InfoTexts.All.Count, doc.Count);
        L.Set(L.Ru);
        foreach (var topic in InfoTexts.All)
        {
            var title = InfoTexts.Title(topic);
            Assert.True(doc.ContainsKey(title), $"В docs/info-texts.md нет раздела «{title}» ({topic})");
            Assert.Equal(doc[title], InfoTexts.Get(topic));
            Assert.False(string.IsNullOrEmpty(En.Strings[topic].Text));
        }
    }
}
