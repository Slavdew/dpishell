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
        Assert.StartsWith("Включится вместе с оптимизацией", net.GameStatus);   // оптимизация выключена
        Assert.Contains("подсетей, обновлён", net.ListText);
        Assert.Contains("по списку адресов ниже ↓ (", net.GameStatus);   // подпись-связка с IP-списком
        Assert.EndsWith(" подсетей)", net.GameStatus);

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
        // UX-аудит 6, п. 4: десятиминутная проверка названа так, чтобы её не спутать с режимом «Только из списков».
        Assert.StartsWith(NetViewModel.AnyName + ": осталось", net.AnyText);
        Assert.Equal("Все адреса на 10 минут (проверка)", NetViewModel.AnyName);
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
    public async Task Hosts_ForeignRecord_WarnsOnlyWhenEnabled()
    {
        using var t = new TempDir();
        var (_, vm, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _f = f;
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n10.0.0.5 github.com\r\n");
        await vm.RefreshAsync();
        Assert.DoesNotContain(vm.Warnings, w => w.Contains("чужая запись"));   // функция выключена — тревоги без действия нет
        vm.Enabled = true;
        await vm.RefreshAsync();
        Assert.Contains(vm.Warnings, w => w == "В hosts есть чужая запись для github.com — если сайт не откроется, дело может быть в ней.");
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

    // ---------- ТЗ 12: примерить строки, свои строки, проверка hosts ----------

    const string Guide = "127.0.0.1 localhost\r\n0.0.0.0 ads.example.com\r\n";

    [Fact]
    public async Task Hosts_Try_Verdicts_WriteSelected_RestoreAll_ByteForByte()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, questions, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _f = f;
        File.WriteAllText(hosts, Guide);
        var original = File.ReadAllBytes(hosts);

        vm.PasteText = "# гайд\r\n140.82.121.3 github.com\r\n0.0.0.0 gone.example.com\r\n9.9.9.9 dead.example.com\r\nпросто текст\r\n";
        await vm.TryAsync();
        Assert.Equal(["Поможет", "Не поможет", "Блокирует"], vm.TryRows.Select(r => r.Verdict));
        Assert.Equal(["Строка чинит", "Здесь нужна оптимизация, а не hosts", "Блокировка (ничего не меняет)"], vm.TryRows.Select(r => r.Reason));
        Assert.True(vm.TryRows[0].Selected);                                   // чинящая отмечена
        Assert.False(vm.TryRows[1].CanSelect || vm.TryRows[2].CanSelect);     // остальные записать нельзя
        Assert.Equal("Записать выбранные (1)", vm.WriteSelectedText);
        Assert.StartsWith("Проверено строк: 3. можно записать: 1", vm.TrySummary);
        Assert.Contains("Пропущено строк без адреса или имени: 1", vm.TrySummary);
        Assert.Equal(original, File.ReadAllBytes(hosts));                      // примерка файл не меняет

        vm.TryRows[0].Selected = false;
        Assert.Equal("Записать выбранные", vm.WriteSelectedText);
        vm.TryRows[0].Selected = true;
        await vm.WriteSelectedAsync();
        Assert.Contains("DpiShell user", questions.Last());
        Assert.Contains("HostsFileHijack", questions.Last());
        var text = File.ReadAllText(hosts);
        Assert.Contains("140.82.121.3 github.com  # dpishell-user", text);
        Assert.DoesNotContain("gone.example.com", text);
        Assert.StartsWith(Guide, text);
        Assert.Single(vm.UserRows);
        Assert.Equal("github.com → 140.82.121.3", vm.UserRows[0].Text);
        Assert.True(vm.HasOurs);
        Assert.False(shell.State.HostsFix);   // блок проверенных адресов — отдельная функция, она не включилась

        // Другая программа дописала строку; «Вернуть как было» — исходник плюс её строка.
        File.AppendAllText(hosts, "8.8.4.4 other.app.example\r\n");
        await vm.RestoreAllAsync();
        Assert.Equal([.. original, .. "8.8.4.4 other.app.example\r\n"u8.ToArray()], File.ReadAllBytes(hosts));
        Assert.Empty(vm.UserRows);
        Assert.False(vm.HasOurs);
    }

    [Fact]
    public async Task Hosts_Try_EmptyPaste_SaysWhy_WithoutHelper()
    {
        using var t = new TempDir();
        var (_, vm, _, _, f) = await Hosts(t);
        await using var _f = f;
        vm.PasteText = "просто текст";
        await vm.TryAsync();
        Assert.Empty(vm.TryRows);
        Assert.Contains("Не нашла ни одной строки", vm.TrySummary);
    }

    [Fact]
    public async Task Hosts_RemoveUser_OneByOne_KeepsOthers()
    {
        using var t = new TempDir();
        var (_, vm, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3", "github.com@140.82.121.4");
        await using var _f = f;
        vm.PasteText = "140.82.121.3 github.com\r\n140.82.121.4 github.com";
        await vm.TryAsync();
        await vm.WriteSelectedAsync();
        Assert.Equal(2, vm.UserRows.Count);

        await vm.RemoveUserAsync(vm.UserRows[0]);
        Assert.Single(vm.UserRows);
        var left = File.ReadAllText(hosts);
        Assert.DoesNotContain("140.82.121.3", left);
        Assert.Contains("140.82.121.4 github.com", left);

        await vm.RemoveUserAsync(null);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
        Assert.False(vm.HasUserRows);
    }

    [Fact]
    public async Task Hosts_Audit_Comment_OnlyChosen_AndUncomment()
    {
        using var t = new TempDir();
        // DNS (185.199.108.133) для github открывается, старый адрес из hosts — нет: строка «Сломает».
        var (_, vm, hosts, questions, f) = await Hosts(t, "github.com@185.199.108.133");
        await using var _f = f;
        File.WriteAllText(hosts, Guide + "140.82.112.4 github.com\r\n");
        var original = File.ReadAllBytes(hosts);

        await vm.AuditAsync();
        var bad = Assert.Single(vm.AuditRows, r => r.CanSelect);
        Assert.Equal("Сломает", bad.Verdict);
        Assert.Equal("Сайт перестанет открываться", bad.Reason);
        Assert.False(bad.Selected);                                   // решает человек: по умолчанию ничего не отмечено
        Assert.False(vm.CommentCmd.CanExecute(null));
        Assert.StartsWith("Мешают строки: 1.", vm.AuditSummary);
        Assert.Equal(original, File.ReadAllBytes(hosts));

        vm.SelectProblemsCmd.Execute(null);
        Assert.True(bad.Selected);
        Assert.Equal("Закомментировать выбранные (1)", vm.CommentText);
        await vm.CommentAsync();
        Assert.Contains("# DpiShell off", questions.Last());
        var text = File.ReadAllText(hosts);
        Assert.Contains($"# DpiShell off {DateTime.Now:yyyy-MM-dd}: 140.82.112.4 github.com", text);
        Assert.Contains("0.0.0.0 ads.example.com\r\n", text);        // чужая строка рядом не тронута
        Assert.Single(vm.OffRows);
        Assert.DoesNotContain(vm.AuditRows, r => r.CanSelect);
        Assert.True(vm.HasOurs);

        await vm.UncommentAsync();
        Assert.Equal(original, File.ReadAllBytes(hosts));
        Assert.Empty(vm.OffRows);
    }

    [Fact]
    public async Task Hosts_Comment_WithoutAudit_IsRefusedByHelper_AndFileUntouched()
    {
        using var t = new TempDir();
        var (_, _, hosts, _, f) = await Hosts(t);
        await using var _f = f;
        File.WriteAllText(hosts, Guide);
        await using var c = await f.ConnectAsync();
        var id = HostsText.LineId(2, "0.0.0.0 ads.example.com");
        var e = await Assert.ThrowsAsync<HelperException>(() => c.HostsCommentAsync([id]));
        Assert.Contains("проверьте hosts", e.Message);
        Assert.Equal(Guide, File.ReadAllText(hosts));

        // И после аудита: «не мешает» (заглушка на сайт, который не открывался) — не из мешающих, не трогаем.
        await c.HostsAuditAsync();
        var r = await c.HostsCommentAsync([id]);
        Assert.False(r.Changed);
        Assert.Contains("не из последней проверки", r.Skipped[0]);
        Assert.Equal(Guide, File.ReadAllText(hosts));
    }

    [Fact]
    public async Task Hosts_StaleUserLine_NotifiesOnce_ShowsWarning_AndIsNotRemoved()
    {
        using var t = new TempDir();
        var (shell, vm, hosts, _, f) = await Hosts(t, "github.com@140.82.121.3");
        await using var _f = f;
        vm.PasteText = "140.82.121.3 github.com";
        await vm.TryAsync();
        await vm.WriteSelectedAsync();
        Assert.DoesNotContain(vm.Warnings, w => w.Contains("больше не работает"));

        // Адрес перестал отвечать; прошли сутки.
        shell.Hosts = new HostsManager(shell.Paths, hosts, new Probe(), new Dns("185.199.108.133"), new NoRdap(), () => []);
        shell.State.HostsCheckedAt = DateTime.Now.AddDays(-2);
        await vm.RefreshAsync();
        var notices = new List<string>();
        vm.Notify += s => notices.Add(s);
        await vm.RecheckIfDueAsync();
        Assert.Single(notices);
        Assert.Contains("Адрес для github.com больше не работает", notices[0]);
        Assert.Contains("140.82.121.3 github.com", File.ReadAllText(hosts));      // сами не убираем
        Assert.Contains(vm.Warnings, w => w.StartsWith("Адрес для github.com больше не работает"));
        Assert.True(vm.UserRows[0].Stale);

        // Завтра то же самое — тревога на экране остаётся, но уведомления второй раз нет.
        shell.State.HostsCheckedAt = DateTime.Now.AddDays(-2);
        await vm.RefreshAsync();
        await vm.RecheckIfDueAsync();
        Assert.Single(notices);
        Assert.Contains(vm.Warnings, w => w.Contains("github.com больше не работает"));
    }
}
