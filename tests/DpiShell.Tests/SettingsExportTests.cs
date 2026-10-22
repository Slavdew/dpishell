using System.Net;
using System.Text;
using System.Text.Json;
using DpiShell.Core;
using DpiShell.Core.Export;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Выгрузка и загрузка своих настроек папкой файлов (docs/settings-export.md): круговой обмен, «добавить»/«заменить», битый раздел,
/// чужие стратегии и hosts, пределы, формат другой версии, отказ помощника на подделанный план, журнал и откат.
/// </summary>
public sealed class SettingsExportTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    // ---------- данные ----------

    const string Tls = "--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls={fake}/tls_clienthello_4pda_to.bin";
    const string Split = "--dpi-desync=multisplit --dpi-desync-split-pos=1";
    const string GameTcp = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3";
    static readonly string GameUdp = WinwsArgs.GameUdpDefault;

    static Dictionary<string, string> Roles(string general) => new() { ["general"] = general };

    static SettingsState Env(Action<SettingsState>? tune = null)
    {
        var s = new SettingsState
        {
            FakeFiles = [.. Directory.GetFiles(Path.Combine(Repo.Data, "fake")).Select(f => Path.GetFileName(f)!)],
            Variants = [new ProfileVariant(0, "вариант 1", GameTcp, GameUdp)],
            Strategies = [new Strategy("alt9", "Alt 9", "flowseal", Roles(Split))],
        };
        tune?.Invoke(s);
        return s;
    }

    static AppProfile Profile(string id = "p0000abcd", string title = "Игра", string address = "203.0.113.0/24") => new()
    {
        Id = id, Title = title, TcpPorts = "443", UdpPorts = "27015-27030", Addresses = [address], TcpDesync = GameTcp, UdpDesync = GameUdp, Variant = 0,
    };

    /// <summary>Богатое состояние «отправителя»: во всех разделах что-то есть.</summary>
    static SettingsState Rich() => Env(s =>
    {
        s.Sites = ["site-one.example", "site-two.example"];
        s.Exclude = ["bank.example"];
        s.Strategies.Add(new("own-1", "Моя первая", "user", Roles(Tls)));
        s.Strategies.Add(new("found-1", "Найденная", "search", Roles(Split + " --dpi-desync-repeats=3")));
        s.Strategies.Add(new("mix-1", "Смесь", "mix", Roles(Split + " --dpi-desync-repeats=9")));
        s.Advanced = new()
        {
            Enabled = true, Others = "lists",
            Rows = [new() { Domains = ["game.example"], Strategy = "own-1" }, new() { Domains = ["x.example", "y.example"], Strategy = "auto" },
                new() { Domains = ["z.example"], Strategy = "direct" }, new() { Domains = ["c.example"], Strategy = "alt9" }],
        };
        s.Apps = [Profile()];
        s.Subnets = ["198.51.100.7/32", "203.0.113.0/24"];
        s.SubnetsExclude = ["192.0.2.0/24"];
        s.Hosts = ["140.82.112.3 github.com"];
        s.Dns = new() { Choice = "google" };
    });

    string NewFolder(string name = "pkg") => _t.Sub(name);

    static string Read(string folder, string file) => File.ReadAllText(Path.Combine(folder, file));

    static void Put(string folder, string file, string text)
    {
        var p = Path.Combine(folder, file);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, text, new UTF8Encoding(false));
    }

    ParsedPackage Export(SettingsState s, string name = "pkg", ExportMeta? meta = null, IEnumerable<string>? sections = null)
    {
        var f = NewFolder(name);
        SettingsExport.Write(f, s, sections ?? SettingsSections.All, meta);
        return SettingsImport.Read(f);
    }

    // ---------- формат ----------

    [Fact]
    public void Preview_counts_per_section_and_marks_risky()
    {
        var p = SettingsExport.Preview(Rich()).ToDictionary(x => x.Id);
        Assert.Equal(SettingsSections.All, SettingsExport.Preview(Rich()).Select(x => x.Id));
        Assert.Equal(2, p["sites"].Count);
        Assert.Equal(1, p["exclude"].Count);
        Assert.Equal(2, p["strategies"].Count);          // своя и найденная; готовая и смесь не выгружаются
        Assert.Equal(4, p["advanced"].Count);
        Assert.Equal(1, p["apps"].Count);
        Assert.Equal(3, p["subnets"].Count);
        Assert.Equal(1, p["hosts"].Count);
        Assert.Equal(1, p["dns"].Count);
        Assert.True(p["hosts"].Risky);
        Assert.All(p.Values.Where(x => x.Id != "hosts"), x => Assert.False(x.Risky));
        Assert.Contains("site-one.example", p["sites"].Items);
        Assert.Equal("Свои сайты", p["sites"].Title);
        // Свой DNS — тоже осторожно; пустое состояние — всё по нулям.
        Assert.True(SettingsExport.Preview(Env(s => s.Dns = new() { Choice = "custom", Ips = ["9.9.9.9"] })).Single(x => x.Id == "dns").Risky);
        Assert.All(SettingsExport.Preview(Env()), x => Assert.Equal(0, x.Count));
    }

    [Fact]
    public void Write_makes_manifest_and_one_file_per_section_in_the_documented_layout()
    {
        var f = NewFolder();
        var r = SettingsExport.Write(f, Rich(), SettingsSections.All, new ExportMeta("Провайдер\nN", "Заметка  автора", new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc), "0.10.0"));
        Assert.Equal(SettingsSections.All, r.Sections);
        var m = JsonDocument.Parse(Read(f, "manifest.json")).RootElement;
        Assert.Equal(1, m.GetProperty("format").GetInt32());
        Assert.Equal("DpiShell", m.GetProperty("app").GetString());
        Assert.Equal("0.10.0", m.GetProperty("app_version").GetString());
        Assert.Equal("2026-10-20T12:00:00Z", m.GetProperty("created").GetString());
        Assert.Equal("Провайдер N", m.GetProperty("provider").GetString());   // перевод строки — пробел
        Assert.Equal("Заметка автора", m.GetProperty("note").GetString());
        Assert.Equal(SettingsSections.All, m.GetProperty("sections").EnumerateArray().Select(x => x.GetString()!));
        foreach (var file in new[] { "sites.txt", "exclude.txt", "advanced.json", "subnets.txt", "subnets-exclude.txt", "hosts.txt", "dns.json" })
            Assert.True(File.Exists(Path.Combine(f, file)), file);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(f, "strategies")).Length);
        Assert.Single(Directory.GetFiles(Path.Combine(f, "apps")));
        Assert.StartsWith("#", Read(f, "sites.txt"));
        Assert.Contains("site-one.example\n", Read(f, "sites.txt"));
        Assert.DoesNotContain('\r', Read(f, "sites.txt"));
        Assert.Equal((byte)'#', File.ReadAllBytes(Path.Combine(f, "sites.txt"))[0]);
        // Стратегия — тот же текст, что «Скопировать параметры»; ссылка из advanced.json — на имя файла.
        var own = Directory.GetFiles(Path.Combine(f, "strategies")).Select(File.ReadAllText).Single(t => t.Contains("Моя первая"));
        var line = own.Split('\n').Single(l => l.StartsWith("dpishell:1:"));
        Assert.Equal("Моя первая", StrategyExchange.Decode(line).Title);
        var adv = JsonDocument.Parse(Read(f, "advanced.json")).RootElement;
        Assert.Equal("game.example", adv.GetProperty("rows")[0].GetProperty("domains")[0].GetString());
        Assert.True(File.Exists(Path.Combine(f, "strategies", adv.GetProperty("rows")[0].GetProperty("strategy_file").GetString() + ".txt")));
        Assert.Equal("alt9", adv.GetProperty("rows")[3].GetProperty("strategy").GetString());
        // Не выгружается ничего лишнего.
        var all = string.Concat(Directory.GetFiles(f, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        Assert.DoesNotContain("Смесь", all);
        Assert.DoesNotContain("mix-1", all);
        Assert.DoesNotContain("own-1", all);   // id своих стратегий у людей разные
        Assert.True(File.Exists(Path.Combine(f, "strategies", "moya-pervaya.txt")));
    }

    [Fact]
    public void Write_skips_empty_sections_and_refuses_a_foreign_folder_but_replaces_an_old_export()
    {
        var f = NewFolder();
        var r = SettingsExport.Write(f, Env(s => s.Sites = ["a.example"]), SettingsSections.All);
        Assert.Equal(["sites"], r.Sections);
        Assert.False(File.Exists(Path.Combine(f, "exclude.txt")));
        // Прошлая выгрузка заменяется целиком.
        SettingsExport.Write(f, Env(s => s.Exclude = ["b.example"]), SettingsSections.All);
        Assert.False(File.Exists(Path.Combine(f, "sites.txt")));
        Assert.True(File.Exists(Path.Combine(f, "exclude.txt")));
        // Папка с чужим файлом не трогается.
        File.WriteAllText(Path.Combine(f, "my-photo.jpg"), "x");
        Assert.Throws<IOException>(() => SettingsExport.Write(f, Rich(), SettingsSections.All));
        Assert.True(File.Exists(Path.Combine(f, "my-photo.jpg")));
        Assert.True(File.Exists(Path.Combine(f, "exclude.txt")));
    }

    [Fact]
    public void Round_trip_package_equals_the_exported_data()
    {
        var p = Export(Rich());
        Assert.True(p.Ok);
        Assert.Empty(p.Problems);
        Assert.Equal(SettingsSections.All, p.Sections.Select(s => s.Id));
        Assert.All(p.Sections, s => Assert.True(s.Usable));
        Assert.Equal(["site-one.example", "site-two.example"], p.Data.Sites);
        Assert.Equal(["bank.example"], p.Data.Exclude);
        Assert.Equal(["198.51.100.7/32", "203.0.113.0/24"], p.Data.Subnets);
        Assert.Equal(["192.0.2.0/24"], p.Data.SubnetsExclude);
        Assert.Equal(["140.82.112.3 github.com"], p.Data.Hosts);
        Assert.Equal("google", p.Data.Dns!.Choice);
        Assert.Equal(["Моя первая", "Найденная"], p.Data.Strategies!.Select(s => s.Title).Order(StringComparer.Ordinal));
        Assert.Equal(4, p.Data.Advanced!.Rows.Count);
        Assert.NotNull(p.Data.Advanced.Rows[0].StrategyFile);
        Assert.Equal("Игра", p.Data.Apps!.Single().Title);
        Assert.Equal("27015-27030", p.Data!.Apps![0].Udp);
        Assert.Equal("Rich provider", Export(Rich(), "m", new ExportMeta("Rich provider", "n")).Manifest!.Provider);
    }

    // ---------- слияние ----------

    sealed class Rec : ISettingsTarget
    {
        public List<string> Calls { get; } = [];
        public string? BeginImport(ImportPlan plan) { Calls.Add("begin"); return "rec"; }
        public void EndImport(string? record, ImportResult result) => Calls.Add("end");
        public void SetSites(IReadOnlyList<string> sites) => Calls.Add("sites:" + string.Join(",", sites));
        public void SetExclude(IReadOnlyList<string> sites) => Calls.Add("exclude:" + string.Join(",", sites));
        public void SetSubnets(IReadOnlyList<string> lines, bool exclude) => Calls.Add((exclude ? "subnets-x:" : "subnets:") + string.Join(",", lines));
        public string AddStrategy(string title, IReadOnlyDictionary<string, string> roles) { Calls.Add("strategy:" + title); return "new-" + title; }
        public void RemoveApp(string id) => Calls.Add("app-:" + id);
        public void AddApp(ImportApp app) => Calls.Add("app+:" + app.Title);
        public void SetAdvanced(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows) =>
            Calls.Add($"advanced:{enabled}:{others}:" + string.Join(";", rows.Select(r => string.Join(",", r.Domains) + "=" + r.Strategy)));
        public Task<HostsUserResult> SetHostsAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct)
        {
            Calls.Add("hosts:" + string.Join(",", entries));
            return Task.FromResult(new HostsUserResult(true, entries, [], [], null));
        }
        public Task<DnsSetResult> SetDnsAsync(DnsChoice dns, CancellationToken ct)
        {
            Calls.Add("dns:" + dns.Choice);
            return Task.FromResult(new DnsSetResult(true, "ok", null!));
        }
    }

    [Fact]
    public async Task Add_keeps_own_records_and_appends_new_ones_Replace_leaves_only_the_package()
    {
        var pkg = Export(Env(s => { s.Sites = ["b.example", "c.example"]; s.Hosts = ["140.82.112.3 github.com", "140.82.112.4 gist.github.com"]; }));
        var mine = Env(s => { s.Sites = ["a.example", "b.example"]; s.Hosts = ["140.82.112.3 github.com", "140.82.112.9 old.github.com"]; });

        var add = SettingsImport.Plan(pkg, mine, ["sites", "hosts"], ImportMode.Add);
        var sites = add.Section("sites")!;
        Assert.Equal((1, 0, 1, 0), (sites.Added, sites.Removed, sites.Same, sites.Skipped));
        Assert.Equal([PlanAction.Same, PlanAction.Add], sites.Items.Select(i => i.Action));
        Assert.True(add.Section("hosts")!.Risky);
        var rec = new Rec();
        var res = await SettingsImport.ApplyAsync(add, rec);
        Assert.True(res.AllOk);
        Assert.Contains("sites:a.example,b.example,c.example", rec.Calls);
        Assert.Contains("hosts:140.82.112.3 github.com,140.82.112.9 old.github.com,140.82.112.4 gist.github.com", rec.Calls);

        var rep = SettingsImport.Plan(pkg, mine, ["sites", "hosts"], ImportMode.Replace);
        Assert.Equal((1, 1, 1), (rep.Section("sites")!.Added, rep.Section("sites")!.Removed, rep.Section("sites")!.Same));
        Assert.Contains(rep.Section("sites")!.Items, i => i is { Action: PlanAction.Remove, Text: "a.example" });
        rec = new Rec();
        await SettingsImport.ApplyAsync(rep, rec);
        Assert.Contains("sites:b.example,c.example", rec.Calls);
        Assert.Contains("hosts:140.82.112.3 github.com,140.82.112.4 gist.github.com", rec.Calls);
    }

    [Fact]
    public async Task Nothing_to_change_applies_nothing_and_unchecked_sections_are_not_touched()
    {
        var same = Env(s => s.Sites = ["a.example"]);
        var pkg = Export(same);
        var plan = SettingsImport.Plan(pkg, same, SettingsSections.All, ImportMode.Replace);
        Assert.False(plan.HasChanges);
        var rec = new Rec();
        var res = await SettingsImport.ApplyAsync(plan, rec);
        Assert.Empty(res.Sections);
        Assert.Empty(rec.Calls);

        var two = Export(Env(s => { s.Sites = ["a.example", "n.example"]; s.Exclude = ["e.example"]; }), "two");
        var only = SettingsImport.Plan(two, same, ["exclude"], ImportMode.Add);
        Assert.Equal(["exclude"], only.Sections.Select(s => s.Id));
        Assert.Null(only.Data.Sites);
        Assert.DoesNotContain("two.example", only.ToRequest());
        Assert.Equal(["e.example"], only.Data.Exclude);
    }

    [Fact]
    public async Task Strategies_are_added_by_parameters_and_advanced_rows_follow_them()
    {
        var src = Rich();
        var pkg = Export(src);
        // На этом ПК такая же по параметрам стратегия «Найденная» уже есть (под своим id) — второй раз не добавляется, строка на неё ссылается.
        var mine = Env(s => s.Strategies.Add(new("own-77", "У меня", "user", Roles(Split + " --dpi-desync-repeats=3"))));
        var plan = SettingsImport.Plan(pkg, mine, ["strategies", "advanced"], ImportMode.Add);
        var st = plan.Section("strategies")!;
        Assert.Equal((1, 1), (st.Added, st.Same));
        var rec = new Rec();
        await SettingsImport.ApplyAsync(plan, rec);
        Assert.Contains("strategy:Моя первая", rec.Calls);
        Assert.DoesNotContain("strategy:Найденная", rec.Calls);
        Assert.Contains(rec.Calls, c => c.StartsWith("advanced:True:lists:game.example=new-Моя первая;x.example,y.example=auto;z.example=direct;c.example=alt9"));
        // Раздел стратегий не выбран: ссылка на стратегию, которой нет, — строка пропускается с причиной, остальные строки живут.
        var noStrategies = SettingsImport.Plan(pkg, mine, ["advanced"], ImportMode.Add);
        var adv = noStrategies.Section("advanced")!;
        Assert.Equal(3, adv.Added);
        Assert.Contains(adv.Items, i => i.Action == PlanAction.Skip && i.Reason!.Contains("не загружена"));
    }

    [Fact]
    public void Apps_need_a_matching_variant_here_and_replace_removes_the_missing_ones()
    {
        var pkg = Export(Env(s => s.Apps = [Profile(title: "Новая", address: "198.51.100.0/24")]));
        var mine = Env(s => s.Apps = [Profile("p11111111", "Старая")]);
        var rep = SettingsImport.Plan(pkg, mine, ["apps"], ImportMode.Replace);
        Assert.Equal((1, 1), (rep.Section("apps")!.Added, rep.Section("apps")!.Removed));
        Assert.Contains(rep.Section("apps")!.Items, i => i is { Action: PlanAction.Remove, Text: "Старая" });
        var add = SettingsImport.Plan(pkg, mine, ["apps"], ImportMode.Add);
        Assert.Equal((1, 0), (add.Section("apps")!.Added, add.Section("apps")!.Removed));
        // На этом ПК нет варианта с такими параметрами — профиль пропущен с причиной.
        var other = Env(s => s.Variants = [new ProfileVariant(0, "другой", GameTcp + " --dpi-desync-repeats=2", GameUdp)]);
        var none = SettingsImport.Plan(pkg, other, ["apps"], ImportMode.Add);
        Assert.False(none.HasChanges);
        Assert.Contains(none.Section("apps")!.Items, i => i.Action == PlanAction.Skip && i.Reason!.Contains("нет подходящих параметров"));
    }

    // ---------- чужие и битые файлы ----------

    [Fact]
    public void Broken_section_is_skipped_with_a_reason_and_the_rest_is_read()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Rich(), SettingsSections.All);
        File.WriteAllBytes(Path.Combine(f, "sites.txt"), [0x61, 0x2E, 0x65, 0x00, 0xFF, 0xFE]);   // не текст
        Put(f, "advanced.json", "{ not json");
        File.Delete(Path.Combine(f, "dns.json"));
        var p = SettingsImport.Read(f);
        Assert.True(p.Ok);
        foreach (var id in new[] { "sites", "advanced", "dns" })
        {
            var s = p.Sections.Single(x => x.Id == id);
            Assert.False(s.Usable);
            Assert.False(string.IsNullOrWhiteSpace(s.SkippedReason));
            Assert.Equal(0, s.Count);
        }
        Assert.Null(p.Data.Sites);
        Assert.Null(p.Data.Advanced);
        Assert.Null(p.Data.Dns);
        Assert.Equal(["bank.example"], p.Data.Exclude);
        Assert.Equal(2, p.Data.Strategies!.Count);
        Assert.True(p.Sections.Where(s => s.Id is not ("sites" or "advanced" or "dns")).All(s => s.Usable));
        Assert.Contains("не текст UTF-8", p.Sections.Single(x => x.Id == "sites").SkippedReason);
        Assert.Contains("Файла dns.json нет", p.Sections.Single(x => x.Id == "dns").SkippedReason);
        // Планируются только читаемые разделы.
        var plan = SettingsImport.Plan(p, Env(), SettingsSections.All, ImportMode.Add);
        Assert.DoesNotContain(plan.Sections, s => s.Id is "sites" or "advanced" or "dns");
    }

    [Fact]
    public void Bad_records_inside_a_section_are_dropped_one_by_one_with_line_numbers()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Env(s => s.Sites = ["keep.example"]), ["sites"]);
        Put(f, "sites.txt", "# comment\nGood.Example\nhttps://www.with-scheme.example/path\nbad host!\n\n-flag.example\na b.example\n*.mask.example\nGood.Example\n");
        var p = SettingsImport.Read(f);
        Assert.Equal(["good.example", "with-scheme.example"], p.Data.Sites);
        Assert.Equal(4, p.ProblemsOf("sites").Count());
        Assert.Contains(p.Problems, x => x.Reason.StartsWith("Строка 4:"));
        Assert.True(p.Sections.Single().Usable);
    }

    [Fact]
    public void Harmful_strategy_is_refused_by_the_winws_whitelist_and_a_foreign_fake_file_too()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Rich(), ["strategies"]);
        var good = Directory.GetFiles(Path.Combine(f, "strategies")).Length;
        Put(f, "strategies/evil.txt", StrategyExchange.Encode("Evil", Roles("--dpi-desync=fake --dpi-desync-repeats=6 --exec=calc.exe")) + "\n");
        Put(f, "strategies/fakepath.txt", StrategyExchange.Encode("Path", Roles("--dpi-desync=fake --dpi-desync-fake-tls=C:/Windows/system32/evil.bin")) + "\n");
        Put(f, "strategies/bat.txt", "winws.exe --dpi-desync=fake\n");
        Put(f, "strategies/two.txt", StrategyExchange.Encode("A", Roles(Split)) + "\n" + StrategyExchange.Encode("B", Roles(Split)) + "\n");
        var p = SettingsImport.Read(f);
        Assert.Equal(good, p.Data.Strategies!.Count);
        Assert.DoesNotContain(p.Data.Strategies, s => s.Name is "evil" or "fakepath" or "bat" or "two");
        Assert.Contains(p.Problems, x => x.File == "strategies/evil.txt" && x.Reason.Contains("--exec"));
        Assert.Contains(p.Problems, x => x.File == "strategies/fakepath.txt");
        Assert.Contains(p.Problems, x => x.File == "strategies/bat.txt");
        Assert.Contains(p.Problems, x => x.File == "strategies/two.txt");

        // Параметры в порядке, но файла фейка нет в папке программы этого ПК — стратегия пропускается с причиной.
        Put(f, "strategies/nofake.txt", StrategyExchange.Encode("NoFake", Roles("--dpi-desync=fake --dpi-desync-fake-tls={fake}/not_in_my_folder.bin")) + "\n");
        var q = SettingsImport.Read(f);
        Assert.Contains(q.Data.Strategies!, s => s.Name == "nofake");   // синтаксис верен, файл ещё не сверяли
        var plan = SettingsImport.Plan(q, Env(), ["strategies"], ImportMode.Add);
        Assert.Contains(plan.Section("strategies")!.Items, i => i.Action == PlanAction.Skip && i.Reason!.Contains("not_in_my_folder.bin"));
        Assert.Equal(good, plan.Section("strategies")!.Added);
    }

    [Theory]
    [InlineData("good-name_1.v2", true)]
    [InlineData("A", true)]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData("../evil", false)]
    [InlineData("..\\evil", false)]
    [InlineData("sub/dir", false)]
    [InlineData("sub\\dir", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:\\Windows\\x", false)]
    [InlineData("C:x", false)]
    [InlineData("name.", false)]
    [InlineData(".hidden", false)]
    [InlineData("", false)]
    [InlineData("con name", false)]
    public void File_names_must_be_plain(string name, bool ok) => Assert.Equal(ok, SettingsClean.IsSafeName(name));

    [Fact]
    public void Paths_and_dotdot_in_names_and_references_are_refused()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Rich(), SettingsSections.All);
        var line = StrategyExchange.Encode("X", Roles(Split));
        Put(f, "strategies/a..b.txt", line + "\n");
        Put(f, "strategies/with space.txt", line + "\n");
        Directory.CreateDirectory(Path.Combine(f, "strategies", "nested"));
        Put(f, "strategies/nested/inner.txt", line + "\n");
        Put(f, "apps/a..b.json", "{}");
        Put(f, "advanced.json", """
            { "enabled": true, "others": "lists", "rows": [
              { "domains": ["ok.example"], "strategy_file": "../../evil" },
              { "domains": ["ok2.example"], "strategy_file": "C:\\Windows\\evil" },
              { "domains": ["ok3.example"], "strategy": "../alt9" },
              { "domains": ["ok4.example"], "strategy": "auto" } ] }
            """);
        var p = SettingsImport.Read(f);
        Assert.DoesNotContain(p.Data.Strategies!, s => s.Name.Contains("..") || s.Name.Contains(' ') || s.Name == "inner");
        Assert.Equal(2, p.Data!.Strategies!.Count);
        Assert.Equal(["ok4.example"], p.Data.Advanced!.Rows.Single().Domains);
        Assert.Equal(3, p.ProblemsOf("advanced").Count());
        Assert.Contains(p.Problems, x => x.File == "apps/a..b.json");
        Assert.Contains(p.Problems, x => x.File == "strategies/nested");
        Assert.Single(p.Data.Apps!);
    }

    [Fact]
    public void Hosts_lines_need_a_public_address_so_local_substitutions_of_system_names_are_refused()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Env(s => s.Hosts = ["140.82.112.3 github.com"]), ["hosts"]);
        Put(f, "hosts.txt", """
            # my lines
            140.82.112.3 github.com   # inline comment
            127.0.0.1 update.microsoft.com
            0.0.0.0 windowsupdate.microsoft.com
            ::1 login.live.com
            192.168.1.1 router.example.com
            10.0.0.5 intranet.example.com
            169.254.1.1 x.example.com
            140.82.112.4 localhost
            140.82.112.4 *.github.com
            140.82.112.4 github.com:443
            140.82.112.5 a.example.com b.example.com
            140.82.112.6
            not-an-ip example.com
            140.82.112.7 https://bad.example.com/path
            2606:4700::1111 cloud.example.com
            """);
        var p = SettingsImport.Read(f);
        Assert.Equal(["140.82.112.3 github.com", "2606:4700::1111 cloud.example.com"], p.Data.Hosts);
        Assert.Equal(13, p.ProblemsOf("hosts").Count());
        var s = p.Sections.Single();
        Assert.True(s.Risky);
        Assert.Equal(2, s.Count);
        Assert.Equal(p.Data.Hosts, s.Items);   // окно показывает строки до применения
        Assert.Contains(p.Problems, x => x.Reason.Contains("не публичный"));
    }

    [Fact]
    public void Limits_on_size_lines_files_and_the_manifest()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Env(s => { s.Sites = ["a.example"]; s.Exclude = ["b.example"]; }), SettingsSections.All);
        // Слишком большой файл: раздел пропущен, не прочитан.
        File.WriteAllText(Path.Combine(f, "sites.txt"), string.Concat(Enumerable.Repeat("some-long-name-for-a-site.example\n", 4000)));
        // Слишком много строк (комментарии и пустые тоже строки), хотя файл небольшой.
        File.WriteAllText(Path.Combine(f, "exclude.txt"), string.Concat(Enumerable.Repeat("\n", 600)) + "b.example\n");
        var p = SettingsImport.Read(f);
        Assert.False(p.Sections.Single(x => x.Id == "sites").Usable);
        Assert.Contains("больше 100 КБ", p.Sections.Single(x => x.Id == "sites").SkippedReason);
        Assert.False(p.Sections.Single(x => x.Id == "exclude").Usable);
        Assert.Contains("больше 400 строк", p.Sections.Single(x => x.Id == "exclude").SkippedReason);

        // Записей больше предела при допустимом размере: принимаются первые, остальные — с причиной.
        var big = Enumerable.Range(0, 1150).Select(i => $"s{i}.example");
        File.WriteAllText(Path.Combine(f, "sites.txt"), string.Join("\n", big));
        var q = SettingsImport.Read(f);
        Assert.Equal(1000, q.Data.Sites!.Count);
        Assert.Contains(q.Problems, x => x.Section == "sites" && x.Reason.Contains("Сверх предела (1000)"));

        // Манифест больше 16 КБ.
        Put(f, "manifest.json", "{\"format\":1,\"sections\":[],\"note\":\"" + new string('x', 20000) + "\"}");
        var r = SettingsImport.Read(f);
        Assert.False(r.Ok);
        Assert.Contains("больше 16 КБ", r.Fatal);

        // Лишние файлы стратегий (больше 100) — лишние пропущены.
        var g = NewFolder("many");
        SettingsExport.Write(g, Rich(), ["strategies"]);
        for (int i = 0; i < 110; i++) Put(g, $"strategies/s{i:D3}.txt", StrategyExchange.Encode("S" + i, Roles(Split + " --dpi-desync-repeats=" + (i % 9 + 1))) + "\n");
        var m = SettingsImport.Read(g);
        Assert.True(m.Data.Strategies!.Count <= SettingsSections.MaxStrategies);
        Assert.Contains(m.Problems, x => x.Reason.Contains("Файлов в папке больше"));
    }

    [Fact]
    public void Manifest_of_another_format_version_or_without_one_is_refused_as_a_whole()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Rich(), SettingsSections.All);
        var manifest = Read(f, "manifest.json");

        Put(f, "manifest.json", manifest.Replace("\"format\": 1", "\"format\": 2"));
        var newer = SettingsImport.Read(f);
        Assert.False(newer.Ok);
        Assert.Contains("более новой версией формата (2)", newer.Fatal);
        Assert.Empty(newer.Sections);
        Assert.Empty(SettingsImport.Plan(newer, Env(), SettingsSections.All, ImportMode.Add).Sections);

        Put(f, "manifest.json", manifest.Replace("\"format\": 1,", ""));
        Assert.False(SettingsImport.Read(f).Ok);
        Put(f, "manifest.json", "это не json");
        Assert.Contains("manifest.json не читается", SettingsImport.Read(f).Fatal);
        File.Delete(Path.Combine(f, "manifest.json"));
        Assert.Contains("нет manifest.json", SettingsImport.Read(f).Fatal);
        Assert.Equal("Папки нет.", SettingsImport.Read(NewFolder("none")).Fatal);

        // Разделы, которых нет в манифесте, не читаются; неизвестные имена игнорируются.
        Put(f, "manifest.json", manifest.Replace("\"sites\",", "\"unknown\",").Replace("\"exclude\",", ""));
        var p = SettingsImport.Read(f);
        Assert.Null(p.Data.Sites);
        Assert.Null(p.Data.Exclude);
        Assert.NotNull(p.Data.Strategies);
    }

    [Fact]
    public void Subnets_are_canonical_without_service_or_too_wide_networks()
    {
        var f = NewFolder();
        SettingsExport.Write(f, Env(s => s.Subnets = ["198.51.100.7/32"]), ["subnets"]);
        Put(f, "subnets.txt", "198.51.100.7\n203.0.113.5/24\n10.0.0.0/8\n192.168.1.0/24\n8.0.0.0/8\n77.0.0.0/12\n127.0.0.1\nnot-a-net\n2001:db8::/48\n");
        var p = SettingsImport.Read(f);
        Assert.Equal(["198.51.100.7/32", "203.0.113.0/24", "2001:db8::/48"], p.Data.Subnets);
        Assert.NotEmpty(p.ProblemsOf("subnets"));
        Assert.Contains(p.Problems, x => x.Reason.Contains("слишком широкая"));
    }

    // ---------- помощник: проверка плана ----------

    static string Mutate(string request, Func<string, string> edit)
    {
        var r = edit(request);
        Assert.NotEqual(request, r);
        return r;
    }

    static readonly IHelperCatalog Catalog = new FakeHelperBackend();

    ImportPlan GoodPlan(out string request)
    {
        var pkg = Export(Rich());
        var plan = SettingsImport.Plan(pkg, Env(), SettingsSections.All.Where(s => s != "hosts"), ImportMode.Add);
        request = plan.ToRequest();
        return plan;
    }

    static string Cmd(string request) => JsonSerializer.Serialize(new { id = 1, cmd = "settings_import", args = new { plan = request } });

    [Fact]
    public void Protocol_command_is_exclusive_long_and_the_version_is_raised()
    {
        Assert.True(HelperCompat.AtLeast(HelperProtocol.Api, "1.13"));
        Assert.True(HelperCommands.Specs["settings_import"].Exclusive);
        Assert.True(HelperCommands.Specs["settings_import"].Long);
        Assert.False(HelperCommands.Specs["settings_export_read"].Exclusive);
        Assert.Contains("settings_import", HelperCommands.Names);
        GoodPlan(out var req);
        var call = HelperCommands.Parse(Cmd(req), Catalog);
        Assert.Equal(req, call.Str("plan"));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":2,"cmd":"settings_import","args":{}}""", Catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":2,"cmd":"settings_import","args":{"plan":"{}","path":"C:\\x"}}""", Catalog));
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("""{"id":2,"cmd":"settings_import","args":{"plan":"не json"}}""", Catalog));
    }

    [Fact]
    public void Helper_refuses_a_forged_plan_on_every_kind_of_tampering()
    {
        GoodPlan(out var req);
        string P(string tamper) => Cmd(tamper);
        void Refuse(string forged) => Assert.Throws<HelperRequestException>(() => HelperCommands.Parse(P(forged), Catalog));

        Refuse(Mutate(req, r => r.Replace("site-one.example", "site one.example")));                      // домен с пробелом
        Refuse(Mutate(req, r => r.Replace("Site-One.example".ToLowerInvariant(), "SITE-ONE.example")));   // не в нормальном виде
        Refuse(Mutate(req, r => r.Replace("--dpi-desync-repeats=6", "--dpi-desync-repeats=6 --exec=calc")));   // неразрешённый параметр winws
        Refuse(Mutate(req, r => r.Replace("{fake}/tls_clienthello_4pda_to.bin", "C:/evil.bin")));         // файл фейка не из папки программы
        Refuse(Mutate(req, r => r.Replace("\"mode\":\"add\"", "\"mode\":\"add\",\"extra\":1")));          // лишнее поле
        Refuse(Mutate(req, r => r.Replace("\"sections\":[", "\"sections\":[\"bogus\",")));                // чужой раздел
        Refuse(Mutate(req, r => r.Replace("\"strategyFile\":\"", "\"strategyFile\":\"../")));            // путь в ссылке
        Refuse(Mutate(req, r => r.Replace("\"choice\":\"google\"", "\"choice\":\"custom\",\"ips\":[\"6.6.6.6 --x\"]")));   // свой DNS с негодным адресом
        Refuse(Mutate(req, r => r.Replace("\"choice\":\"google\"", "\"choice\":\"google\",\"ips\":[\"6.6.6.6\"]")));        // у готового варианта нет своих адресов

        // Данные вне выбранных разделов и подмена hosts-строк на локальный адрес.
        var plan = SettingsImport.Plan(Export(Rich()), Env(), ["sites", "hosts"], ImportMode.Add);
        var sitesOnly = plan.ToRequest();
        Refuse(Mutate(sitesOnly, r => r.Replace("\"sections\":[\"sites\",\"hosts\"]", "\"sections\":[\"sites\"]")));
        Refuse(Mutate(sitesOnly, r => r.Replace("140.82.112.3 github.com", "127.0.0.1 update.microsoft.com")));
        Assert.Equal(sitesOnly, HelperCommands.Parse(P(sitesOnly), Catalog).Str("plan"));   // исходный проходит
    }

    sealed class Probe(params string[] ok) : IEndpointProbe
    {
        public Task<EndpointCheck> CheckAsync(string host, IPAddress ip, CancellationToken ct = default) =>
            Task.FromResult(ok.Contains($"{host}@{ip}") ? new EndpointCheck(EndpointOutcome.Ok, 0.1, "HTTP 200") : new EndpointCheck(EndpointOutcome.Timeout, 5, "нет ответа"));
    }

    sealed class FixedDns(string ip) : IHostsDns
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct = default) => Task.FromResult(new[] { IPAddress.Parse(ip) });
    }

    sealed class NoRdap : IRdapClient
    {
        public Task<RdapOwner?> LookupAsync(IPAddress ip, CancellationToken ct = default) => Task.FromResult<RdapOwner?>(null);
    }

    (Shell Shell, ShellHelperBackend B) NewPc(string name, params string[] liveOk)
    {
        var shell = new Shell(Make.Paths(_t, name));
        shell.Data.Seed();
        var hosts = _t.Sub(name + "-hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        shell.Hosts = new HostsManager(shell.Paths, hosts, new Probe(liveOk), new FixedDns("140.82.112.3"), new NoRdap(), () => []);
        return (shell, PcHelper.Over(shell));
    }

    const string Live = "github.com@140.82.112.3";

    /// <summary>ПК с данными во всех разделах (через те же команды, что и кнопки окна).</summary>
    async Task<(Shell Shell, ShellHelperBackend B)> RichPc(string name)
    {
        var (shell, b) = NewPc(name, Live);
        b.SetUserTargets(["site-one.example", "site-two.example"]);
        b.SetUserExclude(["bank.example"]);
        b.WriteUserIpset(["198.51.100.7/32", "203.0.113.0/24"], false, true);
        b.WriteUserIpset(["192.0.2.0/24"], true, true);
        var own = b.AddStrategy("Моя первая", Roles(Tls), "user");
        UserStrategies.Add(shell.Paths, [new Strategy("found-1", "Найденная", "search", Roles(Split + " --dpi-desync-repeats=3"))]);
        b.AdvancedSet(true, "lists", [new(["game.example"], own.Id), new(["x.example", "y.example"], "auto"), new(["z.example"], "direct")]);
        b.AppProfileSet(null, "Игра", "443", "27015-27030", ["203.0.113.0/24"], 0);
        await b.HostsUserSetAsync([new("github.com", "140.82.112.3")], CancellationToken.None);
        await b.DnsSetAsync("google", null, null, false, CancellationToken.None);
        return (shell, b);
    }

    /// <summary>Сравнимый вид состояния: без id стратегий (у людей разные).</summary>
    static string Digest(SettingsState s)
    {
        string StratName(string id) => s.Strategies.FirstOrDefault(x => x.Id == id) is { } st && SettingsState.IsExportable(st) ? "file:" + st.Title : id;
        return JsonSerializer.Serialize(new
        {
            s.Sites, s.Exclude, s.Subnets, s.SubnetsExclude, s.Hosts, Dns = s.Dns?.Choice,
            Strategies = s.Exportable.Select(x => x.Title + "|" + string.Join(";", x.Roles.Select(r => r.Key + "=" + r.Value))).Order(StringComparer.Ordinal),
            Adv = new { s.Advanced.Enabled, s.Advanced.Others, Rows = s.Advanced.Rows.Select(r => string.Join(",", r.Domains) + "=" + StratName(r.Strategy)) },
            Apps = s.Apps.Select(a => $"{a.Title}|{a.TcpPorts}|{a.UdpPorts}|{string.Join(",", a.Addresses)}"),
        });
    }

    [Fact]
    public async Task Round_trip_through_two_real_helpers_gives_the_same_settings()
    {
        var (_, a) = await RichPc("a");
        var exported = a.SettingsExportRead();
        Assert.Equal("google", exported.Dns?.Choice);
        Assert.Equal(["140.82.112.3 github.com"], exported.Hosts);
        var folder = NewFolder();
        SettingsExport.Write(folder, exported, SettingsSections.All);

        var (_, b) = NewPc("b", Live);
        Assert.Empty(b.SettingsExportRead().Sites);
        var pkg = SettingsImport.Read(folder);
        Assert.Empty(pkg.Problems);
        var plan = SettingsImport.Plan(pkg, b.SettingsExportRead(), SettingsSections.All, ImportMode.Add);
        Assert.All(plan.Sections, s => Assert.True(s.Changes, s.Id));
        Assert.True(plan.Section("hosts")!.Risky);

        var result = await b.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.True(result.AllOk, string.Join("; ", result.Sections.Where(s => !s.Ok).Select(s => s.Id + ": " + s.Message)));
        Assert.NotNull(result.JournalId);
        Assert.Equal(Digest(a.SettingsExportRead()), Digest(b.SettingsExportRead()));

        // Повторная загрузка того же — менять нечего.
        var again = SettingsImport.Plan(pkg, b.SettingsExportRead(), SettingsSections.All, ImportMode.Add);
        Assert.False(again.HasChanges);
        Assert.Equal(Digest(a.SettingsExportRead()), Digest(b.SettingsExportRead()));
    }

    [Fact]
    public async Task Helper_rechecks_the_plan_against_this_pc_and_applies_nothing_when_it_fails()
    {
        var (_, b) = NewPc("b");
        var pkg = Export(Rich());
        var plan = SettingsImport.Plan(pkg, Env(), ["sites", "strategies"], ImportMode.Add);   // окно считало с чужим «окружением», где файл фейка есть
        var forged = plan.ToRequest().Replace("tls_clienthello_4pda_to.bin", "tls_clienthello_nowhere.bin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SettingsImportAsync(forged, CancellationToken.None));
        Assert.Empty(b.SettingsExportRead().Sites);
        Assert.Empty(b.ChangesList().Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SettingsImportAsync("{}", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => b.SettingsImportAsync(plan.ToRequest().Replace("site-one.example", "site one"), CancellationToken.None));
    }

    [Fact]
    public async Task Channel_rejects_forged_plan_with_invalid_and_accepts_a_good_one()
    {
        var (_, b) = NewPc("b");
        await using var f = new HelperFixture(backend: b);
        var link = await f.LinkAsync();
        var c = link.Client!;
        var env = await c.SettingsExportReadAsync();
        var plan = SettingsImport.Plan(Export(Env(s => s.Sites = ["one.example", "two.example"])), env, ["sites"], ImportMode.Add);
        var ex = await Assert.ThrowsAsync<HelperException>(() => c.CallAsync("settings_import", new { plan = plan.ToRequest().Replace("one.example", "one example") }));
        Assert.Equal(HelperErrors.Invalid, ex.Code);
        Assert.Empty(b.SettingsExportRead().Sites);
        var ok = await c.SettingsImportAsync(plan);
        Assert.True(ok.AllOk);
        Assert.Equal(["one.example", "two.example"], b.SettingsExportRead().Sites);
        Assert.Equal(["one.example", "two.example"], (await c.SettingsExportReadAsync()).Sites);
    }

    // ---------- журнал и откат ----------

    [Fact]
    public async Task Import_is_journaled_and_undo_returns_the_previous_sites_strategies_and_apps()
    {
        var (shell, b) = NewPc("b");
        b.SetUserTargets(["mine.example"]);
        b.SetUserExclude(["keep.example"]);
        b.WriteUserIpset(["198.51.100.7/32"], false, true);
        b.AppProfileSet(null, "Моя старая", "443", "27015", ["198.51.100.0/24"], 0);
        var oldApp = shell.State.AppProfiles.Single();

        var real = b.SettingsExportRead();   // варианты параметров профилей и файлы фейков — настоящие, как у отправителя на том же каталоге
        var src = Rich();
        src.Variants = real.Variants; src.FakeFiles = real.FakeFiles;
        src.Apps = [new AppProfile { Id = "p0000abcd", Title = "Игра", TcpPorts = "443", UdpPorts = "27015-27030", Addresses = ["203.0.113.0/24"], TcpDesync = real.Variants[0].Tcp, UdpDesync = real.Variants[0].Udp }];
        var pkg = Export(src);
        var plan = SettingsImport.Plan(pkg, real, ["sites", "exclude", "subnets", "strategies", "apps"], ImportMode.Replace);
        var res = await b.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.True(res.AllOk, string.Join("; ", res.Sections.Select(s => s.Message)));
        var entry = Assert.Single(shell.Journal.Active(), e => e.Kind == ChangeKinds.SettingsImport);
        Assert.Equal(res.JournalId, entry.Id);
        Assert.Contains("Загружены настройки из файлов", entry.Title);
        Assert.True(entry.Completed);
        Assert.Contains(b.ChangesList().Items, i => i.Id == entry.Id && i.Undoable);

        var now = b.SettingsExportRead();
        Assert.Equal(["site-one.example", "site-two.example"], now.Sites);
        Assert.Equal(["bank.example"], now.Exclude);
        Assert.Equal(2, now.Exportable.Count());
        Assert.Equal(["Игра"], now.Apps.Select(a => a.Title));   // «заменить» убрало старый профиль

        var report = await b.ChangesUndoAsync(entry.Id, CancellationToken.None);
        Assert.Equal(UndoStatus.Restored, Assert.Single(report.Items).Status);
        var back = b.SettingsExportRead();
        Assert.Equal(["mine.example"], back.Sites);
        Assert.Equal(["keep.example"], back.Exclude);
        Assert.Equal(["198.51.100.7/32"], back.Subnets);
        Assert.Empty(back.SubnetsExclude);
        Assert.Empty(back.Exportable);
        Assert.Contains(back.Apps, a => a.Id == oldApp.Id && a.Title == "Моя старая");
        // Новый профиль убирает своя запись журнала профиля (она тоже в «Отменить всё»).
        Assert.Contains(shell.Journal.Active(), e => e.Kind == ChangeKinds.AppProfile);
    }

    [Fact]
    public async Task Undo_does_not_overwrite_what_the_person_changed_after_the_import()
    {
        var (shell, b) = NewPc("b");
        b.SetUserTargets(["mine.example"]);
        var plan = SettingsImport.Plan(Export(Env(s => s.Sites = ["new.example"])), b.SettingsExportRead(), ["sites"], ImportMode.Replace);
        var res = await b.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        b.SetUserTargets(["new.example", "later.example"]);   // человек поправил список после загрузки
        var report = await b.ChangesUndoAsync(res.JournalId!, CancellationToken.None);
        Assert.Equal(UndoStatus.ChangedByOthers, Assert.Single(report.Items).Status);
        Assert.Equal(["new.example", "later.example"], shell.State.UserTargets);
    }

    [Fact]
    public async Task Failed_import_leaves_no_active_journal_record()
    {
        var (shell, b) = NewPc("b");
        // Hosts: адрес не отвечает на живую проверку — строка отклонена помощником, раздел «ничего не добавил».
        var plan = SettingsImport.Plan(Export(Env(s => s.Hosts = ["140.82.112.3 github.com"])), b.SettingsExportRead(), ["hosts"], ImportMode.Add);
        var res = await b.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.Equal(0, res.Sections.Single().Added);
        Assert.Equal(1, res.Sections.Single().Skipped);
        Assert.Contains("140.82.112.3 github.com", res.Sections.Single().Message);
        Assert.Empty(shell.Journal.Active(ChangeKinds.SettingsImport));
    }

    [Fact]
    public async Task Dns_and_hosts_are_applied_through_their_own_journaled_paths()
    {
        var (shell, b) = NewPc("b", Live);
        var plan = SettingsImport.Plan(Export(Env(s => { s.Dns = new() { Choice = "cloudflare" }; s.Hosts = ["140.82.112.3 github.com"]; })), b.SettingsExportRead(), ["dns", "hosts"], ImportMode.Add);
        var res = await b.SettingsImportAsync(plan.ToRequest(), CancellationToken.None);
        Assert.True(res.AllOk, string.Join("; ", res.Sections.Select(s => s.Message)));
        Assert.Equal("cloudflare", b.SettingsExportRead().Dns?.Choice);
        Assert.Equal(["140.82.112.3 github.com"], b.SettingsExportRead().Hosts);
        Assert.Contains(shell.Journal.Active(), e => e.Kind == ChangeKinds.Dns);
        Assert.Contains(shell.Journal.Active(), e => e.Kind == ChangeKinds.HostsUser);
    }
}
