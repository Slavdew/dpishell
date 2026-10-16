using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class CoreInstallerTests : IAsyncLifetime
{
    static readonly byte[] Winws = Encoding.UTF8.GetBytes("winws-binary-v1");
    static readonly byte[] Divert = Encoding.UTF8.GetBytes("windivert-dll-v1");

    readonly TempDir _t = new();
    byte[] _zip = [];
    RawHttpServer _srv = null!;
    AppPaths _p = null!;

    static string Hash(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    static byte[] MakeZip()
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data) { using var s = z.CreateEntry(name).Open(); s.Write(data); }
            Add("x/winws.exe", Winws);
            Add("x/WinDivert.dll", Divert);
            Add("x/readme.txt", Encoding.UTF8.GetBytes("not listed in manifest"));
            Add("other/winws.exe", Encoding.UTF8.GetBytes("wrong dir"));
        }
        return ms.ToArray();
    }

    public Task InitializeAsync()
    {
        _zip = MakeZip();
        _srv = new RawHttpServer((path, s, ct) => path switch
        {
            "/core.zip" => RawHttpServer.RespondAsync(s, 200, _zip, "Content-Type: application/zip"),
            "/tampered.zip" => RawHttpServer.RespondAsync(s, 200, [.. _zip, 0], "Content-Type: application/zip"),
            _ => RawHttpServer.RespondAsync(s, 404, []),
        });
        _p = new AppPaths(_t.Sub("root"), Repo.Data);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _srv.DisposeAsync();
        _t.Dispose();
    }

    CoreManifest Manifest(string? zipHash = null, string? winwsHash = null, string zipDir = "x/", string? extraFile = null)
    {
        var files = new Dictionary<string, string> { ["winws.exe"] = winwsHash ?? Hash(Winws), ["WinDivert.dll"] = Hash(Divert) };
        if (extraFile is not null) files[extraFile] = Hash(Winws);
        return new("vtest", _srv.Url("/core.zip"), zipHash ?? Hash(_zip), zipDir, files);
    }

    string TmpDir => Path.Combine(_p.Root, "core.download");

    void PutOldCore()
    {
        Directory.CreateDirectory(_p.Core);
        File.WriteAllText(Path.Combine(_p.Core, "winws.exe"), "old");
    }

    void AssertOldCoreUntouched()
    {
        Assert.Equal(["winws.exe"], Directory.GetFiles(_p.Core).Select(Path.GetFileName));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_p.Core, "winws.exe")));
        Assert.False(Directory.Exists(_p.Core + ".old"));
    }

    [Fact]
    public async Task Install_GoodManifest_InstallsExactlyManifestFiles_CheckOk()
    {
        var inst = new CoreInstaller(_p, Manifest());
        Assert.Equal(CoreState.Missing, inst.Check().State);

        using var http = new HttpClient();
        var log = new List<string>();
        await inst.InstallAsync(http, new SyncProgress(log));

        Assert.Equal(Winws, File.ReadAllBytes(Path.Combine(_p.Core, "winws.exe")));
        Assert.Equal(Divert, File.ReadAllBytes(Path.Combine(_p.Core, "WinDivert.dll")));
        Assert.False(File.Exists(Path.Combine(_p.Core, "readme.txt")));
        Assert.False(Directory.Exists(TmpDir));
        Assert.False(Directory.Exists(_p.Core + ".old"));
        var c = inst.Check();
        Assert.Equal(CoreState.Ok, c.State);
        Assert.Empty(c.BadFiles);
        Assert.Empty(c.ExtraFiles);
        Assert.NotEmpty(log);
    }

    [Fact]
    public async Task Install_ReplacesOldCore_DropsForeignFiles()
    {
        PutOldCore();
        File.WriteAllText(Path.Combine(_p.Core, "evil.dll"), "x");
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest());
        await inst.InstallAsync(http);
        Assert.Equal(["WinDivert.dll", "winws.exe"], Directory.GetFiles(_p.Core).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(CoreState.Ok, inst.Check().State);
    }

    [Fact]
    public async Task Install_HashCaseInsensitive()
    {
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest(zipHash: Hash(_zip).ToUpperInvariant(), winwsHash: Hash(Winws).ToUpperInvariant()));
        await inst.InstallAsync(http);
        Assert.Equal(CoreState.Ok, inst.Check().State);
    }

    [Theory]
    [InlineData("/missing.zip")]   // основной адрес пропал (404)
    [InlineData("/tampered.zip")]  // основной отдал подменённый архив
    public async Task Install_PrimaryFails_MirrorUsed(string primary)
    {
        using var http = new HttpClient();
        var m = Manifest() with { ZipUrl = _srv.Url(primary), MirrorUrls = [_srv.Url("/core.zip")] };
        var log = new List<string>();
        await new CoreInstaller(_p, m).InstallAsync(http, new SyncProgress(log));
        Assert.Equal(CoreState.Ok, new CoreInstaller(_p, m).Check().State);
        Assert.Contains(log, l => l.Contains("запасной"));
    }

    [Fact]
    public async Task Install_AllMirrorsTampered_SecurityException()
    {
        using var http = new HttpClient();
        var m = Manifest() with { ZipUrl = _srv.Url("/tampered.zip"), MirrorUrls = [_srv.Url("/tampered.zip")] };
        await Assert.ThrowsAsync<SecurityException>(() => new CoreInstaller(_p, m).InstallAsync(http));
        Assert.Empty(Directory.Exists(_p.Core) ? Directory.GetFiles(_p.Core) : []);
    }

    [Fact]
    public async Task Install_WrongZipHash_SecurityException_OldCoreUntouched()
    {
        PutOldCore();
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest(zipHash: new string('0', 64)));
        await Assert.ThrowsAsync<SecurityException>(() => inst.InstallAsync(http));
        AssertOldCoreUntouched();
        Assert.False(Directory.Exists(TmpDir));
    }

    [Fact]
    public async Task Install_WrongFileHash_Throws_TempRemoved_OldCoreUntouched()
    {
        PutOldCore();
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest(winwsHash: new string('a', 64)));
        await Assert.ThrowsAsync<SecurityException>(() => inst.InstallAsync(http));
        Assert.False(Directory.Exists(TmpDir));
        AssertOldCoreUntouched();
    }

    [Fact]
    public async Task Install_FileMissingInZip_Throws_NoCoreCreated()
    {
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest(extraFile: "cygwin1.dll"));
        await Assert.ThrowsAsync<SecurityException>(() => inst.InstallAsync(http));
        Assert.False(Directory.Exists(TmpDir));
        Assert.Empty(Directory.GetFiles(_p.Core)); // EnsureDirectories создаёт пустую core\ — файлов нет
    }

    [Fact]
    public async Task Install_WrongZipDir_Throws()
    {
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest(zipDir: "y/"));
        await Assert.ThrowsAsync<SecurityException>(() => inst.InstallAsync(http));
        Assert.False(Directory.Exists(TmpDir));
    }

    async Task<CoreInstaller> Installed()
    {
        using var http = new HttpClient();
        var inst = new CoreInstaller(_p, Manifest());
        await inst.InstallAsync(http);
        return inst;
    }

    [Fact]
    public async Task Check_ExtraFile_ReportedInExtraFiles()
    {
        var inst = await Installed();
        File.WriteAllText(Path.Combine(_p.Core, "cygwin1.dll"), "foreign");
        var c = inst.Check();
        Assert.Equal(CoreState.Ok, c.State); // по коду: лишний файл не портит State, только попадает в ExtraFiles
        Assert.Equal(["cygwin1.dll"], c.ExtraFiles);
    }

    [Fact]
    public async Task Check_OneFileDeleted_Modified()
    {
        var inst = await Installed();
        File.Delete(Path.Combine(_p.Core, "WinDivert.dll"));
        var c = inst.Check();
        Assert.Equal(CoreState.Modified, c.State);
        Assert.Equal(["WinDivert.dll"], c.BadFiles);
    }

    [Fact]
    public async Task Check_AllFilesDeleted_Missing()
    {
        var inst = await Installed();
        foreach (var f in Directory.GetFiles(_p.Core)) File.Delete(f);
        var c = inst.Check();
        Assert.Equal(CoreState.Missing, c.State);
        Assert.Equal(2, c.BadFiles.Count);
    }

    [Fact]
    public async Task Check_CoreDirDeleted_Missing()
    {
        var inst = await Installed();
        Directory.Delete(_p.Core, true);
        var c = inst.Check();
        Assert.Equal(CoreState.Missing, c.State);
        Assert.Equal(2, c.BadFiles.Count);
    }

    [Fact]
    public async Task Check_FileContentChanged_Modified()
    {
        var inst = await Installed();
        File.WriteAllText(Path.Combine(_p.Core, "winws.exe"), "patched");
        var c = inst.Check();
        Assert.Equal(CoreState.Modified, c.State);
        Assert.Equal(["winws.exe"], c.BadFiles);
    }

    [Fact]
    public void RealManifest_Loads()
    {
        var m = CoreManifest.Load(Path.Combine(Repo.Data, "core.json"));
        Assert.Equal(4, m.Files.Count);
        Assert.Contains("winws.exe", m.Files.Keys);
        Assert.All(m.Files.Values.Append(m.ZipSha256), h => Assert.Matches("^[0-9a-f]{64}$", h));
        Assert.EndsWith("/", m.ZipDir);
    }

    sealed class SyncProgress(List<string> sink) : IProgress<string>
    {
        public void Report(string value) { lock (sink) sink.Add(value); }
    }
}
