using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// «Диагностика» → «Сохранить отчёт»: один текстовый файл; нужное в нём есть, личного нет (адрес прокси с логином, свои сайты и
/// исключения, локальные адреса, токены, имя пользователя в путях), число своих сайтов и исключений — есть, выбор места — подменяемый.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class SupportReportTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() { _t.Dispose(); L.ResetForTests(); }

    const string ProxyWithLogin = "admin:Pa55w0rd@proxy.corp.example:3128";
    static readonly string[] Personal =
    [
        "Pa55w0rd", "proxy.corp.example", "admin:", "my-secret-bank.example", "other-site.example", "excluded-pay.example",
        "192.168.77.5", "10.77.0.9", "fe80::1ff:fe23:4567:890a", "aa:bb:cc:dd:ee:ff", "abc123SECRET", "SuperToken99", @"C:\Users\Ivan Petrov", "172.16.9.3",
    ];

    Shell NewShell() => new(new AppPaths(_t.Sub("root"), Repo.Data));

    async Task<(MainViewModel vm, HelperFixture f)> PrepareAsync()
    {
        var f = new HelperFixture();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.TargetsText = "my-secret-bank.example\nother-site.example";
        vm.ExcludeText = "excluded-pay.example";
        vm.Proxy.Show(new ProxyOverview(new ProxySettings(true, ProxyWithLogin, "*.corp.example", null), false, ProxySettings.Off, false, null, false,
            new WinHttpInfo("172.16.9.3:8080", null)));
        f.Backend.LogTailValue =
        [
            "2026-10-21 12:00:01 клиент 192.168.77.5 token=abc123SECRET",
            "2026-10-21 12:00:02 подбор: my-secret-bank.example не открылся, шлюз fe80::1ff:fe23:4567:890a mac aa:bb:cc:dd:ee:ff",
            "2026-10-21 12:00:03 прокси http://" + ProxyWithLogin,
        ];
        var logs = _t.Sub("winlogs");
        Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, "app-errors.log"),
            "2026-10-21T12:00:00 System.IO.IOException: файл C:\\Users\\Ivan Petrov\\AppData\\x.json, сервер 10.77.0.9, Authorization: Bearer SuperToken99\r\n");
        vm.WindowLogDir = () => logs;
        return (vm, f);
    }

    [Fact]
    public async Task Report_has_what_is_needed_and_nothing_personal()
    {
        var (vm, f) = await PrepareAsync();
        await using var _ = f;
        var text = await vm.BuildReportAsync();

        foreach (var secret in Personal) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName.Length >= 3 ? @"\Users\" + Environment.UserName + @"\" : "\u0001", text, StringComparison.OrdinalIgnoreCase);

        // Первая строка: что есть и что скрыто.
        var first = text.Split('\n')[0];
        Assert.StartsWith("Отчёт DpiShell,", first);
        Assert.Contains("Скрыто:", first);
        // Версии, служба, роутер, прокси (только вид), число своих сайтов и исключений, журналы.
        Assert.Contains("Программа: " + vm.AppVersionText, text);
        Assert.Contains("Помощник: ", text);
        Assert.Contains("Ядро zapret: ", text);
        Assert.Contains("Данные: ", text);
        Assert.Contains("Windows: ", text);
        Assert.Contains("Режим: ", text);
        Assert.Contains("Оптимизация: ", text);
        Assert.Contains("Прокси пользователя: включён (прокси-сервер)", text);
        Assert.Contains("Прокси машины: выключен", text);
        Assert.Contains("WinHTTP: через прокси", text);
        Assert.Contains("Найден: нет", text);
        Assert.Contains("Свои сайты: 2", text);
        Assert.Contains("Исключения: 1", text);
        Assert.Contains("[скрыто]", text);
        Assert.Contains("клиент [скрыто] token=[скрыто]", text);            // хвост журнала помощника — через log_tail, без личного
        Assert.Contains("IOException", text);                                 // журнал окна
        Assert.Contains("Authorization: [скрыто]", text);
        Assert.Contains("log_tail", string.Join("\n", f.Backend.Calls));
    }

    [Fact]
    public async Task Report_is_in_the_window_language()
    {
        var (vm, f) = await PrepareAsync();
        await using var _ = f;
        L.Set(L.En);
        var text = await vm.BuildReportAsync();
        Assert.StartsWith("DpiShell report,", text);
        Assert.Contains("== Versions ==", text);
        Assert.Contains("User proxy: on (proxy server)", text);
        Assert.Contains("My sites: 2", text);
        foreach (var secret in Personal) Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Save_report_writes_one_file_through_replaceable_dialog_and_cancel_writes_nothing()
    {
        var (vm, f) = await PrepareAsync();
        await using var _ = f;
        var path = Path.Combine(_t.Sub("out"), "report.txt");
        Directory.CreateDirectory(_t.Sub("out"));

        vm.ChooseSaveFile = (_, _, _) => null;
        await vm.SaveReportAsync();
        Assert.Empty(Directory.GetFiles(_t.Sub("out")));

        string? name = null;
        vm.ChooseSaveFile = (_, filter, n) => { name = n; Assert.Contains("*.txt", filter); return path; };
        await vm.SaveReportAsync();
        Assert.StartsWith("dpishell-report-", name);
        var saved = File.ReadAllText(path);
        Assert.Contains("== Версии ==", saved);
        Assert.Contains("Свои сайты: 2", saved);
        foreach (var secret in Personal) Assert.DoesNotContain(secret, saved, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(path, vm.Message);
    }

    [Theory]
    [InlineData("шлюз 192.168.1.1 порт 80", "шлюз [скрыто] порт 80")]
    [InlineData("адрес fe80::1 и 2001:db8::ff00:42:8329 и 2001:0db8:0000:0000:0000:ff00:0042:8329", "адрес [скрыто] и [скрыто] и [скрыто]")]
    [InlineData("карта 00-1A-2B-3C-4D-5E ок", "карта [скрыто] ок")]
    [InlineData("http://user:pass@host.example/x", "http://[скрыто]@host.example/x")]
    [InlineData("password=hunter2 и Token: \"a b\"", "password=[скрыто] и Token: [скрыто]")]
    [InlineData("Bearer abcdef123456", "Bearer [скрыто]")]
    [InlineData(@"в C:\Users\Ivan\AppData\x", @"в C:\Users\[скрыто]\AppData\x")]
    [InlineData("время 12:30:45 и версия 0.10.0", "время 12:30:45 и версия 0.10.0")]
    public void Scrub_removes_addresses_credentials_and_paths_only(string input, string expected)
    {
        Assert.Equal(expected, SupportReport.Scrub(input));
    }

    [Fact]
    public void Scrub_hides_named_things_longest_first_and_proxy_variants()
    {
        var hide = SupportReport.Variants("https://bank.example/path").Concat(SupportReport.Variants(ProxyWithLogin)).ToList();
        var text = SupportReport.Scrub("открыт https://bank.example/path и bank.example, прокси proxy.corp.example:3128 / proxy.corp.example", hide);
        Assert.DoesNotContain("bank.example", text);
        Assert.DoesNotContain("corp.example", text);
    }
}
