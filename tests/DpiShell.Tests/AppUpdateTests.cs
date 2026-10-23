using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Обновление программы (architecture.md «Обновление программы»): подпись release.json, выбор архива, sha256, распаковка, откат.</summary>
public sealed class AppUpdateTests
{
    internal const string A = "https://github.example/releases/latest/download/";
    internal const string B = "https://mirror.example/dpishell/";
    internal static readonly TestSigner Key = new();

    internal static byte[] Zip(Dictionary<string, string> entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, body) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(body);
            }
        return ms.ToArray();
    }

    internal static byte[] GoodZip(string version) => Zip(new()
    {
        ["DpiShell/DpiShell.exe"] = "exe " + version,
        ["DpiShell/data/data.json"] = "{}",
        ["DpiShell/README.txt"] = "readme",
    });

    internal static byte[] ReleaseJson(string version, byte[] zip, string? tag = null, string? minOs = null, string? asset = null)
    {
        var o = new JsonObject
        {
            ["schema"] = 1, ["product"] = "dpishell-windows", ["version"] = version, ["tag"] = tag ?? "windows-v" + version,
            ["min_os"] = minOs ?? "10.0.17763", ["changes"] = new JsonArray("Быстрее подбор", "Исправлен трей"), ["changes_en"] = new JsonArray("Faster search", "Tray fixed"),
            ["assets"] = new JsonArray(new JsonObject { ["name"] = asset ?? AppRelease.PortableName(version), ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(zip)) }),
            // Как у CI: описание всегда с датой (без неё подписанное описание не принимается — аудит-2 №6).
            ["issued"] = ReleaseFreshnessTests.Iso(DateTimeOffset.UtcNow),
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
    }

    internal static MapHandler Serve(MapHandler h, string src, string version, byte[]? zip = null, TestSigner? by = null, byte[]? release = null)
    {
        zip ??= GoodZip(version);
        release ??= ReleaseJson(version, zip);
        return h.Ok(src + AppRelease.FileName, release)
            .Ok(src + AppRelease.SignatureName, (by ?? Key).Sign(release))
            .Ok(src + AppRelease.PortableName(version), zip);
    }

    internal static AppUpdateClient Client(MapHandler h, params string[] sources) =>
        new(new HttpClient(h), sources) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = Key.PublicKey, OsVersion = new Version(10, 0, 26200) };

    [Fact]
    public async Task Check_SignedNewer_IsAvailable()
    {
        var r = await Client(Serve(new MapHandler(), A, "1.2.0"), A).CheckAsync("1.0.0");
        Assert.True(r.Available);
        Assert.Equal("1.2.0", r.Version);
        Assert.Equal(["Быстрее подбор", "Исправлен трей"], r.Changes);
        Assert.Equal(AppRelease.PortableName("1.2.0"), r.Found!.Release.Portable!.Name);

        var same = await Client(Serve(new MapHandler(), A, "1.2.0"), A).CheckAsync("1.2.0.0");
        Assert.False(same.Newer);
        Assert.False(same.Available);
    }

    /// <summary>«Что нового» на языке окна: английский, если он есть в подписанном release.json; иначе русский (Б-2 аудита перед бетой).</summary>
    [Fact]
    public async Task Check_Changes_FollowWindowLanguage_FallBackToRussian()
    {
        var r = await Client(Serve(new MapHandler(), A, "1.2.0"), A).CheckAsync("1.0.0");
        var rel = r.Found!.Release;
        Assert.Equal(["Faster search", "Tray fixed"], rel.ChangesFor(L.En));
        Assert.Equal(["Быстрее подбор", "Исправлен трей"], rel.ChangesFor(L.Ru));
        Assert.Equal(["Быстрее подбор", "Исправлен трей"], rel.ChangesFor("de"));
        Assert.Equal(["Быстрее подбор", "Исправлен трей"], (rel with { ChangesEn = null }).ChangesFor(L.En));

        // Старый release.json без changes_en читается: перевода нет — русский и в английском окне.
        var old = JsonNode.Parse(ReleaseJson("1.3.0", GoodZip("1.3.0")))!.AsObject();
        old.Remove("changes_en");
        var parsed = AppRelease.Parse(Encoding.UTF8.GetBytes(old.ToJsonString()));
        Assert.Null(parsed.ChangesEn);
        Assert.Equal(parsed.Changes, parsed.ChangesFor(L.En));
    }

    [Fact]
    public async Task Check_BadOrMissingSignature_SourceRejected()
    {
        // A: подпись чужим ключом; B: без подписи вовсе (у выпусков переходного периода нет); C — верная.
        const string C = "https://third.example/";
        var zip = GoodZip("1.2.0");
        var rel = ReleaseJson("1.2.0", zip);
        var h = Serve(new MapHandler(), A, "1.2.0", by: new TestSigner())
            .Ok(B + AppRelease.FileName, rel);
        Serve(h, C, "1.2.0");
        var r = await Client(h, A, B, C).CheckAsync("1.0.0");
        Assert.Equal(C, r.Found!.Source);
        Assert.Contains("подпись", r.Errors[0]);
        Assert.Contains("404", r.Errors[1]);

        // Подпись от другого release.json (подменили версию) — отказ.
        var swapped = new MapHandler().Ok(A + AppRelease.FileName, ReleaseJson("9.9.9", zip)).Ok(A + AppRelease.SignatureName, Key.Sign(rel));
        Assert.Null((await Client(swapped, A).CheckAsync("1.0.0")).Found);
    }

    [Theory]
    [InlineData("1.2.0", "router-v1.2.0", null)]      // релиз роутера на месте «последнего»
    [InlineData("1.2", null, null)]                   // не X.Y.Z
    [InlineData("1.2.0", null, "../evil.zip")]        // имя файла с путём
    public async Task Check_InvalidRelease_Rejected(string version, string? tag, string? asset)
    {
        var zip = GoodZip(version);
        var rel = ReleaseJson(version, zip, tag: tag, asset: asset);
        var r = await Client(Serve(new MapHandler(), A, version, zip, release: rel), A).CheckAsync("1.0.0");
        Assert.Null(r.Found);
        Assert.Single(r.Errors);
    }

    [Fact]
    public async Task Check_OsTooOld_NotOffered()
    {
        var zip = GoodZip("1.2.0");
        var h = Serve(new MapHandler(), A, "1.2.0", zip, release: ReleaseJson("1.2.0", zip, minOs: "10.0.30000"));
        var r = await Client(h, A).CheckAsync("1.0.0");
        Assert.True(r.Newer);
        Assert.True(r.OsTooOld);
        Assert.False(r.Available);
    }

    [Fact]
    public async Task Check_Timeout_NextSource()
    {
        var h = Serve(new MapHandler().Hang(A + AppRelease.FileName), B, "1.2.0");
        var r = await Client(h, A, B).CheckAsync("1.0.0");
        Assert.Equal(B, r.Found!.Source);
        Assert.Contains("не ответил", r.Errors[0]);
    }

    [Fact]
    public async Task Prepare_Downloads_Verifies_Unpacks_AndRestartPlan()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var h = Serve(new MapHandler(), A, "1.2.0");
        var up = new AppUpdater(Client(h, A), store) { CurrentVersion = "1.0.0", CurrentExe = t.Sub("portable\\DpiShell.exe"), CurrentPid = 4242 };
        var check = await up.CheckAsync();
        var p = await up.PrepareAsync(check);
        Assert.True(p.Ok, p.Message);
        Assert.Equal("1.2.0", p.Version);
        Assert.Equal("exe 1.2.0", File.ReadAllText(store.ExeFor("1.2.0")));
        Assert.True(File.Exists(Path.Combine(store.PathFor("1.2.0"), "data", "data.json")));
        Assert.Empty(Directory.EnumerateFiles(store.Root, "*.part"));
        Assert.False(Directory.Exists(store.PathFor("1.2.0") + ".new"));

        var plan = up.RestartInto("1.2.0");
        Assert.Equal(store.ExeFor("1.2.0"), plan.Exe);
        Assert.Equal(["--wait-pid", "4242", "--updated-from", "1.0.0"], plan.Args);
        var psi = plan.ToStartInfo();
        Assert.False(psi.UseShellExecute);
        Assert.Equal(store.PathFor("1.2.0"), psi.WorkingDirectory);
        Assert.Equal(new AppVersionsState("1.2.0", "1.0.0", t.Sub("portable\\DpiShell.exe"), Pending: "1.2.0"), store.State);

        // Повторная подготовка той же версии — без скачивания.
        int before = h.Requests.Count;
        Assert.True((await up.PrepareAsync(check)).Ok);
        Assert.Equal(before, h.Requests.Count);
    }

    [Fact]
    public async Task Prepare_HashMismatch_TriesNext_ThenFails()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var good = GoodZip("1.2.0");
        // A отдаёт подписанное описание, но подменённый архив; B — настоящий.
        var h = Serve(new MapHandler(), A, "1.2.0", good).Ok(A + AppRelease.PortableName("1.2.0"), GoodZip("evil"));
        h.Ok(B + AppRelease.PortableName("1.2.0"), good);
        var up = new AppUpdater(Client(h, A, B), store) { CurrentVersion = "1.0.0" };
        var p = await up.PrepareAsync(await up.CheckAsync());
        Assert.True(p.Ok, p.Message);

        using var t2 = new TempDir();
        var store2 = new AppVersionStore(t2.Sub("versions"));
        var bad = Serve(new MapHandler(), A, "1.2.0", good).Ok(A + AppRelease.PortableName("1.2.0"), GoodZip("evil"));
        var up2 = new AppUpdater(Client(bad, A), store2) { CurrentVersion = "1.0.0" };
        var p2 = await up2.PrepareAsync(await up2.CheckAsync());
        Assert.False(p2.Ok);
        Assert.Contains("контрольная сумма не совпала", p2.Message);
        Assert.Contains("программа не изменена", p2.Message);
        Assert.Contains("sha256", p2.Details);
        Assert.False(store2.IsInstalled("1.2.0"));
        Assert.Empty(Directory.EnumerateFiles(store2.Root, "*", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("DpiShell/../../evil.txt")]
    [InlineData("other/DpiShell.exe")]
    public void Install_RejectsEntriesOutsideFolder(string entry)
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var zip = t.Sub("a.zip");
        File.WriteAllBytes(zip, Zip(new() { ["DpiShell/DpiShell.exe"] = "x", [entry] = "evil" }));
        Assert.Throws<InvalidDataException>(() => store.Install(zip, "1.2.0"));
        Assert.False(store.IsInstalled("1.2.0"));
        Assert.False(File.Exists(t.Sub("evil.txt")));
        Assert.False(Directory.Exists(store.PathFor("1.2.0") + ".new"));
    }

    [Fact]
    public void Install_WithoutExe_Rejected()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var zip = t.Sub("a.zip");
        File.WriteAllBytes(zip, Zip(new() { ["DpiShell/README.txt"] = "x" }));
        Assert.Throws<InvalidDataException>(() => store.Install(zip, "1.2.0"));
        Assert.Throws<ArgumentException>(() => store.PathFor("..\\x"));
    }

    [Fact]
    public void Rollback_And_Prune_KeepCurrentAndPrevious()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        foreach (var v in new[] { "1.0.0", "1.1.0", "1.2.0" })
        {
            var zip = t.Sub(v + ".zip");
            File.WriteAllBytes(zip, GoodZip(v));
            store.Install(zip, v);
        }
        var up = new AppUpdater(Client(new MapHandler(), A), store) { CurrentVersion = "1.1.0", CurrentExe = store.ExeFor("1.1.0"), CurrentPid = 7 };
        Assert.Null(up.Rollback());                    // переключений ещё не было
        up.RestartInto("1.2.0");
        Assert.Equal(["1.0.0"], store.Prune(running: "1.1.0"));
        Assert.True(store.IsInstalled("1.1.0") && store.IsInstalled("1.2.0"));

        // Новая копия (1.2.0) откатывается на 1.1.0; прежней становится 1.2.0 — можно вернуться обратно.
        var now = new AppUpdater(Client(new MapHandler(), A), store) { CurrentVersion = "1.2.0", CurrentExe = store.ExeFor("1.2.0"), CurrentPid = 8 };
        var back = now.Rollback()!;
        Assert.Equal(store.ExeFor("1.1.0"), back.Exe);
        Assert.Equal("1.1.0", back.Version);
        Assert.Equal(new AppVersionsState("1.1.0", "1.2.0", store.ExeFor("1.2.0")), store.State);
    }

    /// <summary>Мало места: до скачивания, ничего не качается и не пишется, фраза называет диск и числа.</summary>
    [Fact]
    public async Task Prepare_NotEnoughSpace_FailsBeforeDownload()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var h = Serve(new MapHandler(), A, "1.2.0");
        var up = new AppUpdater(Client(h, A), store) { CurrentVersion = "1.0.0", FreeSpace = _ => 120L * 1024 * 1024 };
        var check = await up.CheckAsync();
        h.Requests.Clear();
        var p = await up.PrepareAsync(check);
        Assert.False(p.Ok);
        Assert.Contains("Не хватает места", p.Message);
        Assert.Contains("600 МБ", p.Message);
        Assert.Contains("120 МБ", p.Message);
        Assert.Contains("программа не изменена", p.Message);
        Assert.DoesNotContain(h.Requests, r => r.EndsWith(".zip"));
        Assert.False(store.IsInstalled("1.2.0"));

        // Места хватает — обычная установка.
        var ok = new AppUpdater(Client(h, A), store) { CurrentVersion = "1.0.0", FreeSpace = _ => 5L * 1024 * 1024 * 1024 };
        Assert.True((await ok.PrepareAsync(check)).Ok);
    }

    /// <summary>Диск кончился посреди записи (ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL) узнаётся по коду, остальные ошибки ввода-вывода — нет.</summary>
    [Fact]
    public void DiskFull_RecognizedByHResult()
    {
        Assert.True(AppUpdater.IsDiskFull(new IOException("диск полон", unchecked((int)0x80070070))));
        Assert.True(AppUpdater.IsDiskFull(new IOException("диск полон", unchecked((int)0x80070027))));
        Assert.False(AppUpdater.IsDiskFull(new IOException("файл занят", unchecked((int)0x80070020))));
    }

    /// <summary>
    /// Новая версия, которая стартовала и не подтвердила запуск, на втором старте возвращает прежнюю сама;
    /// подтверждённая — остаётся. Неудачная запоминается для одного сообщения и не предлагается кнопкой отката.
    /// </summary>
    [Fact]
    public void Startup_UnconfirmedNewVersion_RollsBackOnSecondStart()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        foreach (var v in new[] { "1.0.0", "1.2.0" })
        {
            var zip = t.Sub(v + ".zip");
            File.WriteAllBytes(zip, GoodZip(v));
            store.Install(zip, v);
        }
        AppUpdater Make(string v) => new(Client(new MapHandler(), A), store) { CurrentVersion = v, CurrentExe = store.ExeFor(v), CurrentPid = 5 };

        // 1.0.0 обновляется до 1.2.0: запись «ждёт подтверждения».
        Make("1.0.0").RestartInto("1.2.0");
        Assert.Equal("1.2.0", store.State.Pending);

        // Первый старт 1.2.0 — продолжает; упал до показа окна. Второй — возвращает 1.0.0.
        Assert.Null(Make("1.2.0").CheckStartup());
        Assert.Equal(1, store.State.PendingStarts);
        var back = Make("1.2.0").CheckStartup()!;
        Assert.Equal(store.ExeFor("1.0.0"), back.Exe);
        Assert.Equal(["--wait-pid", "5", "--updated-from", "1.2.0"], back.Args);
        Assert.Equal(new AppVersionsState("1.0.0", null, null, null, 0, "1.2.0"), store.State);

        // Прежняя читает сообщение один раз; кнопки отката на неудачную нет.
        var old = Make("1.0.0");
        Assert.False(old.CanRollback);
        Assert.Equal("1.2.0", old.TakeFailedNotice());
        Assert.Null(old.TakeFailedNotice());
    }

    [Fact]
    public void Startup_ConfirmedVersion_StaysAndNoPendingLeft()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        foreach (var v in new[] { "1.0.0", "1.2.0" })
        {
            var zip = t.Sub(v + ".zip");
            File.WriteAllBytes(zip, GoodZip(v));
            store.Install(zip, v);
        }
        AppUpdater Make(string v) => new(Client(new MapHandler(), A), store) { CurrentVersion = v, CurrentExe = store.ExeFor(v), CurrentPid = 5 };
        Make("1.0.0").RestartInto("1.2.0");
        var run = Make("1.2.0");
        Assert.Null(run.CheckStartup());
        run.ConfirmStarted();
        Assert.Null(store.State.Pending);
        Assert.Null(Make("1.2.0").CheckStartup());                // следующие запуски — без проверки
        Assert.True(run.CanRollback);
        Assert.Equal("1.0.0", run.PreviousVersion);

        // Откат вручную не оставляет «ждёт подтверждения»: прежняя уже проверена.
        Assert.NotNull(run.Rollback());
        Assert.Null(store.State.Pending);
        Assert.Null(Make("1.0.0").CheckStartup());
    }

    [Fact]
    public void Startup_NoPreviousExe_DoesNotLoop()
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var zip = t.Sub("a.zip");
        File.WriteAllBytes(zip, GoodZip("1.2.0"));
        store.Install(zip, "1.2.0");
        // Прежняя portable-папка пользователя исчезла: откатываться некуда — не зацикливаться, а работать как есть.
        store.Save(new("1.2.0", "1.0.0", t.Sub("gone\\DpiShell.exe"), Pending: "1.2.0", PendingStarts: 1));
        var up = new AppUpdater(Client(new MapHandler(), A), store) { CurrentVersion = "1.2.0" };
        Assert.Null(up.CheckStartup());
        Assert.Null(store.State.Pending);
    }

    [Fact]
    public void WaitForPrevious_ParsesArgs_DoesNotHangOnMissingPid()
    {
        Assert.Null(AppUpdater.WaitForPrevious(["--tray"], TimeSpan.FromSeconds(1)));
        Assert.Equal("1.0.0", AppUpdater.WaitForPrevious(["--wait-pid", "999999", "--updated-from", "1.0.0"], TimeSpan.FromSeconds(5)));
        Assert.Null(AppUpdater.WaitForPrevious(["--updated-from", "..\\x"], TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Settings_DailySchedule_AndFile()
    {
        var now = new DateTimeOffset(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
        var s = new AppUpdateSettings();
        Assert.True(s.IsDue(now));
        s = s.Checked(now, "1.2.0");
        Assert.False(s.IsDue(now.AddHours(23)));
        Assert.True(s.IsDue(now.AddHours(24)));
        Assert.True(s.IsDue(now.AddHours(-1)));         // часы ушли назад
        Assert.False((s with { AutoCheck = false }).IsDue(now.AddDays(3)));
        Assert.Equal("1.2.0", s.Checked(now, null).LatestVersion);
        Assert.True(s.ShouldOffer("1.2.0", "1.0.0"));
        Assert.False((s with { SkippedVersion = "1.2.0" }).ShouldOffer("1.2.0", "1.0.0"));
        Assert.False(s.ShouldOffer("1.0.0", "1.0.0.0"));

        using var t = new TempDir();
        var file = new AppUpdateSettingsFile(t.Sub("app-update.json"));
        Assert.Equal(new AppUpdateSettings(), file.Load());
        file.Save(s);
        Assert.Equal(s, file.Load());
        File.WriteAllText(file.Path, "{ испорчен");
        Assert.Equal(new AppUpdateSettings(), file.Load());
    }

    [Fact]
    public void Sources_FromFile_AndDefault()
    {
        var s = UpdateSources.Load(Path.Combine(Repo.Data, UpdateSources.FileName));
        Assert.Equal(["https://github.com/Slavdew/dpishell/releases/latest/download/"], s.AppSources);
        Assert.Equal(UpdateSources.DefaultAppSources, UpdateSources.From([A]).AppSources);
        Assert.Equal([B], UpdateSources.From([A], null, ["http://plain.example/", B]).AppSources);
    }

    /// <summary>Версия программы — из Directory.Build.props (единый источник, CI сверяет с тегом).</summary>
    [Fact]
    public void CurrentVersion_FromBuildProps()
    {
        var props = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "Directory.Build.props"));
        var v = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>").Groups[1].Value;
        Assert.Equal(v, AppRelease.CurrentVersion);
        // Заметки к выпуску текущей версии есть — иначе CI не выпустит тег.
        var notes = JsonNode.Parse(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "packaging", "release-notes.json")))!.AsObject();
        // Запись версии — {"ru": [...], "en": [...]}, перевод один к одному (packaging/release-json.py сверяет то же).
        var entry = notes[v]!.AsObject();
        Assert.NotEmpty(entry["ru"]!.AsArray());
        Assert.Equal(entry["ru"]!.AsArray().Count, entry["en"]!.AsArray().Count);
    }
}
