using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Белый список команд помощника и строгая проверка аргументов (11-helper-spec §3, §5) — без канала.</summary>
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
        ];
        Assert.Equal(spec.Order(), HelperCommands.Specs.Keys.Order());
        // Команды DNS описаны в 11-helper-spec §3 (часть старых там записана сокращённо: hosts_check/preview/…).
        var doc = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "docs", "11-helper-spec.md"));
        Assert.All(["dns_status", "dns_set {preset, ips?, doh?, fallback?}", "dns_restore"], cmd => Assert.Contains("`" + cmd + "`", doc));
        // Только чтение — не занимает «одну операцию за раз».
        Assert.Equal(["cancel", "details", "dns_status", "hosts_preview", "read_results", "status"],
            HelperCommands.Specs.Values.Where(s => !s.Exclusive).Select(s => s.Name).Order());
    }

    [Fact]
    public void Error_texts_are_russian_and_distinct()
    {
        string[] codes = [HelperErrors.Invalid, HelperErrors.Busy, HelperErrors.Conflict, HelperErrors.Failed, HelperErrors.Denied, HelperErrors.Cancelled];
        var texts = codes.Select(HelperErrors.Text).ToList();
        Assert.Equal(codes.Length, texts.Distinct().Count());
        Assert.All(texts, t => Assert.Matches("[а-яА-Я]", t));
    }
}
