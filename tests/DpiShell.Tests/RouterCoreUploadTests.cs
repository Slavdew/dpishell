using System.Net;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>Ядро zapret для роутера: окно качает и проверяет само, кладёт в /tmp/dpishell/core/zapret.zip до установки.</summary>
public class RouterCoreUploadTests
{
    static string Vendor => Path.Combine(Path.GetDirectoryName(Repo.Data)!, "vendor", "zapret-v72.13.zip");

    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Urls.Add(r.RequestUri!.ToString());
            return Task.FromResult(f(r));
        }
    }

    [Fact]
    public void EmbeddedManifest_MatchesVendorAndWindowsCore()
    {
        var m = RouterCoreManifest.Embedded();
        Assert.Equal("zapret-v72.13.zip", m.ZipName);
        Assert.Equal(m.ZipSha256, CoreInstaller.Sha256(Vendor));
        Assert.Equal(CoreManifest.Load(Path.Combine(Repo.Data, "core.json")).ZipSha256, m.ZipSha256);
        Assert.Contains("arm64", m.Nfqws.Keys);
    }

    [Fact]
    public async Task Ensure_FromLocalCopy_NoNetwork()
    {
        using var d = new TempDir();
        var h = new Handler(_ => throw new InvalidOperationException("сети быть не должно"));
        var a = new RouterCoreArchive(RouterCoreManifest.Embedded(), d.Sub("cache"));
        var path = await a.EnsureAsync(new HttpClient(h), [d.Sub("нет.zip"), Vendor]);
        Assert.Equal(a.CachePath, path);
        Assert.True(a.IsValid(path));
        Assert.Empty(h.Urls);
        // Второй раз — из кэша.
        Assert.Equal(path, await a.EnsureAsync(new HttpClient(h)));
    }

    [Fact]
    public async Task Ensure_Download_WrongHashOnMain_MirrorOk()
    {
        using var d = new TempDir();
        var good = await File.ReadAllBytesAsync(Vendor);
        var m = RouterCoreManifest.Embedded();
        var h = new Handler(r => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(r.RequestUri!.ToString() == m.ZipUrl ? "подмена"u8.ToArray() : good),
        });
        var a = new RouterCoreArchive(m, d.Sub("cache"));
        var path = await a.EnsureAsync(new HttpClient(h));
        Assert.Equal([m.ZipUrl, m.MirrorUrls![0]], h.Urls);
        Assert.True(a.IsValid(path));
    }

    [Fact]
    public async Task Ensure_AllWrong_Throws_NoCache()
    {
        using var d = new TempDir();
        var h = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("подмена"u8.ToArray()) });
        var a = new RouterCoreArchive(RouterCoreManifest.Embedded(), d.Sub("cache"));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => a.EnsureAsync(new HttpClient(h)));
        Assert.Contains("хеш не совпал", e.Message);
        Assert.False(File.Exists(a.CachePath));
    }

    [Fact]
    public async Task UploadCore_MkdirChmod_UploadVerified_ThenSkip()
    {
        var m = RouterCoreManifest.Embedded();
        var ssh = new FakeSsh();
        var r = new RouterSsh(ssh);
        await using (var s = File.OpenRead(Vendor)) await r.UploadCoreAsync(s, m.ZipSha256);
        Assert.Equal("mkdir -p /tmp/dpishell/core && chmod 700 /tmp/dpishell /tmp/dpishell/core", ssh.Runs[0].Command);
        Assert.Equal([RouterSsh.CoreZip], ssh.Uploads);
        Assert.Equal(new FileInfo(Vendor).Length, ssh.Files[RouterSsh.CoreZip].Length);

        await using (var s = File.OpenRead(Vendor)) await r.UploadCoreAsync(s, m.ZipSha256);
        Assert.Single(ssh.Uploads);   // уже лежит с тем же хешем — повторно не грузим
    }

    [Fact]
    public async Task UploadCore_HashMismatchOnRouter_RemovesAndThrows()
    {
        var ssh = new FakeSsh { CorruptUpload = b => b[..100] };
        await using var s = File.OpenRead(Vendor);
        var e = await Assert.ThrowsAsync<RouterSshException>(() => new RouterSsh(ssh).UploadCoreAsync(s, RouterCoreManifest.Embedded().ZipSha256));
        Assert.Contains("не совпал по sha256", e.Message);
        Assert.False(ssh.Files.ContainsKey(RouterSsh.CoreZip));
    }
}
