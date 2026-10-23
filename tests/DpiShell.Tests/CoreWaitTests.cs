using System.Net;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Ядро после установки помощника ждёт окна (ТЗ av-exclusion, п. 10; CoreWait) и офлайн-архив ядра (п. 9: core_offline_part/core_offline_install).
/// Сеть — подставная: архив ядра — настоящий vendor\zapret-v72.13.zip (тот же sha256, что в data/core.json).
/// </summary>
public class CoreWaitTests
{
    static string VendorZip => Path.Combine(Path.GetDirectoryName(Repo.Data)!, "vendor", "zapret-v72.13.zip");

    /// <summary>Отдаёт архив ядра по любому адресу и считает запросы.</summary>
    sealed class ZipServer(byte[]? body) : HttpMessageHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return body is null ? throw new HttpRequestException("сети быть не должно")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = r });
        }
    }

    static (Shell Shell, ZipServer Net) NewShell(TempDir t, bool serveZip, DateTime now)
    {
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var net = new ZipServer(serveZip ? File.ReadAllBytes(VendorZip) : null);
        shell.DownloadClient = () => new HttpClient(net, disposeHandler: false);
        shell.UtcNow = () => now;
        return (shell, net);
    }

    [Fact]
    public void Remaining_counts_from_mark_and_never_exceeds_limit()
    {
        using var t = new TempDir();
        var p = new AppPaths(t.Sub("root"), Repo.Data);
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        Assert.Null(CoreWait.Remaining(p, now));
        CoreWait.Begin(p, now);
        Assert.Equal(CoreWait.Limit, CoreWait.Remaining(p, now));
        Assert.Equal(TimeSpan.FromMinutes(5), CoreWait.Remaining(p, now + CoreWait.Limit - TimeSpan.FromMinutes(5)));
        Assert.Null(CoreWait.Remaining(p, now + CoreWait.Limit));
        // Часы перевели назад — дольше предела не ждём; метка не читается — качать сразу.
        Assert.Equal(CoreWait.Limit, CoreWait.Remaining(p, now - TimeSpan.FromHours(3)));
        File.WriteAllText(CoreWait.FilePath(p), "мусор");
        Assert.Null(CoreWait.Remaining(p, now));
        CoreWait.Clear(p);
        Assert.False(CoreWait.Exists(p));
    }

    /// <summary>Служба при старте: метка свежая — ядро не качается (сначала вопрос об исключении); метки нет — качается как раньше.</summary>
    [Fact]
    public async Task Prepare_skips_download_while_waiting_for_window()
    {
        using var t = new TempDir();
        var now = DateTime.UtcNow;
        var (shell, net) = NewShell(t, serveZip: false, now);
        CoreWait.Begin(shell.Paths, now);
        var log = new List<string>();
        var check = await shell.PrepareAsync(new LineProgress(log.Add));
        Assert.Equal(CoreState.Missing, check.State);
        Assert.Equal(0, net.Requests);
        Assert.Contains(log, l => l.Contains("15"));

        using var t2 = new TempDir();
        var (shell2, net2) = NewShell(t2, serveZip: true, now);
        Assert.Equal(CoreState.Ok, (await shell2.PrepareAsync()).State);
        Assert.Equal(1, net2.Requests);
    }

    /// <summary>Окно так и не скачало (нет окна) — по истечении служба качает сама, один раз; окно успело — служба ничего не делает.</summary>
    [Fact]
    public async Task Service_downloads_itself_after_limit_unless_window_did()
    {
        using var t = new TempDir();
        var now = new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
        var (shell, net) = NewShell(t, serveZip: true, now);
        CoreWait.Begin(shell.Paths, now);
        var waited = TimeSpan.Zero;
        shell.UtcNow = () => now;
        shell.Delay = (d, _) => { waited += d; now += d; return Task.CompletedTask; };
        await shell.InstallAfterWaitAsync();
        Assert.Equal(CoreWait.Limit, waited);
        Assert.Equal(1, net.Requests);
        Assert.Equal(CoreState.Ok, shell.Installer.Check().State);
        Assert.False(CoreWait.Exists(shell.Paths));

        using var t2 = new TempDir();
        var (shell2, net2) = NewShell(t2, serveZip: true, now);
        CoreWait.Begin(shell2.Paths, now);
        // Окно пришло через минуту и скачало само (core_repair) — метка снята.
        shell2.UtcNow = () => now;
        shell2.Delay = async (d, _) => { now += d; await shell2.RepairCoreAsync(); };
        await shell2.InstallAfterWaitAsync();
        Assert.Equal(1, net2.Requests);
        Assert.False(CoreWait.Exists(shell2.Paths));
    }

    [Fact]
    public void Offline_archive_installs_only_with_manifest_hash()
    {
        using var t = new TempDir();
        var (shell, net) = NewShell(t, serveZip: false, DateTime.UtcNow);
        var bytes = File.ReadAllBytes(VendorZip);
        var inst = shell.Installer;
        long size = 0;
        for (int i = 0; i < bytes.Length; i += HelperCommands.MaxOfflineChunkBytes)
            size = inst.AppendOffline(bytes.AsSpan(i, Math.Min(HelperCommands.MaxOfflineChunkBytes, bytes.Length - i)), first: i == 0);
        Assert.Equal(bytes.Length, size);
        inst.InstallOffline();
        Assert.Equal(CoreState.Ok, inst.Check().State);
        Assert.False(Directory.Exists(inst.OfflineDir));
        Assert.Equal(0, net.Requests);

        // Не тот архив: отказ, ядро не тронуто, присланное стёрто.
        inst.AppendOffline(bytes.AsSpan(0, 1000), first: true);
        var e = Assert.Throws<SecurityException>(() => inst.InstallOffline());
        Assert.Contains("sha256", e.Message);
        Assert.False(Directory.Exists(inst.OfflineDir));
        Assert.Equal(CoreState.Ok, inst.Check().State);
        // Продолжение без начала и без архива — отказ.
        Assert.Throws<InvalidOperationException>(() => inst.AppendOffline(new byte[1], first: false));
        Assert.Throws<InvalidOperationException>(() => inst.InstallOffline());
    }

    [Fact]
    public void Offline_archive_size_is_limited()
    {
        using var t = new TempDir();
        var inst = new Shell(new AppPaths(t.Sub("root"), Repo.Data)).Installer;
        var chunk = new byte[HelperCommands.MaxOfflineChunkBytes];
        bool first = true;
        Assert.Throws<InvalidOperationException>(() =>
        {
            for (int i = 0; i <= CoreInstaller.MaxOfflineZipBytes / chunk.Length; i++) { inst.AppendOffline(chunk, first); first = false; }
        });
        Assert.False(Directory.Exists(inst.OfflineDir));
    }

    /// <summary>Аргументы core_offline_part: только base64 до 1 МБ и флаг; пути и лишние поля — invalid.</summary>
    [Theory]
    [InlineData("""{"id":1,"cmd":"core_offline_part","args":{"data":"QUJD","first":true}}""", true)]
    [InlineData("""{"id":1,"cmd":"core_offline_part","args":{"data":"не base64"}}""", false)]
    [InlineData("""{"id":1,"cmd":"core_offline_part","args":{"data":"QUJD","path":"C:\\Windows\\System32\\config\\SAM"}}""", false)]
    [InlineData("""{"id":1,"cmd":"core_offline_part","args":{}}""", false)]
    [InlineData("""{"id":1,"cmd":"core_offline_install","args":{"path":"C:\\x.zip"}}""", false)]
    public void Offline_part_accepts_only_bytes(string json, bool ok)
    {
        var catalog = new FakeHelperBackend();
        if (ok) Assert.Equal("ABC"u8.ToArray(), HelperCommands.Parse(json, catalog).Bytes("data"));
        else Assert.Throws<HelperRequestException>(() => HelperCommands.Parse(json, catalog));
        var big = Convert.ToBase64String(new byte[HelperCommands.MaxOfflineChunkBytes + 1]);
        Assert.Throws<HelperRequestException>(() => HelperCommands.Parse("{\"id\":1,\"cmd\":\"core_offline_part\",\"args\":{\"data\":\"" + big + "\"}}", catalog));
    }

    /// <summary>Настоящий помощник над временной папкой: окно шлёт байты файла частями — ядро ставится тем же путём, без сети.</summary>
    [Fact]
    public async Task Offline_install_through_the_channel()
    {
        using var t = new TempDir();
        var (shell, net) = NewShell(t, serveZip: false, DateTime.UtcNow);
        CoreWait.Begin(shell.Paths, DateTime.UtcNow);
        var backend = new ShellHelperBackend(shell)
        {
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false), IsActive = () => false, FindForeign = () => [],
            DnsSystem = new FakeDnsSystem(), Defender = FakeDefender.Active(),
        };
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        Assert.True(c.Compat.Supports("core_offline_install"));
        var log = new List<string>();
        await using (var s = File.OpenRead(VendorZip)) await c.CoreOfflineAsync(s, new LineProgress(log.Add));
        Assert.Equal(CoreState.Ok, shell.Installer.Check().State);
        Assert.Equal(0, net.Requests);
        Assert.False(CoreWait.Exists(shell.Paths));   // окно поставило ядро — служба сама качать не станет
        Assert.Contains(log, l => l.Contains("sha256"));

        // Чужой файл — отказ с понятной причиной, ядро прежнее.
        var e = await Assert.ThrowsAsync<HelperException>(async () => { await using var s = new MemoryStream(new byte[5000]); await c.CoreOfflineAsync(s); });
        Assert.Contains("sha256", e.Message);
        Assert.Equal(CoreState.Ok, shell.Installer.Check().State);
    }
}
