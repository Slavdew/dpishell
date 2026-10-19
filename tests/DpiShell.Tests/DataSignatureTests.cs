using System.Text;
using System.Text.Json.Nodes;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Подпись манифеста данных data.json.sig (architecture.md «Обновления данных», building.md «Подписи»).</summary>
public sealed partial class DataUpdateTests
{
    static readonly TestSigner DataKey = new();

    static DataUpdateClient SignedClient(MapHandler h, bool require = false, params string[] sources) =>
        new(new HttpClient(h), UpdateSources.From(sources)) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = DataKey.PublicKey, RequireSignature = require };

    static MapHandler ServeSigned(MapHandler h, string source, (string Json, Dictionary<string, byte[]> Files) remote, TestSigner? by = null)
    {
        Serve(h, source, remote);
        return h.Ok(source + DataUpdateClient.SignatureName, (by ?? DataKey).Sign(Encoding.UTF8.GetBytes(remote.Json)));
    }

    [Fact]
    public async Task Signature_Valid_IsSigned_Missing_IsUnsigned()
    {
        var h = ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-12-01"));
        var r = await SignedClient(h, false, A).FetchManifestAsync();
        Assert.True(r.Result!.Signed);
        Assert.NotNull(r.Result.Signature);

        // Переходный период: подписи у источника нет вовсе — манифест принят, но «не подписан».
        var plain = Serve(new MapHandler(), A, Remote(Repo.Data, "2026-12-01"));
        var u = await SignedClient(plain, false, A).FetchManifestAsync();
        Assert.False(u.Result!.Signed);
        Assert.Null(u.Result.Signature);

        // Конец переходного периода: без подписи — отказ.
        var req = await SignedClient(plain, true, A).FetchManifestAsync();
        Assert.Null(req.Result);
        Assert.Contains("без подписи", req.Errors[0]);
    }

    [Fact]
    public async Task Signature_Wrong_SourceRejected_NextTried()
    {
        // A: подпись чужим ключом; B: верная. Неверная подпись — источник отброшен, а не «не подписан».
        var h = ServeSigned(ServeSigned(new MapHandler(), A, Remote(Repo.Data, "2026-12-01"), new TestSigner()), B, Remote(Repo.Data, "2026-12-02"));
        var r = await SignedClient(h, false, A, B).FetchManifestAsync();
        Assert.Equal(B, r.Result!.Source);
        Assert.True(r.Result.Signed);
        Assert.Contains("подпись манифеста не совпала", r.Errors[0]);

        // Подпись от другого манифеста (подменили data.json, sig оставили) — тоже отказ.
        var swapped = Serve(new MapHandler(), A, Remote(Repo.Data, "2026-12-09"))
            .Ok(A + DataUpdateClient.SignatureName, DataKey.Sign(Encoding.UTF8.GetBytes(Remote(Repo.Data, "2026-12-01").Json)));
        var s = await SignedClient(swapped, false, A).FetchManifestAsync();
        Assert.Null(s.Result);
    }

    [Fact]
    public async Task Signature_Unreachable_IsNotTreatedAsMissing()
    {
        // Подпись не скачалась (обрыв) — это не «подписи нет»: источник пропускается.
        var h = Serve(new MapHandler(), A, Remote(Repo.Data, "2026-12-01")).Fail(A + DataUpdateClient.SignatureName);
        var r = await SignedClient(h, false, A).FetchManifestAsync();
        Assert.Null(r.Result);
        Assert.Contains("подпись манифеста не получена", r.Errors[0]);
    }

    [Fact]
    public async Task Install_Signed_KeepsSignature_AndAllowsCoreFromAnyHttps()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var core = JsonNode.Parse(File.ReadAllText(Path.Combine(pkg, "core.json")))!.AsObject();
        core["version"] = "v72.14";
        core["zip_url"] = "https://github.com/Slavdew/zapret/releases/download/v72.14/zapret-v72.14.zip";
        var remote = Remote(pkg, "2026-12-01", new() { ["core.json"] = Encoding.UTF8.GetBytes(core.ToJsonString()) });
        var h = ServeSigned(new MapHandler(), A, remote);
        bool installed = false;
        var r = await new DataUpdater(paths, SignedClient(h, false, A)) { InstallCore = _ => { installed = true; return Task.CompletedTask; } }.InstallAsync(false);
        Assert.True(r.Applied, r.Message);
        Assert.True(r.Signed);
        Assert.True(installed);
        var sigFile = Path.Combine(paths.Updates, DataUpdateClient.SignatureName);
        Assert.True(ReleaseSignature.Verify(File.ReadAllBytes(Path.Combine(paths.Updates, DataManifest.FileName)), File.ReadAllBytes(sigFile), DataKey.PublicKey));

        // Следующее обновление без подписи — старая подпись рядом с новым манифестом не остаётся.
        paths.ResetSeed();
        var h2 = Serve(new MapHandler(), A, Remote(pkg, "2026-12-02"));
        var r2 = await new DataUpdater(paths, SignedClient(h2, false, A)) { InstallCore = _ => Task.CompletedTask }.InstallAsync(false);
        Assert.True(r2.Applied, r2.Message);
        Assert.False(r2.Signed);
        Assert.False(File.Exists(sigFile));
    }

    [Fact]
    public async Task Default_RequiresSignature_UnsignedInstallRejected_DataKept()
    {
        Assert.True(new DataUpdateClient(new HttpClient(), UpdateSources.From([A])).RequireSignature);
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var before = paths.SeedManifest!.Version;
        var h = Serve(new MapHandler(), A, Remote(pkg, "2026-12-01"));
        var client = new DataUpdateClient(new HttpClient(h), UpdateSources.From([A])) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = DataKey.PublicKey };
        var r = await new DataUpdater(paths, client) { InstallCore = _ => Task.CompletedTask }.InstallAsync(false);
        Assert.False(r.Applied);
        Assert.Contains("без подписи", r.Message);
        paths.ResetSeed();
        Assert.Equal(before, paths.SeedManifest!.Version);
    }

    [Fact]
    public async Task Install_TamperedManifest_Rejected()
    {
        using var t = new TempDir();
        var (paths, pkg) = Setup(t, A);
        var good = Remote(pkg, "2026-12-01");
        var evil = Remote(pkg, "2026-12-09");
        var h = Serve(new MapHandler(), A, evil).Ok(A + DataUpdateClient.SignatureName, DataKey.Sign(Encoding.UTF8.GetBytes(good.Json)));
        var r = await new DataUpdater(paths, SignedClient(h, true, A)) { InstallCore = _ => Task.CompletedTask }.InstallAsync(false);
        Assert.False(r.Applied);
        Assert.Contains("подпись манифеста не совпала", r.Message);
    }

    [Fact]
    public void ManifestKey_NeverSignature() => Assert.False(DataKeys.IsAllowed(DataUpdateClient.SignatureName));
}
