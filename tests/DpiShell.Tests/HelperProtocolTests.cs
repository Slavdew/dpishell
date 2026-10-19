using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Белый список команд помощника и строгая проверка аргументов (helper-protocol §3, §5) — без канала.</summary>
public class HelperProtocolTests
{
    static readonly FakeHelperBackend Catalog = new();

    static HelperCall Parse(string json) => HelperCommands.Parse(json, Catalog);

    static string Rejected(string json) => Assert.Throws<HelperRequestException>(() => Parse(json)).Message;

    [Fact]
    public void Valid_requests_are_typed()
    {
        var c = Parse("""{"id":1,"cmd":"enable","args":{"strategy":"alt9"}}""");
        Assert.Equal("enable", c.Cmd);
        Assert.Equal("alt9", c.Str("strategy"));

        var s = Parse("""{"id":2,"cmd":"select","args":{"quick":false,"groups":["youtube","user"]}}""");
        Assert.False(s.Bool("quick", true));
        Assert.Equal(["youtube", "user"], s.List("groups"));

        var n = Parse("""{"id":3,"cmd":"status"}""");
        Assert.Empty(n.Args);

        var x = Parse("""{"id":4,"cmd":"cancel","target":2}""");
        Assert.Equal(2, x.Target);
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"format_c"}""", "Неизвестная команда")]
    [InlineData("""{"id":1,"cmd":"run","args":{"exe":"cmd.exe"}}""", "Неизвестная команда")]
    [InlineData("""{"id":1,"cmd":"status","args":{"verbose":true}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":"alt9","args":"--wf-tcp=1"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"status","extra":1}""", "Лишнее поле")]
    [InlineData("""{"id":1,"cmd":"status","target":1}""", "Лишнее поле")]
    [InlineData("""{"cmd":"status"}""", "id")]
    [InlineData("""{"id":-1,"cmd":"status"}""", "id")]
    [InlineData("""{"id":"1","cmd":"status"}""", "id")]
    [InlineData("""[1,2]""", "объектом")]
    [InlineData("""not json""", "JSON")]
    [InlineData("""{"id":1,"id":2,"cmd":"status"}""", "повтор")]
    [InlineData("""{"id":1,"cmd":"enable"}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":"alt999"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":"C:\\Windows\\System32\\cmd.exe"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":"..\\..\\winws.exe"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":"alt9 --wf-tcp=80"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"enable","args":{"strategy":9}}""", "строка")]
    [InlineData("""{"id":1,"cmd":"select","args":{"groups":["youtube","C:\\x"]}}""", "группы")]
    [InlineData("""{"id":1,"cmd":"select","args":{"groups":["youtube","youtube"]}}""", "дважды")]
    [InlineData("""{"id":1,"cmd":"select","args":{"quick":"yes"}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"set_run_mode","args":{"service":1}}""", "true или false")]
    [InlineData("""{"id":1,"cmd":"ipset_mode","args":{"mode":"any"}}""", "допустимо")]
    [InlineData("""{"id":1,"cmd":"game_mode","args":{"mode":"tcp"}}""", "допустимо")]
    [InlineData("""{"id":1,"cmd":"ipset_update","args":{"from":"file","path":"C:\\list.txt"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"ipset_update","args":{"from":"url"}}""", "допустимо")]
    [InlineData("""{"id":1,"cmd":"ipset_update","args":{"from":"content"}}""", "нет содержимого")]
    [InlineData("""{"id":1,"cmd":"ipset_update","args":{"from":"manifest","content":"1.2.3.0/24"}}""", "content")]
    [InlineData("""{"id":1,"cmd":"user_targets_set","args":{"lines":["\"C:\\Windows\\cmd.exe\" /c calc"]}}""", "путь")]
    [InlineData("""{"id":1,"cmd":"user_targets_set","args":{"lines":["\\\\server\\share\\x"]}}""", "путь")]
    [InlineData("""{"id":1,"cmd":"user_ipset_set","args":{"lines":"1.2.3.0/24"}}""", "список строк")]
    [InlineData("""{"id":1,"cmd":"cancel"}""", "target")]
    [InlineData("""{"id":1,"cmd":"cancel","target":1,"args":{}}""", "аргументов")]
    [InlineData("""{"id":1,"cmd":"read_results","args":{"file":"..\\state.json"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"choose"}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"choose","args":{"strategy":"C:\\zapret\\bin\\winws.exe"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"choose","args":{"strategy":"alt999"}}""", "нет такой стратегии")]
    [InlineData("""{"id":1,"cmd":"details","args":{"file":"state.json"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"hosts_apply","args":{"entries":[{"host":"github.com","ip":"6.6.6.6"}]}}""", "лишнее поле")]
    public void Bad_requests_are_invalid(string json, string why) =>
        Assert.Contains(why, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Id_is_reported_back_when_readable()
    {
        var e = Assert.Throws<HelperRequestException>(() => Parse("""{"id":7,"cmd":"nope"}"""));
        Assert.Equal(7, e.Id);
    }

    [Fact]
    public void Too_many_lines_rejected()
    {
        var lines = string.Join(",", Enumerable.Range(0, HelperCommands.MaxUserTargets + 1).Select(i => $"\"a{i}.ru\""));
        Assert.Contains("больше", Rejected($$$"""{"id":1,"cmd":"user_targets_set","args":{"lines":[{{{lines}}}]}}"""));
    }

    /// <summary>ТЗ 12 (API 1.4): пары «адрес имя» и «номер:хеш» — строгий разбор, никаких путей и произвольного текста.</summary>
    [Fact]
    public void Hosts_tools_take_strict_pairs_and_line_ids()
    {
        var t = Parse("""{"id":1,"cmd":"hosts_try","args":{"entries":["0.0.0.0 Ads.Example.com","140.82.121.3 github.com","140.82.121.3 github.com"]}}""");
        Assert.Equal(["0.0.0.0 ads.example.com", "140.82.121.3 github.com"], t.List("entries"));   // канонический вид, без повторов
        var u = Parse("""{"id":2,"cmd":"hosts_user_set","args":{"entries":["2606:4700::1111 one.one.one.one"]}}""");
        Assert.Equal(["2606:4700::1111 one.one.one.one"], u.List("entries"));
        Assert.Empty(Parse("""{"id":3,"cmd":"hosts_user_set","args":{"entries":[]}}""").List("entries")!);   // пусто — «убрать все свои»
        var c = Parse("""{"id":4,"cmd":"hosts_comment","args":{"ids":["12:0123456789ab","3:abcdefabcdef"]}}""");
        Assert.Equal(["12:0123456789ab", "3:abcdefabcdef"], c.List("ids"));
        Assert.Equal("hosts_audit", Parse("""{"id":5,"cmd":"hosts_audit"}""").Cmd);
        Assert.Equal("hosts_restore", Parse("""{"id":6,"cmd":"hosts_restore"}""").Cmd);
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"hosts_user_set","args":{"entries":["0.0.0.0 github.com"]}}""", "не пара")]           // только публичные адреса
    [InlineData("""{"id":1,"cmd":"hosts_user_set","args":{"entries":["192.168.1.5 nas.example.com"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_user_set","args":{"entries":["127.0.0.1 github.com"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_user_set","args":{"entries":["fe80::1%12 github.com"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":["1.2.3.4 github.com extra"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":["1.2.3.4 C:\\Windows\\x"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":["1.2.3.4 *.github.com"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":["1.2.3.4 localhost"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":["1.2.3.4\ngithub.com"]}}""", "не пара")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":"1.2.3.4 github.com"}}""", "список строк")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{}}""", "обязательного")]
    [InlineData("""{"id":1,"cmd":"hosts_try","args":{"entries":[],"path":"C:\\hosts"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"ids":["12"]}}""", "не «номер:отпечаток»")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"ids":["12:ABCDEF012345"]}}""", "не «номер:отпечаток»")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"ids":["0:abcdef012345"]}}""", "не «номер:отпечаток»")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"ids":["1:abcdef012345; calc"]}}""", "не «номер:отпечаток»")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"ids":["1:abcdef012345","1:abcdef012345"]}}""", "дважды")]
    [InlineData("""{"id":1,"cmd":"hosts_comment","args":{"text":"0.0.0.0 x"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"hosts_audit","args":{"file":"hosts"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"hosts_restore","args":{"backup":"x.bak"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"hosts_uncomment","args":{"ids":[]}}""", "лишнее поле")]
    public void Hosts_tools_reject_bad_arguments(string json, string why) =>
        Assert.Contains(why, Rejected(json), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Hosts_tools_limit_500_entries()
    {
        string Many(int n, Func<int, string> f) => string.Join(",", Enumerable.Range(0, n).Select(i => $"\"{f(i)}\""));
        var ok500 = Many(500, i => $"1.2.3.4 h{i}.example.com");
        var bad501 = Many(501, i => $"1.2.3.4 h{i}.example.com");
        var ids501 = Many(501, i => $"{i + 1}:abcdef012345");
        Assert.Equal(500, Parse($$$"""{"id":1,"cmd":"hosts_user_set","args":{"entries":[{{{ok500}}}]}}""").List("entries")!.Count);
        Assert.Contains("больше", Rejected($$$"""{"id":1,"cmd":"hosts_user_set","args":{"entries":[{{{bad501}}}]}}"""));
        Assert.Contains("больше", Rejected($$$"""{"id":1,"cmd":"hosts_comment","args":{"ids":[{{{ids501}}}]}}"""));
    }

    [Fact]
    public void Hosts_tools_flags_exclusive_and_long()
    {
        foreach (var cmd in new[] { "hosts_try", "hosts_audit", "hosts_user_set", "hosts_comment", "hosts_uncomment", "hosts_restore" })
            Assert.True(HelperCommands.Specs[cmd].Exclusive, cmd);   // меняют файл или грузят сеть — по одной
        Assert.True(HelperCommands.Specs["hosts_try"].Long);
        Assert.True(HelperCommands.Specs["hosts_audit"].Long);
        Assert.True(HelperCommands.Specs["hosts_user_set"].Long);   // повторная проверка адресов — можно отменить
        Assert.False(HelperCommands.Specs["hosts_comment"].Long);
    }

    [Fact]
    public void Content_and_cidr_lines_pass()
    {
        var c = Parse("""{"id":1,"cmd":"ipset_update","args":{"from":"content","content":"1.2.3.0/24\n5.6.0.0/16\n","confirmed":true}}""");
        Assert.True(c.Bool("confirmed"));
        var u = Parse("""{"id":2,"cmd":"user_ipset_set","args":{"lines":["10.0.0.0/8","2001:db8::/32"],"exclude":true}}""");
        Assert.Equal(2, u.List("lines")!.Count);
    }

    [Fact]
    public void Whitelist_matches_spec()
    {
        string[] spec =
        [
            "status", "enable", "disable", "set_run_mode", "select", "search", "cancel", "ipset_mode", "all_addresses_start",
            "all_addresses_stop", "game_mode", "ipset_update", "user_ipset_set", "user_targets_set", "hosts_check", "hosts_preview",
            "hosts_apply", "hosts_revert", "hosts_recheck", "stop_foreign", "core_repair", "remove_all", "clear_history", "read_results",
            "details", "choose", "tcp_timestamps_on", "data_update", "dns_status", "dns_set", "dns_restore",
            "strategy_add", "strategy_delete", "strategy_check",
            "hosts_try", "hosts_audit", "hosts_user_set", "hosts_comment", "hosts_uncomment", "hosts_restore",
            "rec_start", "rec_status", "rec_stop", "app_profiles", "app_profile_set", "app_profile_remove",
            "changes_list", "changes_undo_all", "changes_undo",
            "proxy_status", "proxy_user_set", "proxy_machine_set", "proxy_machine_restore",
        ];
        Assert.Equal(spec.Order(), HelperCommands.Specs.Keys.Order());
        // Команды DNS описаны в helper-protocol §3 (часть старых там записана сокращённо: hosts_check/preview/…).
        var doc = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "docs", "dev", "helper-protocol.md"));
        Assert.All(["dns_status", "dns_set {preset, ips?, doh?, fallback?}", "dns_restore"], cmd => Assert.Contains("`" + cmd + "`", doc));
        // Список команд в status — тот же белый список; версия протокола — та, что записана в helper-protocol §6.
        Assert.Equal(HelperCommands.Specs.Keys.Order(StringComparer.Ordinal), HelperCommands.Names);
        Assert.All(["strategy_add {title, roles, source}", "strategy_delete {strategy}", "strategy_check {strategy, groups?}"], cmd => Assert.Contains("`" + cmd + "`", doc));
        Assert.All(["hosts_try {entries}", "hosts_audit", "hosts_user_set {entries}", "hosts_comment {ids}", "hosts_uncomment", "hosts_restore"],
            cmd => Assert.Contains("`" + cmd + "`", doc));
        Assert.All(["changes_list", "changes_undo_all", "changes_undo {id}"], cmd => Assert.Contains("`" + cmd + "`", doc));
        Assert.All(["proxy_status", "proxy_user_set {enabled, server?, bypass?, pac?}", "proxy_machine_set {enabled, server?, bypass?, pac?, winhttp?}", "proxy_machine_restore"],
            cmd => Assert.Contains("`" + cmd + "`", doc));
        Assert.Contains($"Текущая версия — `{HelperProtocol.Api}`", doc);
        // Только чтение — не занимает «одну операцию за раз».
        Assert.Equal(["app_profiles", "cancel", "changes_list", "details", "dns_status", "hosts_preview", "proxy_status", "read_results", "rec_status", "status"],
            HelperCommands.Specs.Values.Where(s => !s.Exclusive).Select(s => s.Name).Order());
    }

    [Fact]
    public void Error_texts_are_russian_and_distinct()
    {
        string[] codes = [HelperErrors.Invalid, HelperErrors.Busy, HelperErrors.Conflict, HelperErrors.Failed, HelperErrors.Denied, HelperErrors.Cancelled, HelperErrors.Incompatible];
        var texts = codes.Select(HelperErrors.Text).ToList();
        Assert.Equal(codes.Length, texts.Distinct().Count());
        Assert.All(texts, t => Assert.Matches("[а-яА-Я]", t));
    }

    // ---------- совместимость окна и помощника (helper-protocol §6) ----------

    [Theory]
    [InlineData("1.1", HelperCompatLevel.Ok)]
    [InlineData(null, HelperCompatLevel.MinorDiffers)]      // помощник до 1.1 не присылает api — «1.0»
    [InlineData("1.0", HelperCompatLevel.MinorDiffers)]
    [InlineData("1.7", HelperCompatLevel.MinorDiffers)]
    [InlineData("2.0", HelperCompatLevel.Incompatible)]
    [InlineData("0.9", HelperCompatLevel.Incompatible)]
    [InlineData("мусор", HelperCompatLevel.Incompatible)]
    public void Compat_levels(string? helperApi, HelperCompatLevel level)
    {
        var c = HelperCompat.Evaluate(helperApi, null, "1.0.0.0", windowApi: "1.1", windowVersion: "1.0.0.0");
        Assert.Equal(level, c.Level);
        Assert.Equal(level == HelperCompatLevel.Ok, c.Message is null);
    }

    [Fact]
    public void Compat_incompatible_allows_only_status_and_asks_reinstall()
    {
        var c = HelperCompat.Evaluate("2.0", ["status", "enable"], "1.0.0.0", "1.1", "1.0.0.0");
        Assert.True(c.Allows("status"));
        Assert.False(c.Allows("enable"));
        Assert.False(c.Supports("enable"));
        Assert.True(c.NeedsReinstall);
        Assert.Contains("переустановите", c.Message);
    }

    [Fact]
    public void Compat_minor_disables_unknown_commands_only()
    {
        var c = HelperCompat.Evaluate("1.0", ["status", "enable"], "1.0.0.0", "1.1", "1.0.0.0");
        Assert.True(c.Allows("dns_set"));            // отправить можно — помощник сам ответит invalid
        Assert.False(c.Supports("dns_set"));         // но кнопка неактивна
        Assert.True(c.Supports("enable"));
        Assert.Contains("Обновите помощника", c.Message);
        // Список неизвестен (помощник до 1.1) — не ограничиваем.
        Assert.True(HelperCompat.Evaluate(null, null, "1.0.0.0", "1.1", "1.0.0.0").Supports("dns_set"));
    }

    [Fact]
    public void Compat_older_helper_program_asks_update()
    {
        var c = HelperCompat.Evaluate("1.1", null, "1.0.0.0", "1.1", "1.2.0.0");
        Assert.Equal(HelperCompatLevel.Ok, c.Level);
        Assert.True(c.HelperOutdated);
        Assert.True(c.NeedsReinstall);
        Assert.Contains("обновите", c.Message);
        Assert.False(HelperCompat.Evaluate("1.1", null, "1.2.0.0", "1.1", "1.2.0").HelperOutdated);
    }
}
