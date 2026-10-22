using DpiShell.Core;
using DpiShell.Core.Localization;

namespace DpiShell.Tests;

/// <summary>Перевод готовых фраз на язык окна (L.Relocalize): ответы помощника, записи журнала, сообщения последнего действия.</summary>
[Collection(LangCollection.Name)]
public sealed class RelocalizeTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Fact]
    public void Core_phrases_with_arguments_become_english_and_back()
    {
        L.Set("en");
        Assert.Equal("301: redirect to www.example.com", L.Relocalize("301: редирект на www.example.com"));
        Assert.Equal("Core files not found. An antivirus may have removed them.", L.Relocalize("Файлы ядра не найдены. Возможно, их удалил антивирус."));
        // Вложенная фраза и список.
        Assert.Equal("restored: proxy settings, proxy for services (WinHTTP)", L.Relocalize("возвращено: настройки прокси, прокси для служб (WinHTTP)"));
        Assert.Equal("DNS is back as before: Ethernet.", L.Relocalize("DNS — как было: Ethernet."));
        // Уже английское и чужое — без изменений.
        Assert.Equal("301: redirect to www.example.com", L.Relocalize("301: redirect to www.example.com"));
        Assert.Equal("Моя игра", L.Relocalize("Моя игра"));
        L.Set("ru");
        Assert.Equal("301: редирект на www.example.com", L.Relocalize("301: redirect to www.example.com"));
    }

    [Fact]
    public void Own_messages_are_matched_by_short_templates_and_plural_forms()
    {
        L.Set("en");
        Assert.Equal("Helper is not responding — its records are not shown now.", L.Relocalize("Помощник не отвечает — его записи сейчас не видны.", own: true));
        Assert.Equal("Suitable: general (ALT)", L.Relocalize("Подходит: general (ALT)", own: true));
        Assert.Equal("5 sites", L.Relocalize("5 сайтов", own: true));
        // Чужой текст короткими шаблонами не задевается.
        Assert.Equal("Подходит: general (ALT)", L.Relocalize("Подходит: general (ALT)"));
    }

    [Fact]
    public void Auto_names_of_strategies_are_shown_in_english()
    {
        L.Set("en");
        Assert.Equal("New strategy 2 (search 16.10 23:19)", StrategyNaming.DisplayTitle("Новая стратегия 2 (поиск 16.10 23:19)", null));
        Assert.Equal("Found 1 (261016-2213)", StrategyNaming.DisplayTitle("Найденная 1 (261016-2213)", null));
        Assert.Equal("Mixed · YouTube — ALT · my sites and Discord — ALT2", StrategyNaming.DisplayTitle("Смешанная · YouTube — ALT · свои сайты и Discord — ALT2", null));
        Assert.Equal("My own", StrategyNaming.DisplayTitle("My own", null));
    }

    [Fact]
    public void Score_error_is_a_code_and_text_follows_language()
    {
        var s = new StrategyScore(new Strategy("a", "A", "catalog", new Dictionary<string, string>()), [], 0, 1, "ломает обычные сайты: vk.com", StrategyScore.BreaksCode, "vk.com");
        Assert.True(s.BreaksRegular);
        L.Set("en");
        Assert.Equal("breaks regular sites: vk.com", s.ErrorText);
        L.Set("ru");
        Assert.Equal("ломает обычные сайты: vk.com", s.ErrorText);
    }

    [Fact]
    public void Router_warnings_are_translated_by_key_then_by_code_then_left_as_is()
    {
        L.Set("en");
        using var doc = System.Text.Json.JsonDocument.Parse("""{ "name": "alt9", "time": 1792000000 }""");
        var p = doc.RootElement.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone());
        Assert.Equal("The strategy stopped working — backup alt9 is on", App.RouterTexts.WarningText(new(Code: "switched_backup", Text: "Стратегия перестала работать — включена запасная alt9", Key: "web.warn.switched_backup", Params: p)));
        // Старый роутер: ключа нет — имя берётся из фразы по коду.
        Assert.Equal("The strategy stopped working — backup alt9 is on", App.RouterTexts.WarningText(new("switched_backup", "Стратегия перестала работать — включена запасная alt9")));
        Assert.Equal("The ISP is spoofing site addresses — apps outside the browser may not work", App.RouterTexts.WarningText(new("dns_spoofed", "Провайдер подменяет адреса сайтов — приложения вне браузера могут не работать")));
        Assert.Equal("Auto-search switched to general (ALT)", App.RouterTexts.WarningText(new("auto_selected", "Автоподбор включил general (ALT)")));
        Assert.Equal("Strategy search was interrupted: boom", App.RouterTexts.WarningText(new("select_failed", "Подбор прервался: boom")));
        // Неизвестный код — текст роутера как есть.
        Assert.Equal("Что-то новое", App.RouterTexts.WarningText(new("brand_new", "Что-то новое")));
    }

    [Fact]
    public void Router_probe_detail_and_update_notes_follow_window_language()
    {
        // detail_msg (роутер 0.2.0-r15): «редирект на <хост>» с обёртками TLS и пары; части без ключа — текстом.
        using var doc = System.Text.Json.JsonDocument.Parse("""
            { "key": "router.probe.pair", "text": "TLS 1.3: редирект на warning.rt.ru; TLS 1.2: таймаут", "params": {
                "a": { "key": "router.probe.tls", "params": { "tls": "1.3", "detail": { "key": "router.probe.redirect", "params": { "host": "warning.rt.ru" }, "text": "редирект на warning.rt.ru" } } },
                "b": { "key": "router.probe.tls", "params": { "tls": "1.2", "detail": { "text": "таймаут" } } } } }
            """);
        var v = new DpiShell.Core.Router.RouterUrlVerdict("google", "https://www.youtube.com/", false, "TLS 1.3: редирект на warning.rt.ru; TLS 1.2: таймаут", null, doc.RootElement.Clone());
        L.Set("en");
        Assert.Equal("TLS 1.3: redirect to warning.rt.ru; TLS 1.2: таймаут", v.Shown);
        L.Set("ru");
        Assert.Equal("TLS 1.3: редирект на warning.rt.ru; TLS 1.2: таймаут", v.Shown);
        // Старый роутер: detail_msg нет — текст как есть.
        Assert.Equal("редирект на x.ru", (v with { Detail = "редирект на x.ru", DetailMsg = null }).Shown);

        // Заметки обновления: английские — если роутер прислал notes_en; старый роутер — русские.
        var u = new DpiShell.Core.Router.RouterUpdate(true, "0.2.1-r1", ["Строка"], null, NotesEn: ["Line"]);
        Assert.Equal(["Line"], u.NotesFor(L.En));
        Assert.Equal(["Строка"], u.NotesFor("ru"));
        Assert.Equal(["Строка"], (u with { NotesEn = null }).NotesFor(L.En));
        // release.json выпуска: changes_en — по-английски, нет — русские.
        var rel = DpiShell.Core.Router.RouterReleaseInfo.Parse(System.Text.Encoding.UTF8.GetBytes("""
            { "schema": 1, "version": "0.2.1", "tag": "router-v0.2.1", "changes": ["Строка"], "changes_en": ["Line"], "assets": [] }
            """));
        Assert.Equal(["Line"], rel.ChangesFor(L.En));
        Assert.Equal(["Строка"], rel.ChangesFor("ru"));
        Assert.Equal(["Строка"], (rel with { ChangesEn = null }).ChangesFor(L.En));
    }
}
