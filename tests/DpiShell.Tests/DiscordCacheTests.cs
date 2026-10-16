using System.Text;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>«Очистить кэш Discord»: только Cache / Code Cache / GPUCache, остальное (вход, настройки) не трогается. Только временная папка.</summary>
public sealed class DiscordCacheTests
{
    static void Put(string file, int bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[bytes]);
    }

    [Fact]
    public void Find_SeesOnlyCacheFolders_OfAllClients()
    {
        using var tmp = new TempDir();
        Put(tmp.Sub(@"discord\Cache\Cache_Data\data_0"), 100);
        Put(tmp.Sub(@"discord\Code Cache\js\a"), 50);
        Put(tmp.Sub(@"discordptb\GPUCache\data_1"), 10);
        Put(tmp.Sub(@"discord\Local Storage\leveldb\000003.log"), 999);
        Put(tmp.Sub(@"discord\settings.json"), 7);

        var found = DiscordCache.Find(tmp.Path);

        Assert.Equal(3, found.Folders.Count);
        Assert.Equal(160, found.Bytes);
        Assert.DoesNotContain(found.Folders, f => f.Contains("Local Storage"));
    }

    [Fact]
    public void Clear_DeletesCaches_KeepsLoginAndSettings()
    {
        using var tmp = new TempDir();
        Put(tmp.Sub(@"discord\Cache\Cache_Data\data_0"), 100);
        Put(tmp.Sub(@"discord\Code Cache\wasm\b"), 20);
        Put(tmp.Sub(@"discordcanary\GPUCache\data_1"), 10);
        Put(tmp.Sub(@"discord\Local Storage\leveldb\000003.log"), 999);
        Put(tmp.Sub(@"discord\settings.json"), 7);

        var r = DiscordCache.Clear(tmp.Path);

        Assert.Equal(130, r.Bytes);
        Assert.Empty(r.Failed);
        Assert.False(Directory.Exists(tmp.Sub(@"discord\Cache")));
        Assert.False(Directory.Exists(tmp.Sub(@"discord\Code Cache")));
        Assert.False(Directory.Exists(tmp.Sub(@"discordcanary\GPUCache")));
        Assert.True(File.Exists(tmp.Sub(@"discord\Local Storage\leveldb\000003.log")));
        Assert.True(File.Exists(tmp.Sub(@"discord\settings.json")));
        Assert.Empty(DiscordCache.Find(tmp.Path).Folders);
    }

    [Fact]
    public void Clear_LockedFile_IsReported_OthersDeleted()
    {
        using var tmp = new TempDir();
        Put(tmp.Sub(@"discord\Cache\locked"), 10);
        Put(tmp.Sub(@"discord\Cache\free"), 20);
        using (new FileStream(tmp.Sub(@"discord\Cache\locked"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var r = DiscordCache.Clear(tmp.Path);
            Assert.Single(r.Failed);
            Assert.Equal(20, r.Bytes);
        }
        Assert.True(File.Exists(tmp.Sub(@"discord\Cache\locked")));
        Assert.False(File.Exists(tmp.Sub(@"discord\Cache\free")));
    }

    [Fact]
    public void NoDiscord_NothingFound()
    {
        using var tmp = new TempDir();
        var r = DiscordCache.Clear(tmp.Path);
        Assert.Empty(r.Folders);
        Assert.Equal(0, r.Bytes);
    }
}

/// <summary>Проверки «Диагностики» без действий: разбор hosts, путь, прокси, службы — на данных, без системы.</summary>
public sealed class SystemChecksTests
{
    [Fact]
    public void YoutubeLines_OutsideOurBlock_Found()
    {
        var hosts = "# comment\r\n127.0.0.1 localhost\r\n0.0.0.0 www.youtube.com youtu.be\r\n"
                    + "# DpiShell begin v1 2026-10-17\r\n146.75.22.132 raw.githubusercontent.com\r\n# DpiShell end\r\n"
                    + "1.2.3.4 rr1---sn-abc.googlevideo.com # ускоритель\r\n";
        var found = SystemChecks.YoutubeLinesOutsideBlock(Encoding.ASCII.GetBytes(hosts));
        Assert.Equal(2, found.Count);
        Assert.StartsWith("0.0.0.0 www.youtube.com", found[0]);
    }

    [Fact]
    public void YoutubeLines_Utf16AndClean_NoneFound()
    {
        var clean = "127.0.0.1 localhost\n140.82.112.4 github.com\n# 0.0.0.0 youtube.com\n";
        Assert.Empty(SystemChecks.YoutubeLinesOutsideBlock([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(clean)]));
        Assert.Single(SystemChecks.YoutubeLinesOutsideBlock([0xFF, 0xFE, .. Encoding.Unicode.GetBytes(clean + "0.0.0.0 youtube.com\n")]));
    }

    [Theory]
    [InlineData(@"C:\Program Files\DpiShell\", false)]
    [InlineData(@"C:\Users\Иван\Desktop\DpiShell\", true)]
    [InlineData(@"C:\Users\ivan\OneDrive\DpiShell\", true)]
    public void PathProblem(string path, bool bad) => Assert.Equal(bad, SystemChecks.PathProblem(path) is not null);

    [Fact]
    public void Proxy_ShowsAddress()
    {
        var on = SystemChecks.ProxyFinding((true, "127.0.0.1:8888"));
        Assert.False(on.Ok);
        Assert.Contains("127.0.0.1:8888", on.Text);
        Assert.True(SystemChecks.ProxyFinding((false, "127.0.0.1:8888")).Ok);
    }

    [Fact]
    public void Services_MatchByNameOrDisplayName()
    {
        var m = SystemChecks.MatchServices([
            ("KNDBWM", "Killer Network Service"), ("SmartByteNetworkService", ""), ("Dnscache", "DNS-клиент"),
            ("jhi_service", "Intel(R) Connectivity Network Service"),
        ]);
        Assert.Equal(["KNDBWM", "SmartByteNetworkService", "jhi_service"], m.Select(x => x.Name));
    }
}
