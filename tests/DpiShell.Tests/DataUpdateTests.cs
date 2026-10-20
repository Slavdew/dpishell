using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Подмена HTTP: ответы по адресу, порядок запросов, «висящий» адрес до отмены.</summary>
public sealed class MapHandler : HttpMessageHandler
{
    readonly Dictionary<string, Func<CancellationToken, Task<HttpResponseMessage>>> _map = [];
    public List<string> Requests { get; } = [];

    public MapHandler Ok(string url, byte[] body)
    {
        _map[url] = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        return this;
    }

    public MapHandler Ok(string url, string body) => Ok(url, Encoding.UTF8.GetBytes(body));

    public MapHandler Status(string url, HttpStatusCode code)
    {
        _map[url] = _ => Task.FromResult(new HttpResponseMessage(code));
        return this;
    }

    /// <summary>Нет связи (как заблокированный по IP адрес с RST).</summary>
    public MapHandler Fail(string url)
    {
        _map[url] = _ => throw new HttpRequestException("connection reset");
        return this;
    }

    /// <summary>Молчит, пока запрос не отменят (как адрес, пакеты к которому просто теряются).</summary>
    public MapHandler Hang(string url)
    {
        _map[url] = async ct => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        lock (Requests) Requests.Add(url);
        return _map.TryGetValue(url, out var f) ? f(ct) : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Обновление данных по data.json (architecture.md «Обновления данных»): порядок источников, зеркало, хеши, свои файлы, команда помощника.</summary>
public sealed partial class DataUpdateTests
{
    const string A = "https://primary.example/data/";
    const string B = "https://mirror.example/data/";

    static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    /// <summary>Манифест «как у нас»: версия, хеши текущей поставки и подмены для отдельных файлов.</summary>
    static (string Json, Dictionary<string, byte[]> Files) Remote(string data, string version, Dictionary<string, byte[]>? changed = null, string? minApp = null, string? app = null)
    {
        var pkg = DataManifest.Load(Path.Combine(data, DataManifest.FileName));
        var files = new JsonObject();
        var bodies = new Dictionary<string, byte[]>();
        foreach (var (key, f) in pkg.Files)
        {
            var body = changed?.GetValueOrDefault(key) ?? File.ReadAllBytes(Path.Combine(data, key.Replace('/', Path.DirectorySeparatorChar)));
            bodies[key] = body;
            // Хеш — тем же правилом, что у манифеста (текст — по LF): тело кладём во временный файл с тем же расширением.
            var tmp = Path.Combine(Path.GetTempPath(), "dpishell-tests", Guid.NewGuid().ToString("N") + Path.GetExtension(key));
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            File.WriteAllBytes(tmp, body);
            files[key] = new JsonObject { ["sha256"] = DataManifest.Sha256(tmp) };
            File.Delete(tmp);
        }
        foreach (var (key, body) in changed ?? [])
            if (!files.ContainsKey(key))
            {
                files[key] = new JsonObject { ["sha256"] = Sha(body) };
                bodies[key] = body;
            }
        var o = new JsonObject { ["version"] = version, ["files"] = files };
        if (minApp is not null) o["min_app_version"] = minApp;
        if (app is not null) o["app_version"] = app;
        return (o.ToJsonString(), bodies);
    }

    static MapHandler Serve(MapHandler h, string source, (string Json, Dictionary<string, byte[]> Files) remote)
    {
        h.Ok(source + DataManifest.FileName, remote.Json);
        foreach (var (k, b) in remote.Files) h.Ok(source + k, b);
        return h;
    }

    /// <summary>Поставка — копия data/ репозитория (тест меняет её как хочет), корень программы — пустая папка.</summary>
    static (AppPaths Paths, string Package) Setup(TempDir t, params string[] sources)
    {
        var pkg = t.Sub("pkg");
        foreach (var f in Directory.EnumerateFiles(Repo.Data, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(pkg, Path.GetRelativePath(Repo.Data, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
        File.WriteAllText(Path.Combine(pkg, UpdateSources.FileName),
            new JsonObject { ["sources"] = new JsonArray(sources.Select(s => (JsonNode)s).ToArray()), ["release_page"] = "https://example.com/releases" }.ToJsonString());
        return (new AppPaths(t.Sub("root"), pkg), pkg);
    }

    static DataUpdateClient Client(MapHandler h, params string[] sources) =>
        new(new HttpClient(h), UpdateSources.From(sources)) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), RequireSignature = false };

    // ---------- источники ----------

    [Fact]
    public async Task Sources_AreTriedInOrder_FirstGoodWins()
    {
        var h = Serve(Serve(new MapHandler(), A, Remote(Repo.Data, "2026-12-01")), B, Remote(Repo.Data, "2026-12-02"));
        var r = await Client(h, A, B).FetchManifestAsync();
        Assert.Equal(A, r.Result!.Source);
        Assert.Equal("2026-12-01", r.Result.Manifest.Version);
        Assert.Equal([A + "data.json", A + "data.json.sig"], h.Requests);
    }

    [Fact]
    public async Task PrimaryDown_FallsBackToMirror()
    {
        var h = Serve(new MapHandler().Fail(A + "data.json"), B, Remote(Repo.Data, "2026-12-01"));
        var r = await Client(h, A, B).FetchManifestAsync();
        Assert.Equal(B, r.Result!.Source);
        Assert.Equal([A + "data.json", B + "data.json", B + "data.json.sig"], h.Requests);
        Assert.Contains(r.Errors, e => e.Contains("primary.example"));
    }

    [Fact]
    public async Task SilentPrimary_TimesOut_AndMirrorAnswers()
    {
        var h = Serve(new MapHandler().Hang(A + "data.json"), B, Remote(Repo.Data, "2026-12-01"));
        var sw = Stopwatch.StartNew();
        var r = await Client(h, A, B).FetchManifestAsync();
        Assert.Equal(B, r.Result!.Source);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "ждали дольше таймаута источника: " + sw.Elapsed);
        Assert.Contains(r.Errors, e => e.Contains("не ответил"));
    }

    [Fact]
    public void DefaultTimeout_IsFiveSecondsPerSource() =>
        Assert.Equal(TimeSpan.FromSeconds(5), new DataUpdateClient(new HttpClient(), UpdateSources.From([A])).PerSourceTimeout);

    [Fact]
    public async Task AllSourcesDown_NoManifest()
    {
        var h = new MapHandler().Status(A + "data.json", HttpStatusCode.ServiceUnavailable).Fail(B + "data.json");
        var r = await Client(h, A, B).FetchManifestAsync();
        Assert.Null(r.Result);
        Assert.Equal(2, r.Errors.Count);
    }

    [Fact]
    public async Task ManifestWithBadKeys_IsNotTrusted_NextSourceUsed()
    {
        var evil = Remote(Repo.Data, "2026-12-01", new() { ["lists/user-general.txt"] = "evil.example\n"u8.ToArray() });
        var h = Serve(Serve(new MapHandler(), A, evil), B, Remote(Repo.Data, "2026-12-01"));
        var r = await Client(h, A, B).FetchManifestAsync();
        Assert.Equal(B, r.Result!.Source);
        Assert.Contains(r.Errors, e => e.Contains("не прошёл проверку"));
    }

    [Theory]
    [InlineData("lists/general.txt", true)]
    [InlineData("strategies.json", true)]
    [InlineData("fake/stun.bin", true)]
    [InlineData("lists/user-general.txt", false)]
    [InlineData("lists/user-ipset.txt", false)]
    [InlineData("data.json", false)]
    [InlineData("update-sources.json", false)]
    [InlineData("../core/winws.exe", false)]
    [InlineData("lists/../../x.txt", false)]
    [InlineData("core/winws.exe", false)]
    [InlineData("C:/Windows/x.txt", false)]
    [InlineData("lists\\general.txt", false)]
    [InlineData("lists/a b.txt", false)]
    public void ManifestKeys_OnlyDataFiles(string key, bool ok) => Assert.Equal(ok, DataKeys.IsAllowed(key));

    [Fact]
    public void UpdateSources_OnlyHttps()
    {
        var s = UpdateSources.From(["http://plain.example/data/", "https://ok.example/data", "https://user:pw@x.example/", "https://q.example/data/?a=1", "file:///C:/x/", "https://ok.example/data/"],
            "javascript:alert(1)");
        Assert.Equal(["https://ok.example/data/"], s.Sources);
        Assert.Null(s.ReleasePage);
    }

    // ---------- файлы ----------

    [Fact]
    public async Task WrongHash_FileDropped_NextSourceUsed()
    {
        using var t = new TempDir();
        var good = "good\n"u8.ToArray();
        var h = new MapHandler().Ok(A + "lists/general.txt", "tampered\n").Ok(B + "lists/general.txt", good);
        var target = t.Sub("general.txt");
        await Client(h, A, B).DownloadVerifiedAsync("lists/general.txt", Sha(good), target);
        Assert.Equal(good, File.ReadAllBytes(target));
    }

    [Fact]
    public async Task WrongHashEverywhere_Throws_AndLeavesNothing()
    {
        using var t = new TempDir();
        var h = new MapHandler().Ok(A + "lists/general.txt", "tampered\n").Ok(B + "lists/general.txt", "also bad\n");
        var target = t.Sub("general.txt");
        var e = await Assert.ThrowsAsync<SecurityException>(() => Client(h, A, B).DownloadVerifiedAsync("lists/general.txt", Sha("good\n"u8.ToArray()), target));
        Assert.Contains("sha256", e.Message);
        Assert.False(File.Exists(target));
    }

    // ---------- установка (то, что делает помощник) ----------

    [Fact]
    public async Task Install_UpdatesData_KeepsUserFiles()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A, B);
        var shell = new Shell(paths);
        shell.Data.Seed();
        File.WriteAllText(Path.Combine(paths.Lists, DataStore.UserGeneral), "my-own-site.example\n");
        File.WriteAllText(Path.Combine(paths.Lists, IpLists.UserFile), "198.51.100.0/24\n");
        var userBefore = File.ReadAllText(Path.Combine(paths.Lists, DataStore.UserGeneral));
        var ipsetBefore = File.ReadAllText(Path.Combine(paths.Lists, IpLists.UserFile));

        var general = Encoding.UTF8.GetBytes(File.ReadAllText(Path.Combine(pkg, "lists", "general.txt")).Replace("\r\n", "\n") + "new-domain.example\n");
        var remote = Remote(pkg, "2026-12-01", new() { ["lists/general.txt"] = general });
        // Основной молчит — данные с зеркала.
        var h = ServeSigned(new MapHandler().Fail(A + "data.json"), B, remote);
        shell.DataSigningKey = DataKey.PublicKey;

        var r = await shell.UpdateDataAsync(false, null, null, default, new HttpClient(h));

        Assert.True(r.Applied, r.Message);
        Assert.Equal("2026-12-01", r.Version);
        Assert.Equal(B, r.Source);
        Assert.Equal(paths.Updates, paths.Seed);
        Assert.Equal("2026-12-01", shell.DataVersion);
        Assert.Contains("new-domain.example", File.ReadAllText(Path.Combine(paths.Lists, "general.txt")));
        Assert.Equal(userBefore, File.ReadAllText(Path.Combine(paths.Lists, DataStore.UserGeneral)));
        Assert.Equal(ipsetBefore, File.ReadAllText(Path.Combine(paths.Lists, IpLists.UserFile)));
        // Скачан только изменившийся файл — остальное взято из текущих данных.
        Assert.Equal([A + "data.json", B + "data.json", B + "data.json.sig", B + "lists/general.txt"], h.Requests);
        // Список источников — из поставки, не из скачанного.
        Assert.Equal(File.ReadAllText(Path.Combine(pkg, UpdateSources.FileName)), File.ReadAllText(Path.Combine(paths.Updates, UpdateSources.FileName)));
        Assert.False(Directory.Exists(paths.Updates + ".new"));
    }

    [Fact]
    public async Task Install_WrongHash_KeepsOldData()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var remote = Remote(pkg, "2026-12-01", new() { ["lists/general.txt"] = "changed.example\n"u8.ToArray() });
        remote.Files["lists/general.txt"] = "tampered.example\n"u8.ToArray();
        var h = Serve(new MapHandler(), A, remote);

        var r = await new DataUpdater(paths, Client(h, A)).InstallAsync(false);

        Assert.False(r.Applied);
        Assert.Contains("sha256", r.Message);
        Assert.Equal(pkg, paths.Seed);
        Assert.False(Directory.Exists(paths.Updates));
        Assert.False(Directory.Exists(paths.Updates + ".new"));
    }

    [Fact]
    public async Task Install_SameVersion_UpToDate()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var version = DataManifest.Load(Path.Combine(pkg, DataManifest.FileName)).Version;
        var h = Serve(new MapHandler(), A, Remote(pkg, version));
        var r = await new DataUpdater(paths, Client(h, A)).InstallAsync(false);
        Assert.True(r.UpToDate);
        Assert.False(r.Applied);
        Assert.Equal(2, h.Requests.Count);   // data.json и data.json.sig — файлов не качали
    }

    [Fact]
    public async Task Install_NeedsNewerApp_Refused()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", minApp: "99.0"));
        var r = await new DataUpdater(paths, Client(h, A)) { AppVersion = "1.0.0.0" }.InstallAsync(false);
        Assert.False(r.Applied);
        Assert.Contains("новую версию программы", r.Message);
        Assert.Equal(pkg, paths.Seed);
    }

    [Fact]
    public async Task Install_BrokenCatalog_Rejected()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", new() { ["strategies.json"] = "{ not json"u8.ToArray() }));
        var r = await new DataUpdater(paths, Client(h, A)).InstallAsync(false);
        Assert.False(r.Applied);
        Assert.Equal(pkg, paths.Seed);
    }

    /// <summary>Домен в двух списках ролей (google.txt и discord.txt) — обновление отклоняется с причиной, прежние данные остаются.</summary>
    [Theory]
    [InlineData("lists/google.txt", "youtube.com\nstable.dl2.discordapp.net\n")]
    [InlineData("lists/discord.txt", "discord.com\nyoutube.com\n")]
    public async Task Install_RoleListsOverlap_Rejected_WithReason(string file, string body)
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", new() { [file] = System.Text.Encoding.UTF8.GetBytes(body) }));
        var r = await new DataUpdater(paths, Client(h, A)).InstallAsync(false);

        Assert.False(r.Applied);
        Assert.Contains("google.txt пересекается с discord.txt", r.Message);
        Assert.Contains("прежние данные", r.Message);
        Assert.Equal(pkg, paths.Seed);
        Assert.False(Directory.Exists(paths.Updates));
    }

    [Fact]
    public async Task Install_NewCore_InstalledBeforeSwap()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var core = JsonNode.Parse(File.ReadAllText(Path.Combine(pkg, "core.json")))!.AsObject();
        core["version"] = "v72.14";
        core["zip_url"] = "https://github.com/bol-van/zapret/releases/download/v72.14/zapret-v72.14.zip";
        core.Remove("mirror_urls");
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", new() { ["core.json"] = Encoding.UTF8.GetBytes(core.ToJsonString()) }));
        string? installed = null;
        var r = await new DataUpdater(paths, Client(h, A)) { InstallCore = m => { installed = m.Version; return Task.CompletedTask; } }.InstallAsync(false);
        Assert.True(r.Applied, r.Message);
        Assert.True(r.CoreUpdated);
        Assert.Equal("v72.14", installed);

        // Ядро не встало — данные не меняются.
        using var t2 = new TempDir();
        var (paths2, pkg2) = Setup(t2, A);
        var r2 = await new DataUpdater(paths2, Client(h, A)) { InstallCore = _ => throw new SecurityException("хеш архива не совпал") }.InstallAsync(false);
        Assert.False(r2.Applied);
        Assert.Equal(pkg2, paths2.Seed);
    }

    /// <summary>Манифест без подписи: подменивший источник не подсунет своё ядро — только релиз bol-van и без запасных адресов.</summary>
    [Theory]
    [InlineData("https://evil.example/zapret.zip", false)]
    [InlineData("https://github.com/Slavdew/zapret/releases/download/v72.14/zapret-v72.14.zip", false)]
    [InlineData("https://github.com/bol-van/zapret/releases/download/v72.14/zapret-v72.14.zip", true)]
    public async Task Install_NewCore_OnlyOfficialRelease(string zipUrl, bool withMirror)
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var core = JsonNode.Parse(File.ReadAllText(Path.Combine(pkg, "core.json")))!.AsObject();
        core["version"] = "v72.14";
        core["zip_url"] = zipUrl;
        if (!withMirror) core.Remove("mirror_urls");
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", new() { ["core.json"] = Encoding.UTF8.GetBytes(core.ToJsonString()) }));
        bool installed = false;
        var r = await new DataUpdater(paths, Client(h, A)) { InstallCore = _ => { installed = true; return Task.CompletedTask; } }.InstallAsync(false);
        Assert.False(r.Applied);
        Assert.False(installed);
        Assert.Contains("bol-van", r.Message);
        Assert.Equal(pkg, paths.Seed);
    }

    [Fact]
    public async Task DamagedUpdate_FallsBackToPackage()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var general = "changed.example\n"u8.ToArray();
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01", new() { ["lists/general.txt"] = general }));
        Assert.True((await new DataUpdater(paths, Client(h, A)).InstallAsync(false)).Applied);
        Assert.Equal(paths.Updates, paths.Seed);

        File.WriteAllText(Path.Combine(paths.Updates, "lists", "general.txt"), "antivirus ate it\n");
        paths.ResetSeed();
        Assert.Equal(pkg, paths.Seed);
    }

    // ---------- поставка ----------

    [Fact]
    public void PackageManifest_CoversEveryDataFile_AndPassesUpdateRules()
    {
        var m = DataManifest.Load(Path.Combine(Repo.Data, DataManifest.FileName));
        Assert.Empty(m.UpdateProblems());
        var files = Directory.EnumerateFiles(Repo.Data, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Repo.Data, f).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(k => k is not (DataManifest.FileName or UpdateSources.FileName)).Order().ToList();
        Assert.Equal(files, m.Files.Keys.Order().ToList());
        Assert.Empty(m.Mismatches(Repo.Data));
        var s = UpdateSources.Load(Path.Combine(Repo.Data, UpdateSources.FileName));
        // Подписанная копия на Pages (job site в router.yml); источников без data.json.sig нет — их отбросила бы проверка подписи.
        Assert.Equal("https://slavdew.github.io/dpishell/data/", s.Sources[0]);
        Assert.DoesNotContain(s.Sources, u => u.Contains("raw.githubusercontent.com"));
        Assert.NotNull(s.ReleasePage);
    }

    [Theory]
    [InlineData("2026-10-18", "2026-10-17", 1)]
    [InlineData("2026-10-17.2", "2026-10-17", 1)]
    [InlineData("2026-10-17.10", "2026-10-17.9", 1)]
    [InlineData("2026-10-17", "2026-10-17", 0)]
    [InlineData("garbage", "2026-10-17", -1)]
    public void DataVersions_Compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(DataManifest.CompareVersions(a, b)));

    [Fact]
    public void Check_ComparesDataAndApp()
    {
        var m = new DataManifest("2026-12-01", new(), AppVersion: "1.2.0", MinAppVersion: "1.0.0");
        var c = DataUpdateCheck.Compare(m, "2026-10-17", "1.0.0.0");
        Assert.True(c.DataNewer);
        Assert.True(c.AppNewer);
        Assert.False(c.NeedsNewApp);
        var old = DataUpdateCheck.Compare(m with { MinAppVersion = "1.1" }, "2026-10-17", "1.0.0.0");
        Assert.False(old.DataNewer);
        Assert.True(old.NeedsNewApp);
        Assert.False(DataUpdateCheck.Compare(m, "2026-12-01", "1.2.0.0").DataNewer);
    }

    // ---------- команда помощника ----------

    static readonly FakeHelperBackend Catalog = new();

    [Theory]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"url":"https://evil.example/data/"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"source":"https://evil.example/"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"path":"C:\\Users\\x\\data"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"manifest":{"version":"2099-01-01"}}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"confirmed":true,"sha256":"00"}}""", "лишнее поле")]
    [InlineData("""{"id":1,"cmd":"data_update","args":{"confirmed":"yes"}}""", "true или false")]
    public void DataUpdateCommand_RejectsExtraFields(string json, string why) =>
        Assert.Contains(why, Assert.Throws<HelperRequestException>(() => HelperCommands.Parse(json, Catalog)).Message, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void DataUpdateCommand_OnlyConfirmFlag()
    {
        var c = HelperCommands.Parse("""{"id":1,"cmd":"data_update","args":{"confirmed":true}}""", Catalog);
        Assert.True(c.Bool("confirmed"));
        Assert.True(c.Spec.Exclusive);
        Assert.Empty(HelperCommands.Parse("""{"id":2,"cmd":"data_update"}""", Catalog).Args);
    }

    [Fact]
    public async Task DataUpdate_OverChannel_ProgressAndResult()
    {
        await using var f = new HelperFixture();
        using var c = await f.ConnectAsync();
        var log = new List<string>();
        var r = await c.UpdateDataAsync(true, new SyncLog(log));
        Assert.True(r.Applied);
        Assert.Equal("2026-11-01", r.Version);
        Assert.Contains("data_update True", f.Backend.Calls);
        Assert.Contains(log, s => s.Contains("Скачиваю"));

        using var raw = await f.RawAsync();
        var reply = await raw.AskAsync("""{"id":7,"cmd":"data_update","args":{"url":"https://evil.example/"}}""");
        Assert.Contains("\"invalid\"", reply);
    }

    sealed class SyncLog(List<string> into) : IProgress<string>
    {
        public void Report(string value) { lock (into) into.Add(value); }
    }

    // ---------- окно ----------

    [Fact]
    public async Task Window_DailyCheck_ShowsAvailable_AndRemembersFailure()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A, B);
        var shell = new Shell(paths);
        var settings = TestWindow.Settings(shell);
        var h = ServeSigned(new MapHandler().Fail(A + "data.json"), B, Remote(pkg, "2026-12-01", app: "9.0"));
        shell.DataSigningKey = DataKey.PublicKey;
        var now = new DateTime(2026, 10, 17, 12, 0, 0);
        var vm = new UpdatesViewModel(TestWindow.NoHelper(), shell, settings)
        {
            Http = () => new HttpClient(h, disposeHandler: false),
            Now = () => now,
            AppVersion = "1.0.0.0",
        };

        await vm.CheckIfDueAsync();
        Assert.True(vm.DataAvailable);
        Assert.Equal(L.T("about.lists.available", new { date = "01.12.2026" }), vm.AvailableText);
        Assert.True(vm.AppAvailable);
        Assert.Contains("проверено 17.10 в 12:00", vm.StatusText);
        Assert.Equal("2026-12-01", WindowSettings.Load(settings.File).UpdatesData);

        // Раз в день: повторный запуск в тот же день сеть не трогает.
        int before = h.Requests.Count;
        now = now.AddHours(3);
        await vm.CheckIfDueAsync();
        Assert.Equal(before, h.Requests.Count);

        // Назавтра сети нет — «последняя попытка», найденное обновление не теряется.
        var down = new MapHandler().Fail(A + "data.json").Fail(B + "data.json");
        vm.Http = () => new HttpClient(down, disposeHandler: false);
        now = now.AddDays(1);
        await vm.CheckIfDueAsync();
        Assert.Contains(L.T("about.status.failed", new { lists = "", when = L.T("about.when", new { date = "18.10", time = "15:00" }) }).Trim(' ', '·'), vm.StatusText);
        Assert.True(vm.DataAvailable);
    }

    [Fact]
    public async Task Window_Install_GoesThroughHelper()
    {
        using var t = new TempDir();
        await using var f = new HelperFixture();
        var link = await f.LinkAsync();
        var shell = new Shell(Make.Paths(t));
        var settings = TestWindow.Settings(shell);
        settings.UpdatesData = "2099-01-01";
        var said = new List<string>();
        var vm = new UpdatesViewModel(link, shell, settings) { Need = () => Task.FromResult(link.Client), Say = s => { lock (said) said.Add(s); } };
        Assert.True(vm.DataAvailable);
        vm.InstallCmd.Execute(null);
        for (int i = 0; i < 100 && !f.Backend.Calls.Contains("data_update False"); i++) await Task.Delay(50);
        bool Done() { lock (said) return said.Any(s => s.Contains("обновлены")); }
        for (int i = 0; i < 100 && !Done(); i++) await Task.Delay(50);
        Assert.Contains("data_update False", f.Backend.Calls);
        Assert.False(vm.DataAvailable);
        Assert.True(Done());
        await link.DisposeAsync();
    }
}
