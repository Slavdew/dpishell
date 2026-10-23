using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DpiShell.Core;
using static DpiShell.Tests.AppUpdateTests;

namespace DpiShell.Tests;

/// <summary>
/// Защита от «старого» выпуска (аудит №7, вариант «б»): у подписанных release.json и data.json — дата issued; описание старше 120 дней —
/// «не удалось проверить обновления», а не «обновлений нет». Без issued — как раньше. Ниже установленной версии не ставится ничто.
/// </summary>
public sealed class ReleaseFreshnessTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    internal static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    static byte[] Rel(string version, string? issued)
    {
        var o = JsonNode.Parse(ReleaseJson(version, GoodZip(version)))!.AsObject();
        if (issued is not null) o["issued"] = issued;
        else o.Remove("issued");
        return Encoding.UTF8.GetBytes(o.ToJsonString() + "\n");
    }

    static AppUpdateClient FreshClient(MapHandler h, params string[] sources) =>
        new(new HttpClient(h), sources) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = Key.PublicKey, OsVersion = new Version(10, 0, 26200), Now = () => Now };

    [Fact]
    public void Issued_format_is_strict()
    {
        Assert.True(ReleaseFreshness.TryParse("2026-10-22T09:15:00Z", out var a));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 15, 0, TimeSpan.Zero), a);
        Assert.True(ReleaseFreshness.TryParse("2026-10-22T12:15:00+03:00", out var b));
        Assert.Equal(a, b);
        foreach (var bad in new[] { "2026-10-22", "2026-10-22T09:15:00", "22.10.2026", "", "2026-13-01T00:00:00Z", "2026-10-22T09:15:00Z junk" })
            Assert.False(ReleaseFreshness.TryParse(bad, out _), bad);
        Assert.False(ReleaseFreshness.IsStale(null, Now));                       // без даты — как раньше
        Assert.False(ReleaseFreshness.IsStale(Now.AddDays(-120), Now));
        Assert.True(ReleaseFreshness.IsStale(Now.AddDays(-121), Now));
        Assert.False(ReleaseFreshness.IsStale(Now.AddDays(30), Now));            // часы ПК отстают — не тревога
    }

    [Fact]
    public async Task App_stale_release_is_not_no_updates()
    {
        // Подпись верна, версия та же, что стоит, но описанию 200 дней: «новее нет» сказать нельзя.
        var h = Serve(new MapHandler(), A, "1.0.0", release: Rel("1.0.0", Iso(Now.AddDays(-200))));
        var r = await FreshClient(h, A).CheckAsync("1.0.0");
        Assert.Null(r.Found);
        Assert.True(r.Stale);
        Assert.False(r.SignatureFailed);
        Assert.Contains("старше 120 дней", Assert.Single(r.Errors));

        // Свежее зеркало после устаревшего — берётся оно, признак остаётся (для подробностей).
        Serve(h, B, "1.2.0", release: Rel("1.2.0", Iso(Now.AddDays(-2))));
        var m = await FreshClient(h, A, B).CheckAsync("1.0.0");
        Assert.Equal(B, m.Found!.Source);
        Assert.True(m.Available);
        Assert.Equal(Now.AddDays(-2), m.Found.Release.Issued);
    }

    [Fact]
    public async Task App_release_with_fresh_issued_is_accepted_without_or_bad_issued_rejected()
    {
        // release.json до поля issued: подписан верно, но без даты его мог бы вечно отдавать подменённый источник (аудит-2 №6) — «не удалось проверить».
        var old = await FreshClient(Serve(new MapHandler(), A, "1.2.0", release: Rel("1.2.0", null)), A).CheckAsync("1.0.0");
        Assert.Null(old.Found);
        Assert.True(old.Stale);
        Assert.Equal(L.T("core.upd.undated_release", new { host = "github.example" }), Assert.Single(old.Errors));

        var future = await FreshClient(Serve(new MapHandler(), A, "1.2.0", release: Rel("1.2.0", Iso(Now.AddDays(3)))), A).CheckAsync("1.0.0");
        Assert.True(future.Available);

        var bad = await FreshClient(Serve(new MapHandler(), A, "1.2.0", release: Rel("1.2.0", "вчера")), A).CheckAsync("1.0.0");
        Assert.Null(bad.Found);
        Assert.False(bad.Stale);
        Assert.Contains(L.T("core.app.rel.issued"), Assert.Single(bad.Errors));
    }

    [Theory]
    [InlineData("1.0.0")]   // та же
    [InlineData("0.9.0")]   // ниже
    public async Task App_never_installs_same_or_lower_version(string offered)
    {
        using var t = new TempDir();
        var store = new AppVersionStore(t.Sub("versions"));
        var h = Serve(new MapHandler(), A, offered);
        var client = FreshClient(h, A);
        var found = (await client.CheckAsync("0.0.1")).Found!;
        // Проверку собрали в обход CheckAsync и назвали «новее» — установка всё равно отказывает и ничего не качает.
        var forged = new AppUpdateCheck(found, Newer: true, OsTooOld: false, Current: "1.0.0", Errors: []);
        var up = new AppUpdater(client, store) { CurrentVersion = "1.0.0", CurrentExe = t.Sub("DpiShell.exe") };
        int before = h.Requests.Count;
        var p = await up.PrepareAsync(forged);
        Assert.False(p.Ok);
        Assert.Equal(L.T("core.upd.not_newer", new { version = offered, current = "1.0.0" }), p.Message);
        Assert.Equal(before, h.Requests.Count);
        Assert.False(store.IsInstalled(offered));
    }

    [Fact]
    public void Release_script_writes_issued_and_can_reissue()
    {
        // packaging/release-json.py: write ставит issued, reissue меняет только её (CI раз в неделю переподписывает последний выпуск).
        var script = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, "packaging", "release-json.py"));
        Assert.Contains("'issued': now_issued()", script);
        Assert.Contains("mode == 'reissue'", script);
        var ci = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, ".github", "workflows", "windows.yml"));
        Assert.Contains("release-json.py reissue", ci);
        Assert.Contains("schedule:", ci);
        var site = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Repo.Data)!, ".github", "workflows", "router.yml"));
        Assert.Contains("d['issued']=", site);
    }
}

public sealed partial class DataUpdateTests
{
    static DataUpdateClient FreshDataClient(MapHandler h, DateTimeOffset now, params string[] sources) =>
        new(new HttpClient(h), UpdateSources.From(sources)) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = DataKey.PublicKey, Now = () => now };

    [Fact]
    public async Task Freshness_stale_signed_manifest_means_check_failed_not_up_to_date()
    {
        var now = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        var stale = ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-06-01", issued: ReleaseFreshnessTests.Iso(now.AddDays(-121))));
        var r = await FreshDataClient(stale, now, A).FetchManifestAsync();
        Assert.Null(r.Result);
        Assert.True(r.Stale);
        Assert.Contains("старше 120 дней", Assert.Single(r.Errors));

        // Свежее зеркало — принято; подписанный без issued (манифест до этой защиты) — нет (аудит-2 №6).
        ServeSigned(stale, B, Remote(Repo.Data, "2026-12-02", issued: ReleaseFreshnessTests.Iso(now.AddDays(-6))));
        var m = await FreshDataClient(stale, now, A, B).FetchManifestAsync();
        Assert.Equal(B, m.Result!.Source);
        Assert.Equal(now.AddDays(-6), m.Result.Manifest.IssuedAt);
        var plain = await FreshDataClient(ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-12-01", issued: "")), now, A).FetchManifestAsync();
        Assert.Null(plain.Result);
        Assert.True(plain.Stale);
        Assert.Equal(L.T("core.data.undated", new { host = "primary.example" }), Assert.Single(plain.Errors));

        // Нечитаемая дата — манифест не годен.
        var bad = await FreshDataClient(ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-12-01", issued: "2026-10-22")), now, A).FetchManifestAsync();
        Assert.Null(bad.Result);
        Assert.False(bad.Stale);
    }

    [Fact]
    public async Task Freshness_helper_install_says_could_not_check_and_never_goes_lower()
    {
        using var t = new TempDir();
        var (paths, _) = Setup(t, A);
        var stale = ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2099-01-01", issued: "2020-01-01T00:00:00Z"));
        var r = await new DataUpdater(paths, FreshDataClient(stale, DateTimeOffset.UtcNow, A)).InstallAsync(false);
        Assert.False(r.Applied);
        Assert.False(r.UpToDate);
        Assert.StartsWith(L.T("core.data.sources_stale", new { errors = "" }).TrimEnd(), r.Message);

        // Свежий, но старше установленного — «установлено то же или новее», ничего не ставится.
        var older = ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-01-01", issued: ReleaseFreshnessTests.Iso(DateTimeOffset.UtcNow)));
        var o = await new DataUpdater(paths, FreshDataClient(older, DateTimeOffset.UtcNow, A)).InstallAsync(false);
        Assert.False(o.Applied);
        Assert.True(o.UpToDate);
    }
}
