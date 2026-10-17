using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// DNS этого компьютера (architecture.md «DNS этого компьютера»): какие адаптеры меняем, сохранение и «Вернуть как было», откат, если DNS
/// не ответил, и строгая проверка команд помощника. Сеть и реестр — <see cref="FakeDnsSystem"/>.
/// </summary>
public sealed class DnsSettingsTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    DnsManager Make(FakeDnsSystem sys) => new(sys, Path.Combine(_tmp.Sub("root"), "dns-backup.json")) { VerifyDelay = TimeSpan.Zero };

    static readonly FakeHelperBackend Catalog = new();
    static HelperCall Parse(string line) => HelperCommands.Parse(line, Catalog);
    static void Rejected(string line) => Assert.Throws<HelperRequestException>(() => Parse(line));

    // ---------- выбор адаптеров ----------

    [Fact]
    public void Pick_OnlyPhysicalWithGateway_VpnAndVirtualSkipped()
    {
        var (picked, skipped) = DnsManager.Pick(new FakeDnsSystem().List);
        Assert.Equal(["Ethernet", "Wi-Fi"], picked.Select(a => a.Name));
        Assert.Equal(["Radmin VPN", "vEthernet (Default Switch)", "wg0"], skipped.Select(a => a.Name));
    }

    [Fact]
    public async Task Set_TouchesOnlyPicked_VpnUntouched()
    {
        var sys = new FakeDnsSystem();
        var r = await Make(sys).SetAsync("google", null, null, false, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(["write Ethernet", "write Wi-Fi", "flush"], sys.Log);
        Assert.False(sys.Dns.ContainsKey(FakeDnsSystem.Radmin.Id));
        Assert.False(sys.Dns.ContainsKey(FakeDnsSystem.HyperV.Id));
        Assert.False(sys.Dns.ContainsKey(FakeDnsSystem.WireGuard.Id));
        var eth = sys.Dns[FakeDnsSystem.Ethernet.Id];
        Assert.Equal(["8.8.8.8", "8.8.4.4"], eth.Ipv4);
        Assert.Equal(["2001:4860:4860::8888", "2001:4860:4860::8844"], eth.Ipv6);
        Assert.Equal(DnsManager.DohOn, eth.Doh["8.8.8.8"].Flags);
        Assert.Equal("https://dns.google/dns-query", eth.Doh["8.8.8.8"].Template);
        // У Wi-Fi нет IPv6 — адреса IPv6 не пишутся.
        Assert.Empty(sys.Dns[FakeDnsSystem.Wifi.Id].Ipv6);
        // Встроенный шаблон Google не регистрируется заново.
        Assert.Empty(sys.Registered);
        Assert.Equal("google", r.Status.Current);
        Assert.True(r.Status.Encrypted);
        Assert.True(r.Status.CanRestore);
        Assert.Equal(["Radmin VPN", "vEthernet (Default Switch)", "wg0"], r.Status.Skipped);
    }

    [Fact]
    public async Task NoPhysicalAdapter_Throws()
    {
        var sys = new FakeDnsSystem();
        sys.List.RemoveAll(a => a.Name is "Ethernet" or "Wi-Fi");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Make(sys).SetAsync("google", null, null, false, CancellationToken.None));
        Assert.Empty(sys.Dns);
    }

    // ---------- сохранение и «Вернуть как было» ----------

    [Fact]
    public async Task Restore_ReturnsOriginal_EvenAfterTwoSwitches()
    {
        var sys = new FakeDnsSystem();
        var original = new AdapterDns(false, ["192.168.1.1"], true, [], new Dictionary<string, DohSetting>());
        sys.Dns[FakeDnsSystem.Ethernet.Id] = original;
        var m = Make(sys);
        await m.SetAsync("cloudflare", null, null, false, CancellationToken.None);
        await m.SetAsync("yandex", null, null, false, CancellationToken.None);
        // Яндекса нет среди встроенных — шаблон зарегистрирован для каждого адреса.
        Assert.Equal("https://common.dot.dns.yandex.net/dns-query", sys.Registered["77.88.8.8"].Template);
        Assert.Equal("yandex", m.Status().Current);

        var r = await m.RestoreAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(original.Ipv4, sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4);   // прежний статический, а не Cloudflare
        Assert.True(sys.Dns[FakeDnsSystem.Wifi.Id].Ipv4Auto);                    // Wi-Fi был от DHCP — от DHCP
        Assert.Empty(sys.Registered);                                            // свои регистрации DoH убраны
        Assert.False(r.Status.CanRestore);
        Assert.Contains("unregister 77.88.8.8", sys.Log);
    }

    [Fact]
    public async Task Restore_WithoutBackup_NothingToDo()
    {
        var sys = new FakeDnsSystem();
        var r = await Make(sys).RestoreAsync(CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Empty(sys.Log);
    }

    [Fact]
    public async Task Backup_SurvivesNewManager()
    {
        var sys = new FakeDnsSystem();
        await Make(sys).SetAsync("quad9", null, null, false, CancellationToken.None);
        var again = Make(sys);   // помощник перезапустился
        Assert.True(again.Status().CanRestore);
        await again.RestoreAsync(CancellationToken.None);
        Assert.True(sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4Auto);
    }

    // ---------- откат и Windows 10 ----------

    [Fact]
    public async Task NotResolving_RollsBackToBefore()
    {
        var sys = new FakeDnsSystem { Resolves = () => false };
        var m = Make(sys);
        var r = await m.SetAsync(DnsPresets.Custom, ["10.0.0.53"], "https://dns.example.net/dns-query", false, CancellationToken.None);
        Assert.False(r.Ok);
        Assert.Contains("не ответил", r.Message);
        Assert.Contains("разрешите обычный DNS", r.Message);
        Assert.True(sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4Auto);   // вернули как было до переключения
        Assert.Empty(sys.Registered);                                // регистрацию этого раза тоже
        Assert.Equal("auto", r.Status.Current);
    }

    [Fact]
    public async Task Fallback_SetsBit_AndRegistersWithFallback()
    {
        var sys = new FakeDnsSystem();
        var r = await Make(sys).SetAsync(DnsPresets.Custom, ["10.0.0.53"], "https://dns.example.net/dns-query", true, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Equal(DnsManager.DohOn | DnsManager.DohFallbackBit, sys.Dns[FakeDnsSystem.Ethernet.Id].Doh["10.0.0.53"].Flags);
        Assert.True(sys.Registered["10.0.0.53"].Fallback);
        Assert.Equal("custom", r.Status.Current);
        Assert.True(r.Status.Fallback);
    }

    [Fact]
    public async Task Windows10_PlainDnsOnly()
    {
        var sys = new FakeDnsSystem { DohSupported = false };
        var r = await Make(sys).SetAsync("google", null, null, false, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.Contains("без шифрования", r.Message);
        Assert.Empty(sys.Dns[FakeDnsSystem.Ethernet.Id].Doh);
        Assert.False(r.Status.Encrypted);
        Assert.Equal("google", r.Status.Current);
    }

    [Fact]
    public async Task Auto_ResetsToDhcp()
    {
        var sys = new FakeDnsSystem();
        var m = Make(sys);
        await m.SetAsync("google", null, null, false, CancellationToken.None);
        var r = await m.SetAsync("auto", null, null, false, CancellationToken.None);
        Assert.True(r.Ok);
        Assert.True(sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4Auto);
        Assert.Empty(sys.Dns[FakeDnsSystem.Ethernet.Id].Doh);
        Assert.Equal("auto", r.Status.Current);
        Assert.True(r.Status.CanRestore);   // «как было» — исходное, а не DHCP
    }

    // ---------- проверка значений ----------

    [Theory]
    [InlineData("8.8.8.8", "8.8.8.8")]
    [InlineData(" 1.1.1.1 ", "1.1.1.1")]
    [InlineData("2a02:6b8::feed:0ff", "2a02:6b8::feed:ff")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("1.1.1", null)]
    [InlineData("1", null)]
    [InlineData("010.1.1.1", null)]
    [InlineData("256.1.1.1", null)]
    [InlineData("0.0.0.0", null)]
    [InlineData("255.255.255.255", null)]
    [InlineData("fe80::1%12", null)]
    [InlineData("::ffff:1.2.3.4", null)]
    [InlineData("dns.google", null)]
    [InlineData("8.8.8.8; calc", null)]
    public void NormalizeIp(string input, string? expected) => Assert.Equal(expected, DnsValues.NormalizeIp(input));

    [Theory]
    [InlineData("https://dns.google/dns-query", true)]
    [InlineData("https://dns.example.net:8443/q", true)]
    [InlineData("http://dns.google/dns-query", false)]
    [InlineData("https://dns.google/dns-query?dns=x", false)]
    [InlineData("https://user@dns.google/dns-query", false)]
    [InlineData("https://dns.google/dns query", false)]
    [InlineData("https://dns.google/\"a", false)]
    [InlineData("https://dns.google\\x", false)]
    [InlineData("file:///c:/x", false)]
    public void DohTemplate(string input, bool ok) => Assert.Equal(ok, DnsValues.IsDohTemplate(input));

    // ---------- команды помощника ----------

    [Fact]
    public void Commands_Accepted()
    {
        Assert.Equal("google", Parse("""{"id":1,"cmd":"dns_set","args":{"preset":"google"}}""").Str("preset"));
        var c = Parse("""{"id":2,"cmd":"dns_set","args":{"preset":"custom","ips":["10.0.0.53","2001:db8::53"],"doh":"https://dns.example.net/dns-query","fallback":true}}""");
        Assert.Equal(["10.0.0.53", "2001:db8::53"], c.List("ips"));
        Assert.True(c.Bool("fallback"));
        Parse("""{"id":3,"cmd":"dns_restore"}""");
        Assert.False(Parse("""{"id":4,"cmd":"dns_status"}""").Spec.Exclusive);
    }

    [Theory]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{}}""")]                                                     // нет preset
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"opendns"}}""")]                                   // не из списка
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom"}}""")]                                    // свой без адресов
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"google","ips":["1.1.1.1"]}}""")]                  // адреса — только у своего
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"google","doh":"https://x.example/q"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":[]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1","1.0.0.1","8.8.8.8","8.8.4.4","9.9.9.9"]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1","1.1.1.1"]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["dns.google"]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1 & calc"]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":"1.1.1.1"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":[1]}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1"],"doh":"http://x.example/q"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1"],"doh":"https://x.example/q\" & calc"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1"],"doh":"https://x.example/a b"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"google","adapter":"Radmin VPN"}}""")]           // лишнее поле
    [InlineData("""{"id":1,"cmd":"dns_set","args":{"preset":"google","fallback":"yes"}}""")]
    [InlineData("""{"id":1,"cmd":"dns_restore","args":{"all":true}}""")]
    [InlineData("""{"id":1,"cmd":"dns_status","args":{"adapter":"x"}}""")]
    public void Commands_Rejected(string line) => Rejected(line);

    [Fact]
    public void LongTemplate_Rejected()
    {
        var url = "https://x.example/" + new string('a', 200);
        Rejected("""{"id":1,"cmd":"dns_set","args":{"preset":"custom","ips":["1.1.1.1"],"doh":"URL"}}""".Replace("URL", url));
    }

    // ---------- через канал и настоящий помощник над временной папкой ----------

    [Fact]
    public async Task Channel_RealBackend_SetAndRestore()
    {
        var shell = new Shell(new AppPaths(_tmp.Sub("pc"), Repo.Data));
        var sys = new FakeDnsSystem();
        var backend = new ShellHelperBackend(shell)
        {
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false),
            IsActive = () => false, FindForeign = () => [], DnsSystem = sys,
        };
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        var r = await c.DnsSetAsync("cloudflare");
        Assert.True(r.Ok);
        Assert.Equal("cloudflare", r.Status.Current);
        Assert.True(File.Exists(shell.Paths.DnsBackup));
        Assert.Equal(["1.1.1.1", "1.0.0.1"], sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4);
        Assert.Equal("cloudflare", (await c.DnsStatusAsync()).Current);
        var back = await c.DnsRestoreAsync();
        Assert.True(back.Ok);
        Assert.False(File.Exists(shell.Paths.DnsBackup));
        Assert.True(sys.Dns[FakeDnsSystem.Ethernet.Id].Ipv4Auto);
    }
}
