using System.Security.Authentication;
using System.Text.Json;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Export;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Группы «Своих сайтов» (API 1.17): модель, выборка, блокировка по адресу вне оценки, сводка, история, выгрузка и окно.</summary>
public sealed class SiteGroupsTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    // ---------- модель ----------

    [Fact]
    public void Normalize_merges_names_dedups_sites_and_puts_other_last()
    {
        var groups = SiteGroups.Normalize(
        [
            new("Telegram", ["telegram.org", "t.me"]),
            new("", ["plain.example", "t.me"]),                    // t.me уже в Telegram
            new("  telegram ", ["web.telegram.org", "не домен!!"]),  // та же группа без учёта регистра и пробелов
            new("Spotify", ["spotify.com"]),
        ], out var rejected);
        Assert.Equal(["Telegram", "Spotify", ""], groups.Select(g => g.Name));
        Assert.Equal(["telegram.org", "t.me", "web.telegram.org"], groups[0].Sites);
        Assert.Equal(["plain.example"], groups[2].Sites);
        Assert.Equal(["не домен!!"], rejected);
    }

    [Fact]
    public void Sample_is_stable_whatever_the_order_and_capped()
    {
        var sites = Enumerable.Range(1, 120).Select(i => $"site{i}.example").ToList();
        var a = SiteGroups.Sample(sites);
        var b = SiteGroups.Sample([.. sites.AsEnumerable().Reverse()]);
        Assert.Equal(SiteGroups.SampleSize, a.Count);
        Assert.Equal(a.Order(), b.Order());
        Assert.Equal(sites.Take(5), SiteGroups.Sample([.. sites.Take(5)]));   // короткий список — целиком
    }

    [Fact]
    public void UserGroups_targets_carry_group_name_and_sample_each_group()
    {
        var big = Enumerable.Range(1, 120).Select(i => $"big{i}.example").ToList();
        var g = TargetSet.UserGroups([new("Telegram", big), new("", ["a.example", "b.example"])]);
        Assert.Equal("user", g.Id);
        Assert.Equal(10, g.Targets.Count(t => t.Group == "Telegram"));
        Assert.Equal(2, g.Targets.Count(t => t.Group is null));
        Assert.Equal(120, g.Totals!["Telegram"]);
        Assert.Equal(2, g.Totals[""]);
        // Те же адреса при втором построении — выборка устойчива.
        var again = TargetSet.UserGroups([new("Telegram", big.AsEnumerable().Reverse()), new("", ["b.example", "a.example"])]);
        Assert.Equal(g.Targets.Select(t => t.Url).Order(), again.Targets.Select(t => t.Url).Order());
        Assert.Equal(120, TargetSet.UserGroups([new("T", big)], int.MaxValue).Targets.Count);
    }

    [Fact]
    public void Shell_keeps_groups_for_remaining_sites_and_old_state_reads()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        var (saved, rejected) = shell.SetUserGroups([new("Telegram", ["telegram.org", "t.me"]), new("", ["plain.example", "оба!!"])]);
        Assert.Equal(["Telegram", ""], saved.Select(g => g.Name));
        Assert.Contains("оба!!", rejected);
        Assert.Equal(["telegram.org", "t.me", "plain.example"], shell.State.UserTargets);
        // Плоский список (старый путь) группы оставшимся сайтам не стирает, ушедшие сайты уходят и из групп.
        shell.SetUserTargets(["t.me", "plain.example", "new.example"]);
        var groups = shell.UserSiteGroups();
        Assert.Equal(["t.me"], groups[0].Sites);
        Assert.Equal(["plain.example", "new.example"], groups[^1].Sites);
        // Файл для winws остаётся плоским списком доменов.
        Assert.Equal(["t.me", "plain.example", "new.example"], shell.Data.ReadUserDomains());
        // Состояние без поля групп (файл прежней версии) читается как «все — Прочее».
        File.WriteAllText(shell.Paths.StateFile, "{\"user_targets\":[\"x.example\"]}");
        var old = AppState.Load(shell.Paths);
        Assert.Empty(old.UserGroups);
        Assert.Equal(["x.example"], SiteGroups.Apply(old.UserTargets, old.UserGroups)[^1].Sites);
        // Новое состояние в файле: прежняя версия лишнего поля не знает (по именам свойств видно, что user_targets остался полным списком).
        shell.SetUserGroups([new("Telegram", ["t.me"]), new("", ["plain.example"])]);
        using var doc = JsonDocument.Parse(File.ReadAllText(shell.Paths.StateFile));
        Assert.Equal(2, doc.RootElement.GetProperty("user_targets").GetArrayLength());
        Assert.True(doc.RootElement.TryGetProperty("user_groups", out _));
    }

    // ---------- блокировка по адресу ----------

    static ProbeResult P(Target t, ProbeOutcome o, SslProtocols tls) => new(t, tls, o, 0.1, 0, "", NoTcp: o == ProbeOutcome.ConnectFailed);

    static TargetVerdict V(Target t, ProbeOutcome o, string group = "user") =>
        new(group, t, o == ProbeOutcome.Ok, o == ProbeOutcome.Ok ? 0.2 : 0, [P(t, o, SslProtocols.Tls12), P(t, o, SslProtocols.Tls13)]);

    static readonly Target Dead = new("https://dead.example/", Group: "Telegram");
    static readonly Target Alive = new("https://alive.example/", Group: "Telegram");
    static readonly Target Other = new("https://other.example/");

    static StrategyScore Score(string id, ProbeOutcome alive, ProbeOutcome dead = ProbeOutcome.ConnectFailed, ProbeOutcome other = ProbeOutcome.Ok)
    {
        var g = new TargetGroup("user", "Свои сайты", [Dead, Alive, Other]);
        return Selector.Score(new Strategy(id, id, "test", new Dictionary<string, string>()), [g], [V(Dead, dead), V(Alive, alive), V(Other, other)]);
    }

    [Fact]
    public void Address_blocked_is_not_a_strategy_failure_and_leaves_the_score()
    {
        var group = new TargetGroup("user", "Свои сайты", [Dead, Alive, Other]);
        var baseline = new[] { V(Dead, ProbeOutcome.ConnectFailed), V(Alive, ProbeOutcome.Timeout), V(Other, ProbeOutcome.Timeout) };
        var scores = new[] { Score("a", ProbeOutcome.Ok), Score("b", ProbeOutcome.BadStatus) };
        Assert.Equal(0, scores[0].GroupsPassed);                       // пока dead считается: группа не прошла

        var gone = AddressBlocks.Find(baseline, scores);
        Assert.Equal([AddressBlocks.Key(V(Dead, ProbeOutcome.Ok))], gone);
        var a = AddressBlocks.Rescore(scores[0], [group], gone);
        Assert.True(a.Perfect);
        Assert.Equal(2, a.Verdicts.Count);
        Assert.Equal(Dead.Url, Assert.Single(a.Blocked).Target.Url);
        Assert.Equal(1.0, a.TargetShare);

        // Одна стратегия — мало для вывода; тайм-аут вместо «нет соединения» — не блокировка по адресу.
        Assert.Empty(AddressBlocks.Find(baseline, [scores[0]]));
        Assert.Empty(AddressBlocks.Find([V(Dead, ProbeOutcome.Timeout)], [Score("a", ProbeOutcome.Ok, dead: ProbeOutcome.Timeout), Score("b", ProbeOutcome.Ok, dead: ProbeOutcome.Timeout)]));
        // Открылся у одной стратегии — это уже не блокировка по адресу.
        Assert.Empty(AddressBlocks.Find(baseline, [scores[0], Score("c", ProbeOutcome.Ok, dead: ProbeOutcome.Ok)]));
    }

    sealed class FakeRun(AppPaths paths, HttpProbe probe) : Selector(paths, probe)
    {
        public override Task<StrategyScore> RunOneAsync(Strategy s, IReadOnlyList<TargetGroup> groups, CancellationToken ct)
        {
            var verdicts = groups.SelectMany(g => g.Targets.Select(t => V(t, t.Url.Contains("dead") ? ProbeOutcome.ConnectFailed : ProbeOutcome.Ok, g.Id))).ToList();
            return Task.FromResult(Score(s, groups, verdicts));
        }
    }

    [Fact]
    public async Task Run_excludes_address_blocked_from_choice_and_stops_quick_run_early()
    {
        // Без обхода: dead — порт закрыт (нет соединения), alive — тоже не открывается (сервера нет → тоже «нет соединения»), поэтому проверяем через подмену базовой.
        var paths = Make.Paths(_tmp);
        var probe = new HttpProbe { Attempts = 1, ConnectTimeout = TimeSpan.FromSeconds(1), ReadTimeout = TimeSpan.FromSeconds(1) };
        var selector = new FakeRun(paths, probe) { Tls = [SslProtocols.Tls12, SslProtocols.Tls13], Settle = TimeSpan.Zero };
        var user = new TargetGroup("user", "Свои сайты",
        [
            new("https://127.0.0.1:1/dead", Group: "Telegram"),     // закрытый порт: нет соединения
            new("https://127.0.0.1:1/alive", Group: "Telegram"),    // и этот — но «живой» для подмены стратегий
        ]);
        var strategies = Enumerable.Range(1, 4).Select(i => new Strategy($"s{i}", $"S{i}", "test", new Dictionary<string, string>())).ToList();
        // Подмена: 'alive' у стратегий открывается, но без обхода его нет; 'dead' — нет соединения всегда.
        var r = await selector.RunAsync(strategies, [user], quick: false);
        Assert.Equal(4, r.Scores.Count);
        // dead и alive различаются только подменой; alive без обхода «нет соединения», но открылся у стратегий — не блокировка.
        Assert.Equal(["https://127.0.0.1:1/dead"], r.AddressBlocked);
        Assert.All(r.Scores, s => Assert.True(s.Perfect));
        Assert.All(r.Scores, s => Assert.Single(s.Blocked));
        Assert.NotNull(r.Best);

        // Быстрый подбор останавливается на второй стратегии: на первой вывод ещё ненадёжен.
        var quick = await selector.RunAsync(strategies, [user], quick: true);
        Assert.Equal(AddressBlocks.MinStrategies, quick.Scores.Count);
        Assert.True(quick.Best!.Perfect);
    }

    // ---------- история и статистика ----------

    [Fact]
    public void History_hides_group_names_keeps_counts_and_old_files_are_other()
    {
        var paths = Make.Paths(_tmp);
        var group = new TargetGroup("user", "Свои сайты", [Dead, Alive, Other], Totals: new() { ["Telegram"] = 120, [""] = 1 });
        var baseline = new[] { V(Dead, ProbeOutcome.ConnectFailed) };
        var scores = new[] { Score("a", ProbeOutcome.Ok), Score("b", ProbeOutcome.BadStatus) };
        var gone = AddressBlocks.Find(baseline, scores);
        var report = new SelectionReport(baseline, [group], [.. scores.Select(s => AddressBlocks.Rescore(s, [group], gone))], false) { AddressBlocked = [Dead.Url] };
        var file = ResultStore.Save(paths, report);
        var json = File.ReadAllText(file);
        Assert.DoesNotContain("Telegram", json);
        Assert.DoesNotContain("dead.example", json);
        Assert.Contains("группа №1", json);

        var st = StrategyStats.LoadDetailed(paths);
        var a = st["a"];
        var subs = a.Last!.Subs;
        Assert.Equal(2, subs.Count);
        var named = subs[0];
        Assert.Equal("группа №1", named.Group);
        Assert.Equal((1, 1, 1, 120), (named.Ok, named.Total, named.Blocked, named.OfAll));
        Assert.True(subs[1].IsOther);
        // Закрытый по адресу не портит долю своих сайтов.
        Assert.Equal(1.0, a.UserShare);
        Assert.Equal(2, a.UserTotal);

        // Файл прежней версии: у адресов нет ни группы, ни пометки.
        File.WriteAllText(Path.Combine(paths.Results, "2020-01-01_00-00-00.json"),
            "{\"scores\":[{\"Id\":\"old\",\"GroupsPassed\":1,\"GroupsTotal\":1,\"targets\":[{\"GroupId\":\"user\",\"Url\":\"свой адрес №1\",\"Ok\":true,\"Required\":true,\"Control\":false,\"Seconds\":1,\"Probes\":[]}]}]}");
        var old = StrategyStats.LoadDetailed(paths)["old"].Last!.Subs;
        Assert.True(Assert.Single(old).IsOther);
    }

    [Fact]
    public void Strategy_line_and_summary_show_a_line_per_group()
    {
        var names = new GroupNames([]) { UserGroupNames = ["Telegram", "Spotify"] };
        var plain = new SubLine("", 7, 9, 0, 9, []);
        Assert.Equal("Прочее ✓ 7/7", Texts.SubShort(new("", 7, 7, 0, 7, []), names));
        Assert.Equal("Telegram ✗ 0/12", Texts.SubShort(new("группа №1", 0, 12, 0, 12, []), names));   // из истории — по текущему имени
        Assert.Equal("Прочее ✓ 7/8 — не открыто: example.org", Texts.SubLineText(new("", 7, 8, 0, 8, ["example.org"]), names));
        var text = Texts.SubLineText(new("Telegram", 10, 10, 1, 120, []), names);
        Assert.Contains("проверено 11 из 120", text);
        Assert.Contains("заблокировано по адресу: 1 — оптимизация не поможет", text);
        Assert.Equal("Прочее", Texts.SubName(plain, names));
    }

    // ---------- выгрузка и загрузка ----------

    [Fact]
    public void Export_writes_groups_as_comments_and_import_reads_them_back()
    {
        var folder = _tmp.Sub("pack");
        var state = new SettingsState
        {
            Sites = ["plain.example", "t.me", "telegram.org"],
            SiteGroups = new() { ["t.me"] = "Telegram", ["telegram.org"] = "Telegram" },
        };
        SettingsExport.Write(folder, state, [SettingsSections.Sites]);
        var text = File.ReadAllText(Path.Combine(folder, SettingsSections.SitesFile));
        Assert.Contains("# group: Telegram", text);
        // Для читателя без групп (роутер, прежняя версия) это обычный список с комментариями.
        Assert.Equal(["plain.example", "t.me", "telegram.org"],
            text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Order());

        var pkg = SettingsImport.ReadPackage(folder);
        Assert.True(pkg.Ok);
        Assert.Equal(3, pkg.Data.Sites!.Count);
        Assert.Equal("Telegram", pkg.Data.SiteGroups!["t.me"]);
        Assert.False(pkg.Data.SiteGroups.ContainsKey("plain.example"));

        // Пакет без групп — прежнее поведение: групп нет.
        var flat = _tmp.Sub("flat");
        SettingsExport.Write(flat, new SettingsState { Sites = ["a.example"] }, [SettingsSections.Sites]);
        Assert.Null(SettingsImport.ReadPackage(flat).Data.SiteGroups);
    }

    [Fact]
    public async Task Import_applies_groups_and_undo_restores_them()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root2"), Repo.Data));
        var b = new ShellHelperBackend(shell);
        shell.SetUserTargets(["mine.example"]);
        var folder = _tmp.Sub("pack2");
        SettingsExport.Write(folder, new SettingsState { Sites = ["t.me", "telegram.org"], SiteGroups = new() { ["t.me"] = "Telegram", ["telegram.org"] = "Telegram" } }, [SettingsSections.Sites]);
        var pkg = SettingsImport.ReadPackage(folder);
        var plan = SettingsImport.Plan(pkg, b.SettingsExportRead(), [SettingsSections.Sites], ImportMode.Add);
        var request = plan.ToRequest();
        Assert.Contains("siteGroups", request);
        Assert.DoesNotContain("siteGroups", plan.ToRequest(groups: false));
        var result = await b.SettingsImportAsync(request, CancellationToken.None);
        Assert.True(result.AllOk);
        Assert.Equal(["t.me", "telegram.org", "mine.example"], shell.State.UserTargets);   // именованные группы, затем «Прочее»
        var groups = shell.UserSiteGroups();
        Assert.Equal("Telegram", groups[0].Name);
        Assert.Equal(["t.me", "telegram.org"], groups[0].Sites);
        Assert.Equal(["mine.example"], groups[^1].Sites);
    }

    // ---------- окно ----------

    MainViewModel Model(HelperLink link)
    {
        var vm = TestWindow.Model(new Shell(new AppPaths(_tmp.Sub("vmroot"), Repo.Data)), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        return vm;
    }

    [Fact]
    public async Task Window_groups_edit_move_delete_and_save_through_helper()
    {
        await using var f = new HelperFixture();
        f.Backend.StatusValue = FakeHelperBackend.DefaultStatus;
        var vm = Model(await f.LinkAsync());
        vm.Confirm = _ => true;
        await vm.RefreshStatusAsync();
        vm.GoTo(Pages.Sites);

        // Без групп — один список «Прочее» без шапок, TargetsText прежний.
        vm.TargetsText = "a.example\r\nb.example";
        Assert.False(vm.GroupHeaders);
        Assert.Equal("a.example" + Environment.NewLine + "b.example", vm.TargetsText);

        var group = vm.NewGroup("Telegram");
        Assert.True(vm.GroupHeaders);
        vm.MoveSites(vm.SiteGroupRows[^1], ["b.example"], group);
        Assert.Equal(["b.example"], group.Lines());
        Assert.Equal(["a.example"], vm.SiteGroupRows[^1].Lines());
        Assert.True(vm.TargetsDirty);

        vm.SaveSitesCmd.Execute(null);
        for (int i = 0; i < 1000 && !f.Backend.Calls.Contains("user_groups_set Telegram:b.example;:a.example"); i++) await Task.Delay(10);
        Assert.Contains("user_groups_set Telegram:b.example;:a.example", f.Backend.Calls);
        for (int i = 0; i < 1000 && vm.TargetsDirty; i++) await Task.Delay(10);
        Assert.False(vm.TargetsDirty);
        group = vm.SiteGroupRows.First(g => g.Named);   // после сохранения строки собраны заново из ответа помощника
        Assert.Equal("Telegram", group.Name);

        // Новый сайт через общий текст (как «Добавить в список оптимизации») попадает в «Прочее», группа не теряется.
        vm.TargetsText = vm.TargetsText + Environment.NewLine + "c.example";
        Assert.Equal(["b.example"], group.Lines());
        Assert.Equal(["a.example", "c.example"], vm.SiteGroupRows[^1].Lines());

        // Удаление группы — вместе с сайтами, после подтверждения.
        vm.Confirm = _ => false;
        Assert.False(vm.DeleteGroup(group));
        Assert.Contains(group, vm.SiteGroupRows);
        vm.Confirm = _ => true;
        Assert.True(vm.DeleteGroup(group));
        Assert.DoesNotContain("b.example", vm.TargetsText);
        Assert.False(vm.GroupHeaders);

        // Имя занято — прежнее остаётся.
        var one = vm.NewGroup("One"); var two = vm.NewGroup("Two");
        two.EditName = "one";
        Assert.False(vm.CommitRename(two));
        Assert.Equal("Two", two.Name);
        two.EditName = "Three";
        Assert.True(vm.CommitRename(two));
        Assert.Equal("Three", two.Name);
    }

    [Fact]
    public void Lines_at_selection_or_caret()
    {
        const string text = "one.example\ntwo.example\nthree.example\n";
        Assert.Equal(["two.example"], SiteGroupRow.LinesAt(text, 14, 0));
        Assert.Equal(["one.example", "two.example"], SiteGroupRow.LinesAt(text, 5, 10));
        Assert.Equal(["one.example", "two.example"], SiteGroupRow.LinesAt(text, 0, 24));   // выделение до конца строки вместе с переводом
    }
}
