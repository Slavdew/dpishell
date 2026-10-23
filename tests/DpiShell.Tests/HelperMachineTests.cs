using System.Text.Json;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Export;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Действия на весь компьютер (API 1.14, helper-protocol §5): hosts, DNS, прокси машины помощник принимает только от окна той же сборки
/// (образ процесса клиента, хеш); остальные команды того же клиента работают. Окно спрашивает перед такими действиями там, где намерение не очевидно.
/// </summary>
public class HelperMachineTests
{
    /// <summary>Помощник с проверкой клиента; администраторов не пропускаем без проверки — тест мог быть запущен с правами.</summary>
    static HelperFixture Fixture(Func<string?, ClientBuild> check) =>
        new(o => new HelperServerOptions { PipeName = o.PipeName, AllowedUser = o.AllowedUser, AllowAdministrators = false, Log = o.Log, CheckClient = check });

    static string Plan(params string[] sections) => JsonSerializer.Serialize(new ImportRequest
    {
        Mode = ImportMode.Add, Sections = [.. sections],
        Data = new() { Sites = sections.Contains("sites") ? ["a.example"] : null, Hosts = sections.Contains("hosts") ? ["140.82.121.3 github.com"] : null },
    }, SettingsSections.Json);

    [Fact]
    public async Task Other_build_gets_no_machine_actions_but_everything_else_works()
    {
        await using var f = Fixture(_ => ClientBuild.Other);
        await using var c = await f.ConnectAsync();
        Assert.False(c.MachineAllowed);
        f.Backend.Release.SetResult();

        foreach (var (cmd, args) in new (string, object?)[]
        {
            ("dns_set", new { preset = "google" }),
            ("hosts_apply", null),
            ("hosts_user_set", new { entries = new[] { "140.82.121.3 github.com" } }),
            ("hosts_comment", new { ids = new[] { "4:0123456789ab" } }),
            ("proxy_machine_set", new { enabled = true, server = "127.0.0.1:7897", winhttp = true }),
            ("proxy_machine_restore", null),
            ("settings_import", new { plan = Plan("sites", "hosts") }),
        })
        {
            var e = await Assert.ThrowsAsync<HelperException>(() => c.CallAsync(cmd, args));
            Assert.Equal(HelperErrors.Denied, e.Code);
            Assert.Equal(L.T("core.helper.machine.other"), e.Message);
        }
        Assert.DoesNotContain(f.Backend.Calls, x => x.StartsWith("dns_set") || x.StartsWith("hosts_") || x.StartsWith("proxy_machine") || x.StartsWith("settings_import"));

        // Остальное — как прежде: состояние, включение, возврат как было, загрузка без hosts и DNS.
        await c.StatusAsync();
        await c.CallAsync("enable", new { strategy = "found-1" });
        await c.CallAsync("dns_restore");
        await c.CallAsync("hosts_restore");
        await c.CallAsync("settings_import", new { plan = Plan("sites") });
        Assert.Contains(f.Backend.Calls, x => x.StartsWith("settings_import"));
        Assert.Contains(f.Log, l => l.Contains("отказ"));
    }

    [Theory]
    [InlineData(ClientBuild.WindowOlder, "core.helper.machine.window_older")]
    [InlineData(ClientBuild.WindowNewer, "core.helper.machine.window_newer")]
    public async Task Refusal_says_what_to_update(ClientBuild build, string key)
    {
        await using var f = Fixture(_ => build);
        await using var c = await f.ConnectAsync();
        var e = await Assert.ThrowsAsync<HelperException>(() => c.CallAsync("dns_set", new { preset = "google" }));
        Assert.Equal(L.T(key), e.Message);
    }

    [Fact]
    public async Task Same_build_and_old_style_server_accept_machine_actions()
    {
        string? seen = null;
        await using (var f = Fixture(p => { seen = p; return ClientBuild.Same; }))
        await using (var c = await f.ConnectAsync())
        {
            Assert.True(c.MachineAllowed);
            await c.CallAsync("dns_set", new { preset = "google" });
            Assert.Contains(f.Backend.Calls, x => x.StartsWith("dns_set"));
        }
        // Помощник смотрит на образ процесса на том конце канала — это процесс теста.
        Assert.Equal(Environment.ProcessPath, seen, ignoreCase: true);

        // Без проверки (тесты, прежний помощник по смыслу) — всё принимается, hello говорит machine:true.
        await using var plain = new HelperFixture();
        await using var pc = await plain.ConnectAsync();
        Assert.True(pc.MachineAllowed);
    }

    [Fact]
    public async Task Check_failure_is_treated_as_other_build()
    {
        await using var f = Fixture(_ => throw new IOException("нет доступа"));
        await using var c = await f.ConnectAsync();
        Assert.False(c.MachineAllowed);
        await c.StatusAsync();
    }

    [Fact]
    public void Machine_list_covers_owner_decision()
    {
        foreach (var cmd in new[] { "hosts_apply", "hosts_user_set", "hosts_comment", "dns_set", "proxy_machine_set", "proxy_machine_restore" })
        {
            Assert.Contains(cmd, HelperCommands.MachineCommands);
            Assert.True(HelperCommands.Specs.ContainsKey(cmd));
        }
        // Возврат как было и чтение — не машинные.
        foreach (var cmd in new[] { "hosts_revert", "hosts_restore", "hosts_uncomment", "dns_restore", "changes_undo_all", "proxy_status", "proxy_user_set", "status" })
            Assert.DoesNotContain(cmd, HelperCommands.MachineCommands);
        Assert.Equal("1.14", HelperProtocol.MachineApi);
        Assert.True(HelperCompat.AtLeast(HelperProtocol.Api, HelperProtocol.MachineApi));
    }

    // ---------- образ клиента ----------

    [Fact]
    public void Image_check_by_hash_not_by_path()
    {
        using var t = new TempDir();
        var helper = Put(t.Sub("helper"), "DpiShell.exe", "build A");
        var same = Put(t.Sub("portable"), "DpiShell.exe", "build A");
        var other = Put(t.Sub("other"), "DpiShell.exe", "build B");
        var check = new HelperClientImage(helper, "0.10.0.0");
        Assert.Equal(ClientBuild.Same, check.Check(same));
        Assert.Equal(ClientBuild.Other, check.Check(other));        // не DpiShell по ресурсам — «другая»
        Assert.Equal(ClientBuild.Other, check.Check(null));
        Assert.Equal(ClientBuild.Other, check.Check(t.Sub("none.exe")));

        // Файл в записываемой папке подменили после проверки той же длиной и с прежним временем записи — новая проверка видит другой хеш.
        var written = File.GetLastWriteTimeUtc(same);
        File.WriteAllText(same, "build B");
        File.SetLastWriteTimeUtc(same, written);
        Assert.Equal(ClientBuild.Other, check.Check(same));
    }

    [Fact]
    public void Image_check_includes_assemblies_next_to_exe()
    {
        // Сборка не одним файлом (разработка): код в DpiShell.dll — её хеш тоже часть отпечатка.
        using var t = new TempDir();
        var helper = Put(t.Sub("helper"), "DpiShell.exe", "host");
        Put(t.Sub("helper"), "DpiShell.dll", "code A");
        var client = Put(t.Sub("client"), "DpiShell.exe", "host");
        Put(t.Sub("client"), "DpiShell.dll", "code B");
        var lone = Put(t.Sub("lone"), "DpiShell.exe", "host");
        var check = new HelperClientImage(helper, "0.10.0.0");
        Assert.Equal(ClientBuild.Other, check.Check(client));
        Assert.Equal(ClientBuild.Other, check.Check(lone));
        Put(t.Sub("client"), "DpiShell.dll", "code A");
        Assert.Equal(ClientBuild.Same, new HelperClientImage(helper, "0.10.0.0").Check(client));
    }

    [Fact]
    public void Image_check_tells_older_and_newer_window_by_version()
    {
        // Настоящий DpiShell.exe сборки тестов (ProductName DpiShell, версия из Directory.Build.props), помощник — другой файл.
        var exe = Path.Combine(AppContext.BaseDirectory, "DpiShell.exe");
        Assert.True(File.Exists(exe));
        var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
        var mine = new Version(v.FileVersion!);
        using var t = new TempDir();
        var helper = Put(t.Sub("helper"), "DpiShell.exe", "другая сборка");
        Assert.Equal(ClientBuild.WindowOlder, new HelperClientImage(helper, new Version(mine.Major, mine.Minor + 1, 0, 0).ToString()).Check(exe));
        Assert.Equal(ClientBuild.WindowNewer, new HelperClientImage(helper, "0.0.1.0").Check(exe));
        Assert.Equal(ClientBuild.Other, new HelperClientImage(helper, mine.ToString()).Check(exe));   // та же версия, другой файл
        Assert.Equal(ClientBuild.Same, new HelperClientImage(exe).Check(exe));
    }

    [Fact]
    public void Process_path_of_self_is_found()
    {
        Assert.Equal(Environment.ProcessPath, HelperClientImage.ProcessPath((uint)Environment.ProcessId), ignoreCase: true);
        Assert.Null(HelperClientImage.ProcessPath(0));
    }

    static string Put(string dir, string name, string body)
    {
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, body);
        return p;
    }

    // ---------- окно ----------

    [Fact]
    public async Task Window_same_version_other_build_offers_helper_update()
    {
        await using var f = Fixture(_ => ClientBuild.Other);
        var link = new HelperLink { Connector = ct => HelperClient.ConnectAsync(f.ClientOptions, ct), Setup = new NoSetup(), WindowVersion = HelperProtocol.AppVersion };
        Assert.False(await link.ConnectAsync());
        Assert.Equal(HelperState.Outdated, link.State);
        Assert.Equal(L.T("shell.helper.connect_other_build"), link.Problem);
        Assert.NotNull(link.Reader);   // читать можно

        // Окно старее помощника: подключено, остальное работает; на действия на весь компьютер помощник сам скажет, что обновить.
        var old = new HelperLink { Connector = ct => HelperClient.ConnectAsync(f.ClientOptions, ct), Setup = new NoSetup(), WindowVersion = "0.0" };
        Assert.True(await old.ConnectAsync());
        Assert.Equal(HelperState.Connected, old.State);
    }

    [Fact]
    public async Task Window_asks_before_machine_proxy_and_respects_no()
    {
        await using var f = new HelperFixture();
        var link = await f.LinkAsync();
        var shell = new Shell(new AppPaths(new TempDir().Sub("root"), Repo.Data));
        var vm = new ProxyViewModel(link, TestWindow.Settings(shell)) { Registry = new MemoryProxyRegistry(), Need = () => Task.FromResult(link.Client) };
        vm.EditName = "Clash"; vm.EditServer = "127.0.0.1:7897";
        vm.AddCmd.Execute(null);
        var asked = new List<string>();
        vm.Confirm = q => { asked.Add(q); return false; };

        vm.Target = ProxyTarget.Machine;
        vm.AlsoWinHttp = true;
        await vm.EnableAsync(vm.Items[0]);
        var q = Assert.Single(asked);
        Assert.Contains("127.0.0.1:7897", q);
        Assert.Contains(L.T("proxy.confirm.machine_services").Trim(), q);
        Assert.DoesNotContain(f.Backend.Calls, x => x.StartsWith("proxy_machine_set"));

        vm.Confirm = q2 => { asked.Add(q2); return true; };
        await vm.EnableAsync(vm.Items[0]);
        Assert.Equal(2, asked.Count);
        Assert.Contains(f.Backend.Calls, x => x.StartsWith("proxy_machine_set True"));

        // «Для этого пользователя» — без вопроса: только своя ветка реестра.
        vm.Target = ProxyTarget.User;
        await vm.EnableAsync(vm.Items[0]);
        Assert.Equal(2, asked.Count);
    }
}
