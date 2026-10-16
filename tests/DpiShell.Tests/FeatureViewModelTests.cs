using System.Net;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Логика экранов «Игры и IP-списки» и «Починить адреса» без окна. Обход выключен — служба не трогается;
/// hosts — только копия во временной папке, сети нет (подставные проверки).
/// </summary>
public sealed class FeatureViewModelTests
{
    [Fact]
    public async Task Game_On_TurnsIpListOnItself_AndSaysSo()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var said = new List<string>();
        var net = new NetViewModel(shell) { Say = said.Add };
        Assert.False(net.GameOn);
        Assert.True(net.IpsetOff);
        Assert.Equal("Выключен", net.GameStatus);

        await net.SetGameAsync(true);

        Assert.True(net.GameOn);
        Assert.True(net.IpsetList);
        Assert.Contains(said, s => s.Contains("Включён IP-список"));
        Assert.StartsWith("Включится вместе с обходом", net.GameStatus);   // обход выключен
        Assert.Contains("подсетей, обновлён", net.ListText);

        // «Выкл» у IP-списка выключает и игры: без списка им нечего ловить.
        await net.SetModeAsync(IpsetMode.Off);
        Assert.False(net.GameOn);
        Assert.Contains(said, s => s.Contains("Игровой режим выключен"));
    }

    [Fact]
    public async Task AllAddresses_CountsDown_AndRevertsWithNotice()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var net = new NetViewModel(shell);
        await net.SetModeAsync(IpsetMode.List);
        shell.StartAllAddresses();
        net.Refresh();
        Assert.True(net.AnyActive);
        Assert.False(net.GameCanToggle);   // игры при «Все адреса» не включить
        Assert.Matches("осталось (9|10):[0-5][0-9]", net.AnyText);
        Assert.Contains("«По списку»", net.AnyText);

        string? notice = null;
        net.Notify += s => notice = s;
        shell.State.IpsetAnyUntil = DateTime.UtcNow.AddSeconds(-1);
        await net.RevertExpiredAsync();

        Assert.True(net.IpsetList);
        Assert.False(net.AnyActive);
        Assert.Contains("вернулся режим «По списку»", notice);
    }

    [Fact]
    public async Task UserSubnets_BadLinesRejected_WithReasons()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var net = new NetViewModel(shell) { UserText = "203.0.113.0/24\nне адрес\n10.0.0.0/8\n198.51.100.7" };
        await net.SaveUserAsync();

        var saved = shell.IpLists.Read(IpLists.UserFile);
        Assert.Equal(2, saved.Count);
        Assert.Contains("203.0.113.0/24", saved);
        Assert.Contains("Не приняты (2)", net.UserMessage);
        Assert.Contains("частная сеть", net.UserMessage);
    }

    sealed class Probe(params string[] ok) : IEndpointProbe
    {
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(ok.Contains($"{host}@{ip}") ? new EndpointCheck(EndpointOutcome.Ok, 0.1, "HTTP 200") : new EndpointCheck(EndpointOutcome.Timeout, 5, "нет ответа"));
    }

    sealed class Dns(string ip) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) => Task.FromResult(new[] { IPAddress.Parse(ip) });
    }

    sealed class NoRdap : IRdapClient
    {
        public Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default) => Task.FromResult<RdapOwner?>(null);
    }

    static (Shell, HostsViewModel, string Hosts, List<string> Questions) Hosts(TempDir t, params string[] ok)
    {
        var paths = Make.Paths(t);
        var shell = new Shell(paths);
        var hosts = t.Sub("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var cands = new List<HostsCandidate> { new("github.com", ["140.82.121.3", "140.82.121.4"], ["GITHU"], "r", "s", "s") };
        var m = new HostsManager(paths, hosts, new Probe(ok), new Dns("185.199.108.133"), new NoRdap(), () => cands);
        var questions = new List<string>();
        var vm = new HostsViewModel(shell) { Manager = m, Confirm = q => { questions.Add(q); return true; } };
        return (shell, vm, hosts, questions);
    }

    [Fact]
    public async Task Hosts_OffByDefault_CheckPreviewApplyRevert()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, questions) = Hosts(t, "github.com@140.82.121.3");
        Assert.False(vm.Enabled);
        vm.Enabled = true;
        Assert.True(shell.State.HostsFix);

        await vm.CheckAsync();
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("записать", vm.Rows[0].Verdict);
        Assert.Equal("не подходит", vm.Rows[1].Verdict);
        Assert.Contains("не отвечает", vm.Rows[1].Reason);
        Assert.True(vm.CanWrite);
        Assert.Equal(["+ 140.82.121.3 github.com"], vm.PreviewLines);
        Assert.DoesNotContain("DpiShell", File.ReadAllText(hosts));   // предпросмотр не пишет

        await vm.ApplyAsync();
        Assert.Contains("HostsFileHijack", questions.Last());
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(hosts));
        Assert.True(vm.BlockPresent);
        Assert.StartsWith("Записано адресов: 1", vm.StatusText);
        Assert.NotNull(shell.State.HostsCheckedAt);

        // Блок сняли не мы (Защитник) — понятное предупреждение.
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        await vm.RefreshAsync();
        Assert.Contains(vm.Warnings, w => w.Contains("Защитник"));

        await vm.CheckAsync();
        await vm.ApplyAsync();
        await vm.RevertAsync();
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
        Assert.False(vm.BlockPresent);
    }

    [Fact]
    public async Task Hosts_NothingToFix_IsSaidPlainly()
    {
        using var t = new TempDir();
        // DNS-адрес тоже открывается — запись не нужна.
        var (_, vm, hosts, _) = Hosts(t, "github.com@140.82.121.3", "github.com@185.199.108.133");
        vm.Enabled = true;
        await vm.CheckAsync();
        Assert.Equal("Нечего чинить: всё открывается и так.", vm.Summary);
        Assert.False(vm.CanWrite);
        Assert.Equal("не нужно", vm.Rows[0].Verdict);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
    }

    [Fact]
    public async Task Hosts_Recheck_OnlyWhenOnAndDue()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, _) = Hosts(t, "github.com@140.82.121.3");
        await vm.RecheckIfDueAsync();             // выключено — ничего
        Assert.Null(shell.State.HostsCheckedAt);

        vm.Enabled = true;
        await vm.CheckAsync();
        await vm.ApplyAsync();
        shell.State.HostsCheckedAt = DateTime.Now.AddDays(-2);
        string? notice = null;
        vm.Notify += s => notice = s;
        // Адрес перестал отвечать — перепроверка снимает его и говорит об этом.
        var m = new HostsManager(shell.Paths, hosts, new Probe(), new Dns("185.199.108.133"), new NoRdap(),
            () => [new("github.com", ["140.82.121.3"], ["GITHU"], "r", "s", "s")]);
        vm.Manager = m;
        await vm.RecheckIfDueAsync();
        Assert.True(shell.State.HostsCheckedAt > DateTime.Now.AddMinutes(-1));
        Assert.Contains("github.com устарел", notice);
        Assert.DoesNotContain("140.82.121.3", File.ReadAllText(hosts));
    }
}
