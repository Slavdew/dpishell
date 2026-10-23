using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Export;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Окно «Выгрузить / Загрузить настройки» (п. 14) и выбор адресов для проверок (п. 17): ход выгрузки и загрузки без настоящих диалогов
/// (выбор папки и файла подменён), план с итогом по разделам, hosts снят по умолчанию, архив .zip, настройка уходит помощнику, снимки окна.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class TransferWindowTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() { _t.Dispose(); L.ResetForTests(); }

    Shell NewShell() => new(new AppPaths(_t.Sub("root"), Repo.Data));

    static SettingsState Mine() => new()
    {
        Sites = ["one.example", "two.example", "shared.example"],
        Exclude = ["bank.example"],
        Hosts = ["140.82.112.3 github.com"],
        Dns = new() { Choice = "google" },
        FakeFiles = [.. Directory.GetFiles(Path.Combine(Repo.Data, "fake")).Select(f => Path.GetFileName(f)!)],
    };

    static SettingsState Friend() => new()
    {
        Sites = ["shared.example", "new-a.example", "new-b.example"],
        Exclude = ["bank.example", "pay.example"],
        Hosts = ["140.82.112.4 gitlab.com"],
        Dns = new() { Choice = "custom", Ips = ["9.9.9.9"] },
    };

    TransferViewModel Open(HelperFixture f, MainViewModel vm, bool import)
    {
        var t = vm.CreateTransfer(import);
        return t;
    }

    // ---------- выгрузка ----------

    [Fact]
    public async Task Export_lists_sections_with_counts_hosts_off_and_writes_folder_and_zip()
    {
        await using var f = new HelperFixture();
        f.Backend.SettingsStateValue = Mine();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.TransferFolder = () => _t.Sub("docs");
        Directory.CreateDirectory(_t.Sub("docs"));
        var t = Open(f, vm, import: false);
        await t.EnsureExportAsync();

        var rows = t.ExportSections.ToDictionary(s => s.Id);
        Assert.Equal("3", rows["sites"].CountText);
        Assert.Equal("Google", rows["dns"].CountText);
        Assert.True(rows["sites"].IsChecked);
        Assert.False(rows["subnets"].IsChecked);          // пусто — без галочки
        Assert.False(rows["subnets"].Enabled);
        Assert.False(rows["hosts"].IsChecked);            // подмена адресов — только сознательно
        Assert.True(rows["hosts"].HasWarn);
        Assert.Contains("one.example", rows["sites"].ItemsText);   // что уйдёт — видно
        Assert.Contains("settings_export_read", f.Backend.Calls);
        Assert.False(t.CanExport == false && t.HasExportPath);

        // По умолчанию — архив .zip (один файл можно отправить в мессенджере); папка — переключением.
        Assert.True(t.ExportZip);
        t.ExportZip = false;
        // Папка.
        t.Provider = "Тест-ТВ"; t.Note = "заметка";
        await t.ExportAsync();
        var folder = t.ExportPath;
        Assert.True(File.Exists(Path.Combine(folder, "manifest.json")), t.ExportResultText);
        Assert.True(File.Exists(Path.Combine(folder, "sites.txt")));
        Assert.False(File.Exists(Path.Combine(folder, "hosts.txt")));
        Assert.Contains("Тест-ТВ", File.ReadAllText(Path.Combine(folder, "manifest.json")));

        // Архив .zip: путь переключается на .zip, внутри папка с датой.
        t.ExportZip = true;
        Assert.EndsWith(".zip", t.ExportPath);
        rows["hosts"].IsChecked = true;
        await t.ExportAsync();
        Assert.True(File.Exists(t.ExportPath), t.ExportResultText);
        var pkg = SettingsImport.ReadPackage(t.ExportPath);
        Assert.True(pkg.Ok, pkg.Fatal);
        Assert.Equal(["140.82.112.3 github.com"], pkg.Data.Hosts);
    }

    [Fact]
    public async Task Export_browse_uses_replaceable_choosers_only()
    {
        await using var f = new HelperFixture();
        f.Backend.SettingsStateValue = Mine();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.ChooseFolder = (_, _) => _t.Sub("picked");
        var saved = "";
        vm.ChooseSaveZip = (_, _, name) => { saved = name; return _t.Sub("x.zip"); };
        var t = Open(f, vm, import: false);
        await t.EnsureExportAsync();
        t.ExportZip = false;
        t.BrowseExportCmd.Execute(null);
        Assert.StartsWith(_t.Sub("picked"), t.ExportPath);
        t.ExportZip = true;
        t.BrowseExportCmd.Execute(null);
        Assert.Equal(_t.Sub("x.zip"), t.ExportPath);
        Assert.NotEqual("", saved);
        // Настоящий диалог под тестом — ошибка (UiGuard), поэтому без подмены он бы упал.
        var fresh = TestWindow.Model(NewShell(), await f.LinkAsync());
        Assert.Throws<InvalidOperationException>(() => fresh.ChooseFolder("t", null));
    }

    // ---------- загрузка ----------

    string MakeFriendPackage(bool zip)
    {
        var state = Friend();
        var all = new[] { "sites", "exclude", "hosts", "dns" };
        if (zip) { var z = _t.Sub("friend.zip"); SettingsExport.WriteZip(z, state, all, new ExportMeta("Ростелеком", "от соседа")); return z; }
        var d = _t.Sub("friend"); SettingsExport.Write(d, state, all, new ExportMeta("Ростелеком", "от соседа")); return d;
    }

    /// <summary>hosts и DNS из чужого пакета меняют весь компьютер — один вопрос перед загрузкой («что изменится, как вернуть»); «нет» — помощнику ничего.</summary>
    [Fact]
    public async Task Import_with_hosts_or_dns_asks_once_and_no_means_nothing_sent()
    {
        await using var f = new HelperFixture();
        f.Backend.SettingsStateValue = Mine();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var asked = new List<string>();
        vm.Confirm = q => { asked.Add(q); return false; };
        var t = Open(f, vm, import: true);
        await t.LoadPackageAsync(MakeFriendPackage(false));
        foreach (var s in t.ImportSections) s.IsChecked = s.Id is "hosts" or "dns";

        await t.ImportAsync();
        var q = Assert.Single(asked);
        Assert.Contains(SettingsSections.Title("hosts"), q);
        Assert.Contains(SettingsSections.Title("dns"), q);
        Assert.Contains("Отменить всё", q);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("settings_import"));

        vm.Confirm = q2 => { asked.Add(q2); return true; };
        await t.ImportAsync();
        Assert.Equal(2, asked.Count);
        Assert.Contains(f.Backend.Calls, c => c.StartsWith("settings_import"));

        // Только свои сайты — на весь компьютер не влияют, вопроса нет.
        foreach (var s in t.ImportSections) s.IsChecked = s.Id == "sites";
        t.ReplaceMode = true;
        await t.ImportAsync();
        Assert.Equal(2, asked.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_shows_plan_per_section_hosts_and_custom_dns_off_then_loads(bool zip)
    {
        await using var f = new HelperFixture();
        f.Backend.SettingsStateValue = Mine();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var t = Open(f, vm, import: true);
        await t.LoadPackageAsync(MakeFriendPackage(zip));

        Assert.True(t.HasPackage, t.ImportError);
        Assert.Contains("Ростелеком", t.ProviderText);
        Assert.Contains("от соседа", t.PackageNote);
        var rows = t.ImportSections.ToDictionary(s => s.Id);
        Assert.Equal("+2 · 1 уже есть", rows["sites"].CountText);
        Assert.Equal("+1 · 1 уже есть", rows["exclude"].CountText);
        Assert.True(rows["sites"].IsChecked);
        // hosts и свой DNS — снять по умолчанию, предупреждение и строки на месте.
        Assert.False(rows["hosts"].IsChecked);
        Assert.True(rows["hosts"].HasWarn);
        Assert.Contains("gitlab.com", rows["hosts"].ItemsText);
        Assert.False(rows["dns"].IsChecked);
        Assert.True(rows["dns"].Risky && rows["dns"].HasWarn);

        // «Заменить»: свои, которых нет в пакете, уйдут.
        t.ReplaceMode = true;
        Assert.Contains("уйдёт 2", t.ImportSections.Single(s => s.Id == "sites").CountText);
        t.ReplaceMode = false;

        bool refreshed = false;
        t.AfterImport = () => { refreshed = true; return Task.CompletedTask; };
        f.Backend.ImportJournalId = "abcdefabcdef";
        Assert.False(t.ShowUndoImport);
        await t.ImportAsync();
        Assert.Contains(f.Backend.Calls, c => c.StartsWith("settings_import"));
        Assert.True(refreshed);
        Assert.Contains(L.T("settings.transfer.undo_hint"), t.ImportResultText);

        // С-19: «Отменить загрузку» — только эта запись (с её пунктами), не «Отменить всё».
        Assert.True(t.ShowUndoImport);
        vm.Confirm = _ => true;
        await t.UndoImportAsync();
        Assert.Contains("changes_undo_group abcdefabcdef", f.Backend.Calls);
        Assert.DoesNotContain("changes_undo_all", f.Backend.Calls);
        Assert.False(t.ShowUndoImport);
        Assert.Equal(L.T("settings.transfer.undone"), t.ImportResultText);
    }

    [Fact]
    public async Task Import_of_a_bad_archive_shows_the_reason_and_writes_nothing()
    {
        await using var f = new HelperFixture();
        f.Backend.SettingsStateValue = Mine();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var bad = _t.Sub("bad.zip");
        using (var fs = File.Create(bad))
        using (var z = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var s = z.CreateEntry("manifest.json").Open()) s.Write("{}"u8);
            using (var s = z.CreateEntry("../evil.txt").Open()) s.Write("x"u8);
        }
        var t = Open(f, vm, import: true);
        await t.LoadPackageAsync(bad);
        Assert.False(t.HasPackage);
        Assert.True(t.HasImportError);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("settings_import"));
    }

    // ---------- адреса для проверок ----------

    [Fact]
    public async Task Check_dns_setting_reaches_shell_helper_and_dns_summary()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = TestWindow.Model(shell, await f.LinkAsync());
        Assert.True(vm.CheckDnsSecure);
        Assert.Equal(CheckDns.Secure, shell.CheckDnsMode);
        vm.CheckDnsSystem = true;
        Assert.Equal(CheckDns.System, shell.CheckDnsMode);
        Assert.Equal("system", vm.CheckDnsMode);
        Assert.True(TestWindow.Settings(shell).CheckDns is "system");   // записано в window.json
        // Помощнику — аргументом команды подбора (API 1.12), он ставит его в Shell перед подбором.
        f.Backend.Release.TrySetResult();
        await using var c = await f.ConnectAsync();
        await c.SelectAsync(true, null, new Progress<SelectorProgress>(_ => { }), default, vm.CheckDnsMode);
        Assert.Equal("system", f.Backend.CheckDnsMode);
        await c.SelectAsync(true, null, new Progress<SelectorProgress>(_ => { }));
        Assert.Equal("doh", f.Backend.CheckDnsMode);
        SetProp(vm, "IsLoading", false);
        await vm.CheckDnsAsync();
        Assert.Equal(L.T("dns.summary.system"), vm.DnsSummary);
        Assert.Empty(vm.DnsRows);
    }

    // ---------- снимки окна ----------

    [Fact]
    public void Transfer_window_builds_in_ru_en_light_dark()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var (lang, dark) in new[] { (L.Ru, false), ("en", false), (L.Ru, true), ("en", true) })
            {
                L.Set(lang);
                ThemeManager.Apply(dark ? ThemeManager.Dark : ThemeManager.Light);
                var (export, import) = Task.Run(async () =>
                {
                    await using var f = new HelperFixture();
                    f.Backend.SettingsStateValue = new SettingsState
                    {
                        Sites = [.. Enumerable.Range(1, 12).Select(i => $"site{i}.example")], Exclude = ["a.example", "b.example", "c.example"],
                        Hosts = ["140.82.112.3 github.com"], Dns = new() { Choice = "google" },
                        Strategies = [new Strategy("u1", "Моя", "user", new Dictionary<string, string> { ["general"] = "--dpi-desync=multisplit --dpi-desync-split-pos=1" })],
                        FakeFiles = [.. Directory.GetFiles(Path.Combine(Repo.Data, "fake")).Select(x => Path.GetFileName(x)!)],
                    };
                    var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
                    vm.TransferFolder = () => @"D:\Docs";
                    var e = vm.CreateTransfer(false);
                    await e.EnsureExportAsync();
                    foreach (var s in e.ExportSections.Where(s => s.Id == "hosts")) s.ShowItems = true;
                    var pkgDir = _t.Sub("pkg-" + lang + dark);
                    SettingsExport.Write(pkgDir, Friend(), ["sites", "exclude", "hosts", "dns"], new ExportMeta("Rostelecom", "note"));
                    // Строка с неизвестным параметром — «пропущена с причиной».
                    var i = vm.CreateTransfer(true);
                    await i.LoadPackageAsync(pkgDir);
                    i.ImportSections.First(s => s.Id == "hosts").ShowItems = true;
                    return (e, i);
                }).GetAwaiter().GetResult();
                foreach (var (model, name) in new[] { (export, "export"), (import, "import") })
                {
                    model.IsImport = name == "import";
                    var w = TransferView.Create(model);
                    w.WindowStartupLocation = WindowStartupLocation.Manual;
                    w.Left = w.Top = -20000;
                    w.ShowActivated = false; w.ShowInTaskbar = false;
                    w.Show();
                    Pump();
                    CyrillicScan.CheckIfEn(w, "transfer-" + name);
                    if (snapDir is not null)
                    {
                        Directory.CreateDirectory(snapDir);
                        Snap(w, Path.Combine(snapDir, $"transfer-{name}-{lang}-{(dark ? "dark" : "light")}.png"));
                    }
                    w.Close();
                }
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
