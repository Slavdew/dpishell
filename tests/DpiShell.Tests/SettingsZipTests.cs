using System.IO.Compression;
using System.Text;
using DpiShell.Core;
using DpiShell.Core.Export;

namespace DpiShell.Tests;

/// <summary>
/// Пакет настроек в архиве .zip и расширения формата от роутера (docs/settings-export.md): пакет «как от роутера», круг выгрузка .zip → загрузка,
/// zip-бомба, опасные пути и ссылки в архиве.
/// </summary>
public sealed class SettingsZipTests : IDisposable
{
    readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    static byte[] Utf8(string s) => new UTF8Encoding(false).GetBytes(s);

    /// <summary>Архив из записей «имя → содержимое». Имя пишется как есть (проверка опасных путей).</summary>
    string Zip(string name, params (string Entry, byte[] Data)[] entries) => Zip(name, null, entries);

    string Zip(string name, Action<ZipArchiveEntry>? tune, params (string Entry, byte[] Data)[] entries)
    {
        var path = _t.Sub(name);
        using (var fs = File.Create(path))
        using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            foreach (var (entry, data) in entries)
            {
                var e = z.CreateEntry(entry, CompressionLevel.Optimal);
                tune?.Invoke(e);
                using var s = e.Open();
                s.Write(data);
            }
        return path;
    }

    static (string, byte[]) F(string name, string text) => (name, Utf8(text));

    const string RouterManifest = """
        {"format":1,"app":"DpiShell","app_version":"router 0.3.1","created":"2026-10-21T08:00:00Z",
         "sections":["sites","optimize","exclude","dns","mode","apps"],"provider":"Тест-ТВ","note":"с роутера"}
        """;

    static (string, byte[])[] RouterFiles(string prefix) =>
    [
        F(prefix + "manifest.json", RouterManifest),
        F(prefix + "sites.txt", "# DpiShell settings: sites\nyoutube.com\nexample.org\n"),
        F(prefix + "sites-important.txt", "youtube.com\n"),
        F(prefix + "optimize.txt", "example.org\nexample.net\n"),
        F(prefix + "exclude.txt", "# x\n^bank.example\nplain.example\n"),
        F(prefix + "dns.json", "{\"choice\":\"provider\"}\n"),
        F(prefix + "mode.json", "{\"mode\":\"lists\"}\n"),
    ];

    // ---------- расширения формата от роутера ----------

    [Fact]
    public void Router_package_in_folder_zip_is_read_with_all_extensions()
    {
        var path = Zip("router.zip", RouterFiles("dpishell-settings-2026-10-21/"));
        var p = SettingsImport.ReadPackage(path);
        Assert.True(p.Ok, p.Fatal);
        Assert.Equal("router 0.3.1", p.Manifest!.AppVersion);   // manifest с app_version «router …» принимается
        Assert.Equal("Тест-ТВ", p.Manifest.Provider);
        // «Свои сайты» = sites.txt + optimize.txt без повторов; sites-important.txt на ПК не нужен.
        Assert.Equal(["youtube.com", "example.org", "example.net"], p.Data.Sites);
        // «^домен» → домен.
        Assert.Equal(["bank.example", "plain.example"], p.Data.Exclude);
        // provider → автоматически (как у провайдера).
        Assert.Equal("auto", p.Data.Dns!.Choice);
        // Режим роутера на ПК не применяется: строка с причиной, остальное читается; «apps» без файлов тоже пропущен с причиной.
        var mode = p.Sections.Single(s => s.Id == "mode");
        Assert.False(mode.Usable);
        Assert.NotNull(mode.SkippedReason);
        Assert.False(p.Sections.Single(s => s.Id == "apps").Usable);
        Assert.True(p.Sections.Single(s => s.Id == "sites").Usable);
        Assert.Equal(3, p.Sections.Single(s => s.Id == "sites").Count);
        Assert.DoesNotContain(p.Problems, x => x.Section is "sites" or "exclude" or "dns");
    }

    [Fact]
    public void Router_package_with_files_in_the_zip_root_is_read_too()
    {
        var p = SettingsImport.ReadPackage(Zip("root.zip", RouterFiles("")));
        Assert.True(p.Ok, p.Fatal);
        Assert.Equal(3, p.Data.Sites!.Count);
    }

    [Fact]
    public void Router_package_in_a_plain_folder_reads_the_same_as_in_zip()
    {
        var folder = _t.Sub("folder");
        foreach (var (n, d) in RouterFiles("")) { Directory.CreateDirectory(folder); File.WriteAllBytes(Path.Combine(folder, n), d); }
        var a = SettingsImport.ReadPackage(folder);
        var b = SettingsImport.ReadPackage(Zip("same.zip", RouterFiles("")));
        Assert.Equal(a.Data.Canon(), b.Data.Canon());
    }

    [Fact]
    public void Optimize_only_manifest_gives_my_sites()
    {
        var p = SettingsImport.ReadPackage(Zip("opt.zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"router 1","created":"2026-10-21T08:00:00Z","sections":["optimize"]}"""),
            F("optimize.txt", "a.example\nb.example\n")));
        Assert.True(p.Ok, p.Fatal);
        Assert.Equal(["a.example", "b.example"], p.Data.Sites);
        Assert.Equal("sites", p.Sections.Single().Id);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("provider")]
    [InlineData("auto")]
    public void Dns_default_provider_and_auto_all_mean_automatic(string choice)
    {
        var p = SettingsImport.ReadPackage(Zip("dns.zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"router 1","created":"2026-10-21T08:00:00Z","sections":["dns"]}"""),
            F("dns.json", "{\"choice\":\"" + choice + "\"}")));
        Assert.Equal("auto", p.Data.Dns!.Choice);
    }

    [Fact]
    public void Dns_custom_accepts_bootstrap_and_rejects_bad_bootstrap()
    {
        string Pack(string dns) => Zip("dns" + dns.GetHashCode() + ".zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"router 1","created":"2026-10-21T08:00:00Z","sections":["dns"]}"""),
            F("dns.json", dns));
        var ok = SettingsImport.ReadPackage(Pack("""{"choice":"custom","ips":["9.9.9.9"],"doh":"https://dns.example/dns-query","bootstrap":["9.9.9.9","1.1.1.1"]}"""));
        Assert.Equal("custom", ok.Data.Dns!.Choice);
        Assert.Equal(["9.9.9.9"], ok.Data.Dns.Ips);
        var bad = SettingsImport.ReadPackage(Pack("""{"choice":"custom","ips":["9.9.9.9"],"bootstrap":["not-an-ip"]}"""));
        Assert.Null(bad.Data.Dns);
        Assert.False(bad.Sections.Single().Usable);
        // bootstrap у готового варианта быть не может.
        Assert.Null(SettingsImport.ReadPackage(Pack("""{"choice":"google","bootstrap":["8.8.8.8"]}""")).Data.Dns);
    }

    [Fact]
    public void Old_packages_without_extensions_still_read()
    {
        var f = _t.Sub("old");
        Directory.CreateDirectory(f);
        File.WriteAllText(Path.Combine(f, "manifest.json"), """{"format":1,"app":"DpiShell","app_version":"0.10.0","created":"2026-10-20T12:00:00Z","sections":["sites","exclude","dns"]}""");
        File.WriteAllText(Path.Combine(f, "sites.txt"), "a.example\n");
        File.WriteAllText(Path.Combine(f, "exclude.txt"), "b.example\n");
        File.WriteAllText(Path.Combine(f, "dns.json"), """{"choice":"google"}""");
        var p = SettingsImport.ReadPackage(f);
        Assert.Equal(["a.example"], p.Data.Sites);
        Assert.Equal(["b.example"], p.Data.Exclude);
        Assert.Equal("google", p.Data.Dns!.Choice);
    }

    // ---------- круг: выгрузка .zip → загрузка ----------

    [Fact]
    public void Export_to_zip_round_trips_with_the_package_folder_inside()
    {
        var state = new SettingsState { Sites = ["one.example", "two.example"], Exclude = ["bank.example"], Subnets = ["203.0.113.0/24"], Dns = new() { Choice = "google" } };
        var zipPath = _t.Sub("out.zip");
        var r = SettingsExport.WriteZip(zipPath, state, SettingsSections.All, new ExportMeta("Провайдер", "заметка", new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc), "0.11.0"));
        Assert.Equal(zipPath, r.Folder);
        Assert.False(File.Exists(zipPath + ".part"));
        using (var z = ZipFile.OpenRead(zipPath))
        {
            Assert.All(z.Entries, e => Assert.StartsWith("dpishell-settings-2026-10-21/", e.FullName));
            Assert.Contains(z.Entries, e => e.FullName == "dpishell-settings-2026-10-21/manifest.json");
        }
        var tempBefore = Directory.GetDirectories(Path.GetTempPath(), "dpishell-import-*").ToHashSet();
        var p = SettingsImport.ReadPackage(zipPath);
        Assert.True(p.Ok, p.Fatal);
        Assert.Equal("Провайдер", p.Manifest!.Provider);
        Assert.Equal(state.Sites, p.Data.Sites);
        Assert.Equal(state.Exclude, p.Data.Exclude);
        Assert.Equal(state.Subnets, p.Data.Subnets);
        Assert.Equal("google", p.Data.Dns!.Choice);
        // Временные папки убраны.
        // Другие тесты (и другие прогоны) тоже читают пакеты и держат такие папки миг: ждём, пока не останется новых, а не смотрим по времени создания.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        List<string> leaked;
        while ((leaked = Directory.GetDirectories(Path.GetTempPath(), "dpishell-import-*").Where(d => !tempBefore.Contains(d)).ToList()).Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(50);
        Assert.Empty(leaked);
    }

    [Fact]
    public void Export_to_zip_overwrites_existing_archive()
    {
        var zipPath = _t.Sub("again.zip");
        File.WriteAllText(zipPath, "old");
        SettingsExport.WriteZip(zipPath, new SettingsState { Sites = ["a.example"] }, SettingsSections.All);
        Assert.True(SettingsImport.ReadPackage(zipPath).Ok);
    }

    // ---------- безопасность архива ----------

    [Fact]
    public void Zip_bomb_inflating_one_file_is_cut_at_the_limit()
    {
        // 60 МБ нулей сжимаются в десятки КБ; читается не больше предела файла + 1 байт, раздел отклоняется как слишком большой.
        var p = SettingsImport.ReadPackage(Zip("bomb.zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["sites"]}"""),
            ("sites.txt", new byte[60 * 1024 * 1024])));
        Assert.True(File.Exists(_t.Sub("bomb.zip")));
        Assert.True(new FileInfo(_t.Sub("bomb.zip")).Length < 1024 * 1024);
        Assert.True(p.Ok, p.Fatal);
        Assert.False(p.Sections.Single().Usable);
        Assert.Null(p.Data.Sites);
    }

    [Fact]
    public void Zip_bomb_with_many_files_is_refused_by_total_unpacked_size()
    {
        var entries = new List<(string, byte[])> { F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["apps"]}""") };
        for (int i = 0; i < 120; i++) entries.Add(($"apps/a{i}.json", new byte[SettingsSections.JsonBytes + 1000]));
        var p = SettingsImport.ReadPackage(Zip("bomb2.zip", [.. entries]));
        Assert.False(p.Ok);
        Assert.Contains("МБ", p.Fatal);
    }

    [Fact]
    public void Zip_with_too_many_entries_is_refused()
    {
        var entries = new List<(string, byte[])> { F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":[]}""") };
        for (int i = 0; i < SettingsZip.MaxEntries + 1; i++) entries.Add(($"strategies/s{i}.txt", [1]));
        var p = SettingsImport.ReadPackage(Zip("many.zip", [.. entries]));
        Assert.False(p.Ok);
        Assert.Contains(SettingsZip.MaxEntries.ToString(), p.Fatal);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("dpishell-settings-1/../../evil.txt")]
    [InlineData("a/..\\..\\evil.txt")]
    [InlineData("/abs/evil.txt")]
    [InlineData("\\\\server\\share\\evil.txt")]
    [InlineData("C:/Windows/evil.txt")]
    [InlineData("c:evil.txt")]
    [InlineData("sites.txt:stream")]
    public void Unsafe_path_in_archive_refuses_the_whole_archive(string name)
    {
        var marker = Path.Combine(_t.Path, "evil.txt");
        var p = SettingsImport.ReadPackage(Zip("bad.zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["sites"]}"""),
            F("sites.txt", "a.example\n"),
            (name, [1, 2, 3])));
        Assert.False(p.Ok);
        Assert.Null(p.Manifest);   // не распаковано и не прочитано
        Assert.False(File.Exists(marker));
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "evil.txt")));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetTempPath().TrimEnd('\\'))!, "evil.txt")));
    }

    [Fact]
    public void Symlink_in_archive_refuses_the_whole_archive()
    {
        var p = SettingsImport.ReadPackage(Zip("link.zip", e => { if (e.FullName == "sites.txt") e.ExternalAttributes = unchecked((int)(0xA1FFu << 16)); },
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["sites"]}"""),
            F("sites.txt", "C:\\Windows\\win.ini")));
        Assert.False(p.Ok);
        Assert.Contains("ссылк", p.Fatal);
    }

    [Fact]
    public void Duplicate_entry_names_refuse_the_archive()
    {
        var p = SettingsImport.ReadPackage(Zip("dup.zip",
            F("manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["sites"]}"""),
            F("sites.txt", "a.example\n"), F("SITES.txt", "b.example\n")));
        Assert.False(p.Ok);
    }

    [Fact]
    public void Not_a_zip_and_missing_manifest_are_fatal_with_a_reason()
    {
        var junk = _t.Sub("junk.zip");
        File.WriteAllText(junk, "это не архив");
        Assert.False(SettingsImport.ReadPackage(junk).Ok);
        var noManifest = SettingsImport.ReadPackage(Zip("nomanifest.zip", F("sites.txt", "a.example\n")));
        Assert.False(noManifest.Ok);
        // Две папки верхнего уровня с manifest.json — не гадаем, какая нужна.
        var two = SettingsImport.ReadPackage(Zip("two.zip", F("a/manifest.json", "{}"), F("b/manifest.json", "{}")));
        Assert.False(two.Ok);
    }

    [Fact]
    public void Foreign_files_and_nested_folders_in_archive_are_ignored_like_in_a_folder()
    {
        var p = SettingsImport.ReadPackage(Zip("foreign.zip",
            F("p/manifest.json", """{"format":1,"app":"DpiShell","app_version":"x","created":"2026-10-21T08:00:00Z","sections":["sites"]}"""),
            F("p/sites.txt", "a.example\n"), F("p/readme.md", "hi"), F("p/deep/er/file.txt", "x"), F("other/file.txt", "x")));
        Assert.True(p.Ok, p.Fatal);
        Assert.Equal(["a.example"], p.Data.Sites);
    }

    [Fact]
    public void Plan_from_zip_package_counts_added_and_existing_sites()
    {
        var p = SettingsImport.ReadPackage(Zip("plan.zip", RouterFiles("dpishell-settings-2026-10-21/")));
        var cur = new SettingsState { Sites = ["youtube.com"], Exclude = ["plain.example"] };
        var plan = SettingsImport.Plan(p, cur, ["sites", "exclude", "dns"], ImportMode.Add);
        var sites = plan.Section("sites")!;
        Assert.Equal(2, sites.Added);
        Assert.Equal(1, sites.Same);
        Assert.Equal(1, plan.Section("exclude")!.Added);
    }
}
