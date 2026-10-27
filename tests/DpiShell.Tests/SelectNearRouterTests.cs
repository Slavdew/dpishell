using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Подбор на ПК рядом с работающим роутером (architecture.md «Подбор на ПК рядом с роутером»): цели не пересекаются с тем, что ведёт роутер, —
/// без вопроса и без выключения роутера, только по своим сайтам (ownOnly); пересекаются — вопрос называет, что именно; не узнать — как раньше.
/// Подделки роутера и помощника, настоящих диалогов, сети и служб нет.
/// </summary>
public sealed class SelectNearRouterTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    // ---------- разбор: что пересекается ----------

    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> Lists = new Dictionary<string, IReadOnlyList<string>>
    {
        ["google"] = ["youtube.com", "googlevideo.com", "google.ru"],
        ["discord"] = ["discord.com", "discord.gg"],
        ["general"] = ["^dns.google", "rutracker.org"],
        ["user-general"] = ["router-only.example"],
    };

    static readonly string[] Decoys = ["www.google.com", "www.microsoft.com", "4pda.to"];

    [Fact]
    public void Overlap_all_sites_mode_is_everything()
    {
        var o = SelectOverlap.Of("all", null, Lists, ["user"], ["mysite.example"], Decoys);
        Assert.True(o.AllSites);
        Assert.False(o.None);
        Assert.True(SelectOverlap.Of(null, null, Lists, ["user"], [], Decoys).AllSites);   // режим не прислан — считаем «всё»
    }

    [Fact]
    public void Overlap_own_sites_only_with_lists_mode_is_none()
    {
        var o = SelectOverlap.Of("lists", null, Lists, ["user"], ["mysite.example", "https://www.other.example/"], Decoys);
        Assert.True(o.None);
    }

    [Fact]
    public void Overlap_names_youtube_discord_and_sites_from_router_lists()
    {
        var o = SelectOverlap.Of("lists", null, Lists, ["youtube", "discord", "user"], ["mysite.example", "m.youtube.com", "rutracker.org", "router-only.example", "discord.media"], Decoys);
        Assert.Equal(["YouTube", "Discord", "m.youtube.com", "rutracker.org", "router-only.example", "discord.media"], o.Items);
        Assert.False(o.None);
    }

    [Fact]
    public void Overlap_router_advanced_mode_others_all_direct_and_rows()
    {
        var all = new RouterAdvanced(true, AdvancedSites.OthersAll, []);
        Assert.True(SelectOverlap.Of("lists", all, Lists, ["user"], [], Decoys).AllSites);
        // «Остальные — напрямую»: роли каталога на роутере выключены, он ведёт только строки (кроме «напрямую»).
        var direct = new RouterAdvanced(true, AdvancedSites.OthersDirect,
            [new(["row.example"], "alt10"), new(["plain.example"], AdvancedSites.Direct)]);
        var o = SelectOverlap.Of("all", direct, Lists, ["youtube", "user"], ["row.example", "plain.example", "youtube.com"], Decoys);
        Assert.Equal(["row.example"], o.Items);
        // Продвинутый режим выключен — действует mode.
        Assert.True(SelectOverlap.Of("all", new RouterAdvanced(false, AdvancedSites.OthersLists, []), Lists, ["user"], [], Decoys).AllSites);
    }

    [Fact]
    public void Overlap_decoy_name_in_router_lists_is_a_clash()
    {
        var lists = new Dictionary<string, IReadOnlyList<string>>(Lists) { ["user-general"] = ["4pda.to"] };
        var o = SelectOverlap.Of("lists", null, lists, ["user"], ["mysite.example"], Decoys);
        Assert.True(o.DecoyClash);
        Assert.Empty(o.Items);
        Assert.False(o.None);
    }

    [Fact]
    public void OwnOnly_build_has_no_youtube_discord_or_ip_roles()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("b"), Repo.Data));
        var s = shell.Catalog.Strategies.First(x => x.Roles.ContainsKey("google") && x.Roles.ContainsKey("general"));
        var args = string.Join(' ', WinwsArgs.Build(s, shell.Paths, new BuildOptions(Joint: new JointBuild([]))));
        Assert.DoesNotContain("google.txt", args);
        Assert.DoesNotContain("discord.txt", args);
        Assert.DoesNotContain("general.txt", args.Replace("user-general.txt", ""));
        Assert.Contains("user-general.txt", args);
    }

    // ---------- окно ----------

    static FakeRouter NewRouter(string mode = "lists")
    {
        var r = new FakeRouter { WarningsJson = "[]", Mode = mode }.As020();
        r.UpdateFieldOn = false;
        return r;
    }

    async Task<(MainViewModel Vm, HelperFixture F)> OpenAsync(FakeRouter router, IReadOnlyList<string> own)
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("root-" + Guid.NewGuid().ToString("N")[..6]), Repo.Data));
        var f = new HelperFixture();
        f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, Service = SvcState.Stopped, StrategyId = "alt9", UserTargets = [.. own] };
        f.Backend.JointValue = f.Backend.JointValue with { Own = own, Work = own };
        var vm = TestWindow.Model(shell, await f.LinkAsync());
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        await vm.RefreshStatusAsync();
        vm.Router.AddressAuto = false;
        vm.Router.CustomAddress = router.Address;
        vm.Router.PcTimestamps = () => true;
        vm.Router.OwnMac = _ => "aa:bb:cc:dd:ee:01";
        await vm.Router.ConnectAsync("secret");
        Assert.True(vm.Router.Connected && vm.Router.RouterEnabled);
        return (vm, f);
    }

    static int Count(FakeRouter r, string method) { lock (r.Calls) return r.Calls.Count(c => c["params"]![2]!.GetValue<string>() == method); }

    [Fact]
    public async Task OwnSitesOnly_RouterLeadsYoutubeDiscord_SelectsOnPcWithoutQuestion()
    {
        using var router = NewRouter();
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };
        Assert.True(await vm.RouterGateAsync(select: true, ["user"]));
        Assert.Null(asked);
        Assert.True(vm.SelectOwnOnly);
        Assert.True(router.Enabled);
        Assert.Equal(0, Count(router, "disable"));
        // После подбора роутер не выключали — и вопроса «включить обратно» нет.
        vm.Confirm = q => { asked = q; return true; };
        await vm.RouterResumeAsync();
        Assert.Null(asked);
    }

    [Fact]
    public async Task OwnOnly_ReachesHelper_OnlyWhenAsked()
    {
        await using var f = new HelperFixture();
        var link = await f.LinkAsync();
        Assert.True(await link.ConnectAsync());
        var c = link.Client!;
        await c.StrategyCheckAsync("alt9", ["user"], ownOnly: true);
        Assert.True(f.Backend.SelectOwnOnly);
        await c.StrategyCheckAsync("alt9", ["user"]);
        Assert.False(f.Backend.SelectOwnOnly);
    }

    [Fact]
    public async Task YoutubeAndDiscordChecked_QuestionNamesThem()
    {
        using var router = NewRouter();
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };
        Assert.False(await vm.RouterGateAsync(select: true, ["youtube", "discord", "user"]));
        Assert.Equal(L.T("router.select_choice.overlap", new { what = "YouTube и Discord" }), asked);
        Assert.StartsWith("Роутер уже оптимизирует YouTube и Discord", asked);
        Assert.False(vm.SelectOwnOnly);
        Assert.True(router.Enabled);
    }

    [Fact]
    public async Task OwnSiteInRouterList_QuestionNamesTheSite_PauseStillWorks()
    {
        using var router = NewRouter();
        var (vm, f) = await OpenAsync(router, ["mysite.example", "youtube.com"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return 1; };
        Assert.True(await vm.RouterGateAsync(select: true, ["user"]));
        Assert.Equal(L.T("router.select_choice.overlap", new { what = "youtube.com" }), asked);
        Assert.False(vm.SelectOwnOnly);
        Assert.False(router.Enabled);   // «Выключить на роутере на время подбора» — как раньше
    }

    [Fact]
    public async Task RouterAllSites_GenericQuestion()
    {
        using var router = NewRouter(mode: "all");
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };
        Assert.False(await vm.RouterGateAsync(select: true, ["user"]));
        Assert.Equal(L.T("router.select_choice.text"), asked);
        Assert.False(vm.SelectOwnOnly);
    }

    [Fact]
    public async Task RouterWithoutBuiltinLists_UnknownSoGenericQuestion()
    {
        using var router = NewRouter();
        router.BuiltinListsOn = false;   // роутер до r9: встроенных списков не отдаёт — что он ведёт, наверняка не узнать
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };
        Assert.False(await vm.RouterGateAsync(select: true, ["user"]));
        Assert.Equal(L.T("router.select_choice.text"), asked);
    }

    [Fact]
    public async Task NoGroupsGiven_AsBefore()
    {
        using var router = NewRouter();
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        string? asked = null;
        vm.Choose = (t, _) => { asked = t; return -1; };
        Assert.False(await vm.RouterGateAsync(select: true));
        Assert.Equal(L.T("router.select_choice.text"), asked);
    }

    [Fact]
    public async Task ApplyAfterOwnOnlySelect_RouterOn_AsksLikeEnable_NotEnablingFullPcConfig()
    {
        using var router = NewRouter();
        var (vm, f) = await OpenAsync(router, ["mysite.example"]);
        await using var _ = f;
        IReadOnlyList<string>? options = null;
        vm.ChooseDetailed = r => { options = r.Options.Select(o => o.Title).ToList(); return -1; };
        var apply = typeof(MainViewModel).GetMethod("ApplyNearRouterAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert.False(await (Task<bool>)apply.Invoke(vm, ["alt10"])!);
        Assert.Equal(new[] { L.T("router.choice.joint"), L.T("router.choice.select_router"), L.T("router.choice.only_pc") }, options);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("enable"));
        Assert.True(router.Enabled);
        Assert.Equal("alt10", vm.CurrentStrategyId);   // выбор запомнен: «Включить» включит её
    }
}
