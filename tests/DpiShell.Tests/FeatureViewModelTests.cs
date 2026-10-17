using System.Net;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Логика экранов «Игры и IP-списки» и «Починить адреса» без окна. Окно без прав: всё — через помощника по каналу
/// (настоящий <see cref="ShellHelperBackend"/> над временной папкой, sc и WMI подменены).
/// Обход выключен — служба не трогается; hosts — только копия во временной папке, сети нет (подставные проверки).
/// </summary>
public sealed class FeatureViewModelTests
{
    /// <summary>Помощник над этим Shell и подключённая к нему связь окна.</summary>
    static async Task<(HelperFixture, HelperLink)> Helper(Shell shell)
    {
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        return (f, await f.LinkAsync());
    }

    [Fact]
    public async Task Game_On_TurnsIpListOnItself_AndSaysSo()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var (f, link) = await Helper(shell);
        await using var _ = f;
        var said = new List<string>();
        var net = new NetViewModel(link) { Say = said.Add };
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

    /// <summary>UX-аудит, п. 34: «Все адреса» — не переключатель, а «Проверить, поможет ли обход» с подтверждением.</summary>
    [Fact]
    public async Task AllAddresses_OnlyAfterConfirm()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var (f, link) = await Helper(shell);
        await using var _ = f;
        string? asked = null;
        bool answer = false;
        var net = new NetViewModel(link) { Confirm = q => { asked = q; return answer; } };
        await net.SetModeAsync(IpsetMode.List);

        await net.TryAnyAsync();
        Assert.Equal(NetViewModel.TryAnyQuestion, asked);
        Assert.False(net.AnyActive);   // «Нет» — ничего не включилось

        answer = true;
        await net.TryAnyAsync();
        Assert.True(net.AnyActive);
        Assert.False(net.TryAnyCmd.CanExecute(null));   // уже идёт — второй раз не предлагаем
    }

    [Fact]
    public async Task AllAddresses_CountsDown_AndRevertsWithNotice()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var (f, link) = await Helper(shell);
        await using var _ = f;
        var net = new NetViewModel(link);
        await net.SetModeAsync(IpsetMode.List);
        await link.Client!.StartAllAddressesAsync();
        await link.FetchAsync(full: false);
        net.Refresh();
        Assert.True(net.AnyActive);
        Assert.False(net.GameCanToggle);   // игры при «Все адреса» не включить
        Assert.Matches("осталось (9|10):[0-5][0-9]", net.AnyText);
        Assert.Contains("«Только из списков»", net.AnyText);

        string? notice = null;
        net.Notify += s => notice = s;
        shell.State.IpsetAnyUntil = DateTime.UtcNow.AddSeconds(-1);
        await net.RevertExpiredAsync();

        Assert.True(net.IpsetList);
        Assert.False(net.AnyActive);
        Assert.Contains("вернулся режим «Только из списков»", notice);
    }

    [Fact]
    public async Task UserSubnets_BadLinesRejected_WithReasons()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var (f, link) = await Helper(shell);
        await using var _ = f;
        var net = new NetViewModel(link) { UserText = "203.0.113.0/24\nне адрес\n10.0.0.0/8\n198.51.100.7" };
        await net.SaveUserAsync();

        var saved = shell.IpLists.Read(IpLists.UserFile);
        Assert.Equal(2, saved.Count);
        Assert.Contains("203.0.113.0/24", saved);
        Assert.Contains("Не приняты (2)", net.UserMessage);
        Assert.Contains("частная сеть", net.UserMessage);
        // На экране — сохранённое так, как его записал помощник.
        Assert.Equal(saved, net.UserText.Split(Environment.NewLine));
    }

    /// <summary>Помощника нет: действия зовут установку (здесь — отказ), ничего не меняется и не падает.</summary>
    [Fact]
    public async Task WithoutHelper_ActionsOfferInstall_AndChangeNothing()
    {
        var link = new HelperLink
        {
            Connector = _ => throw new HelperUnavailableException(HelperUnavailableReason.NotInstalled),
            Setup = new NoSetup(),
        };
        await link.ConnectAsync();
        Assert.Equal(HelperState.NotInstalled, link.State);
        int asked = 0;
        Task<HelperClient?> Need() { asked++; return Task.FromResult(link.Client); }
        var net = new NetViewModel(link) { Need = Need };
        var hosts = new HostsViewModel(link) { Need = Need, Confirm = _ => true };

        await net.SetGameAsync(true);
        Assert.False(net.GameOn);
        hosts.Enabled = true;   // включить можно и без помощника: это черновик окна
        await hosts.CheckAsync();
        Assert.Empty(hosts.Rows);
        Assert.Equal(2, asked);
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

    /// <summary>hosts — копия во временной папке; пишет её помощник (Shell.Hosts), окно только командует.</summary>
    static async Task<(Shell, HostsViewModel, string Hosts, List<string> Questions, HelperFixture)> Hosts(TempDir t, params string[] ok)
    {
        var paths = Make.Paths(t);
        var shell = new Shell(paths);
        var hosts = t.Sub("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        var cands = new List<HostsCandidate> { new("github.com", ["140.82.121.3", "140.82.121.4"], ["GITHU"], "r", "s", "s") };
        shell.Hosts = new HostsManager(paths, hosts, new Probe(ok), new Dns("185.199.108.133"), new NoRdap(), () => cands);
        var (f, link) = await Helper(shell);
        var questions = new List<string>();
        var vm = new HostsViewModel(link) { Confirm = q => { questions.Add(q); return true; } };
        vm.RefreshNow();
        return (shell, vm, hosts, questions, f);
    }

    [Fact]
    public async Task Hosts_OffByDefault_CheckPreviewApplyRevert()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, questions, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _ = f;
        Assert.False(vm.Enabled);
        vm.Enabled = true;
        Assert.True(vm.Enabled);
        Assert.False(shell.State.HostsFix);   // у помощника включится с первой записью

        await vm.CheckAsync();
        Assert.Equal(2, vm.Rows.Count);
        Assert.Equal("записать", vm.Rows[0].Verdict);
        Assert.Equal("не подходит", vm.Rows[1].Verdict);
        Assert.Contains("не отвечает", vm.Rows[1].Reason);
        Assert.True(vm.CanWrite);
        Assert.Equal(["+ 140.82.121.3 github.com"], vm.PreviewLines);
        Assert.Equal("Записать адресов: 1", vm.ApplyText);   // число на кнопке = строк в предпросмотре
        Assert.DoesNotContain("DpiShell", File.ReadAllText(hosts));   // предпросмотр не пишет

        await vm.ApplyAsync();
        Assert.Contains("HostsFileHijack", questions.Last());
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(hosts));
        Assert.True(vm.BlockPresent);
        Assert.StartsWith("Записано адресов: 1", vm.StatusText);
        Assert.NotNull(shell.State.HostsCheckedAt);
        Assert.True(shell.State.HostsFix);
        Assert.True(vm.Enabled);

        // Блок сняли не мы (Защитник) — понятное предупреждение.
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        await vm.RefreshAsync();
        Assert.Contains(vm.Warnings, w => w.Contains("Защитник"));

        await vm.CheckAsync();
        await vm.ApplyAsync();
        await vm.RevertAsync();
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
        Assert.False(vm.BlockPresent);
        Assert.False(shell.State.HostsFix);
        Assert.False(vm.Enabled);
    }

    [Fact]
    public async Task Hosts_NothingToFix_IsSaidPlainly()
    {
        using var t = new TempDir();
        // DNS-адрес тоже открывается — запись не нужна.
        var (_, vm, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3", "github.com@185.199.108.133");
        await using var _f = f;
        vm.Enabled = true;
        await vm.CheckAsync();
        Assert.Equal("Нечего чинить: всё открывается и так.", vm.Summary);
        Assert.False(vm.CanWrite);
        Assert.Equal("не нужно", vm.Rows[0].Verdict);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
    }

    /// <summary>Помощник пишет только то, что сам проверил: запись без проверки — отказ, hosts не тронут.</summary>
    [Fact]
    public async Task Hosts_ApplyWithoutCheck_IsRefusedByHelper()
    {
        using var t = new TempDir();
        var (_, _, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _f = f;
        await using var c = await f.ConnectAsync();
        var e = await Assert.ThrowsAsync<HelperException>(() => c.HostsApplyAsync());
        Assert.Contains("проверьте", e.Message);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
    }

    [Fact]
    public async Task Hosts_Recheck_OnlyWhenOnAndDue()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _f = f;
        await vm.RecheckIfDueAsync();             // выключено — ничего
        Assert.Null(shell.State.HostsCheckedAt);

        vm.Enabled = true;
        await vm.CheckAsync();
        await vm.ApplyAsync();
        shell.State.HostsCheckedAt = DateTime.Now.AddDays(-2);
        await vm.RefreshAsync();
        string? notice = null;
        vm.Notify += s => notice = s;
        // Адрес перестал отвечать — перепроверка снимает его и говорит об этом.
        shell.Hosts = new HostsManager(shell.Paths, hosts, new Probe(), new Dns("185.199.108.133"), new NoRdap(),
            () => [new("github.com", ["140.82.121.3"], ["GITHU"], "r", "s", "s")]);
        await vm.RecheckIfDueAsync();
        Assert.True(shell.State.HostsCheckedAt > DateTime.Now.AddMinutes(-1));
        Assert.Contains("github.com устарел", notice);
        Assert.DoesNotContain("140.82.121.3", File.ReadAllText(hosts));
    }
}
