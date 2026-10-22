using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Вход на роутер по HTTPS: перенаправление с HTTP или закрытый HTTP — дальше по HTTPS; самоподписанный сертификат закрепляется при первом входе
/// (router-hostkeys.json, как ключ SSH), смена — сверка по SSH (/etc/uhttpd.crt) или вопрос пользователю. Старый роутер (только HTTP) — как раньше.
/// </summary>
public sealed class RouterHttpsTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly List<string> _said = [];
    public void Dispose() => _tmp.Dispose();

    sealed class MemoryPins : IRouterHostKeyStore
    {
        public Dictionary<string, string> D { get; } = [];
        public string? Get(string id) => D.GetValueOrDefault(id);
        public void Set(string id, string fp) => D[id] = fp;
        public void Remove(string id) => D.Remove(id);
    }

    // ---------- клиент ----------

    [Fact]
    public async Task Redirect_GoesHttps_PinsAtFirstLogin()
    {
        using var r = new FakeRouter().WithHttps();
        var pins = new MemoryPins();
        using var c = new RouterClient(r.Address, pins: pins);
        Assert.Equal(RouterFound.Ok, (await c.DetectAsync()).Found);
        Assert.True(c.Https);
        Assert.Empty(pins.D);   // до входа — не закрепляем
        await c.LoginAsync(() => r.Password);
        Assert.Equal(r.CertificateFingerprint, pins.Get(RouterCert.PinId("localhost", r.TlsPort)));
        Assert.Equal("alt10", (await c.StatusAsync()).Strategy);
        // Пароль по HTTP не ходил: по HTTP — только первый status.json (ответ — перенаправление).
        lock (r.Schemes) Assert.Equal(["http", "https", "https", "https"], r.Schemes);
    }

    [Fact]
    public async Task HttpClosed_HttpsAnswers_GoesHttps()
    {
        using var r = new FakeRouter().WithHttps(redirect: false);
        r.HttpClosed = true;
        using var c = new RouterClient(r.Address, pins: new MemoryPins(), httpsPort: r.TlsPort);
        Assert.Equal(RouterFound.Ok, (await c.DetectAsync()).Found);
        Assert.True(c.Https);
        await c.LoginAsync(() => r.Password);
        Assert.NotNull(c.CertificateFingerprint);
    }

    [Fact]
    public async Task NoPinStore_SelfSignedNotAccepted()
    {
        // Без хранилища проверка не отключается: самоподписанному не верим — роутер «не найден», как раньше со старым окном.
        using var r = new FakeRouter().WithHttps();
        using var c = new RouterClient(r.Address);
        Assert.Equal(RouterFound.NotFound, (await c.DetectAsync()).Found);
        Assert.False(c.Https);
    }

    [Fact]
    public async Task OldRouter_HttpOnly_AsBefore()
    {
        using var r = new FakeRouter();
        var pins = new MemoryPins();
        using var c = new RouterClient(r.Address, pins: pins);
        Assert.Equal(RouterFound.Ok, (await c.DetectAsync()).Found);
        await c.LoginAsync(() => r.Password);
        Assert.False(c.Https);
        Assert.Null(c.CertificateFingerprint);
        Assert.Empty(pins.D);
        lock (r.Schemes) Assert.All(r.Schemes, s => Assert.Equal("http", s));
    }

    [Fact]
    public async Task CertificateChanged_Refused_WithNewFingerprint()
    {
        using var r = new FakeRouter().WithHttps();
        var pins = new MemoryPins();
        using (var c = new RouterClient(r.Address, pins: pins)) { await c.DetectAsync(); await c.LoginAsync(() => r.Password); }
        var old = r.CertificateFingerprint;
        r.Certificate = FakeRouter.NewCertificate();
        using var again = new RouterClient(r.Address, pins: pins);
        var e = await Assert.ThrowsAsync<RouterException>(() => again.DetectAsync());
        Assert.Equal(RouterFailure.CertificateChanged, e.Failure);
        Assert.Equal(r.CertificateFingerprint, e.Certificate!.Fingerprint);
        Assert.Equal(RouterCert.PinId("localhost", r.TlsPort), e.Certificate.PinId);
        Assert.Equal(old, pins.Get(e.Certificate.PinId));   // молча не меняется
        Assert.Equal(1, r.Logins);   // пароль подменённому не ушёл
    }

    [Fact]
    public void WebCertificate_DerAndPem()
    {
        using var cert = FakeRouter.NewCertificate();
        var fp = RouterCert.Fingerprint(cert.RawData);
        Assert.Equal(fp, RouterSsh.ParseWebCertificate("der " + Convert.ToHexStringLower(SHA256.HashData(cert.RawData)) + "\n"));
        Assert.Equal(fp, RouterSsh.ParseWebCertificate("pem\n" + cert.ExportCertificatePem() + "\n"));
        Assert.Null(RouterSsh.ParseWebCertificate(""));
        Assert.Null(RouterSsh.ParseWebCertificate("der nothex"));
    }

    // ---------- окно ----------

    WindowSettings Settings = new();

    RouterViewModel Make(FakeRouter r)
    {
        var pc = new Shell(new AppPaths(_tmp.Sub("root"), Repo.Data));
        Settings = WindowSettings.Load(Path.Combine(_tmp.Sub("window"), "window.json"));
        var vm = new RouterViewModel(pc, Settings)
        {
            AddressAuto = false,
            CustomAddress = r.Address,
            PollInterval = TimeSpan.FromMilliseconds(10),
            PcTimestamps = () => true,
            Gateway = () => throw new InvalidOperationException("в тестах — только фейковый роутер"),
        };
        vm.Say = s => { lock (_said) _said.Add(s); };
        vm.TrustNewCert = _ => throw new InvalidOperationException("вопрос не ожидался");
        return vm;
    }

    string Pin(FakeRouter r) => Settings.HostKeys.Get(RouterCert.PinId("localhost", r.TlsPort))!;

    [Fact]
    public async Task Window_HttpsRouter_ConnectsAndPinsInHostKeysFile()
    {
        using var r = new FakeRouter().WithHttps();
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Assert.True(vm.Connected);
        Assert.Equal(r.CertificateFingerprint, Pin(r));
        Assert.Contains(r.CertificateFingerprint, File.ReadAllText(Path.Combine(Path.GetDirectoryName(Settings.File)!, "router-hostkeys.json")));
    }

    [Fact]
    public async Task Window_CertChanged_SshConfirms_AcceptedSilently()
    {
        using var r = new FakeRouter().WithHttps();
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        Settings.HostKeys.Set(new RouterSshTarget(r.Address).HostKeyId, "ssh-ed25519 SHA256:known");   // по SSH уже входили
        r.Certificate = FakeRouter.NewCertificate();
        var ssh = new FakeSsh { WebCertOutput = r.WebCertSshOutput };
        vm.SshConnector = new FakeSshConnector(ssh);

        // Фоновая проверка: без вопроса, по SSH сертификат совпал — принят.
        await vm.RefreshAsync();
        Assert.True(vm.Connected);
        Assert.Equal(r.CertificateFingerprint, Pin(r));
        Assert.Contains(ssh.Runs, x => x.Command == RouterSsh.WebCertCommand);
        lock (_said) Assert.Contains(L.T("router.cert.accepted_ssh"), _said);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Window_CertChanged_SshDiffers_AsksUser(bool trust)
    {
        using var r = new FakeRouter().WithHttps();
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        var old = Pin(r);
        Settings.HostKeys.Set(new RouterSshTarget(r.Address).HostKeyId, "ssh-ed25519 SHA256:known");
        var ssh = new FakeSsh { WebCertOutput = r.WebCertSshOutput };   // на роутере прежний — а отвечает другой
        vm.SshConnector = new FakeSshConnector(ssh);
        r.Certificate = FakeRouter.NewCertificate();

        // Фоновая проверка не спрашивает: только «сертификат изменился».
        await vm.RefreshAsync();
        Assert.False(vm.Connected);
        Assert.Equal(L.T("router.cert.changed"), vm.ConnectionText);
        Assert.Equal(old, Pin(r));

        var asked = new List<string>();
        vm.TrustNewCert = t => { asked.Add(t); return trust; };
        Assert.Equal(trust, await vm.ConnectAsync(null));
        Assert.Equal([RouterViewModel.CertText], asked);
        if (trust)
        {
            Assert.True(vm.Connected);
            Assert.Equal(r.CertificateFingerprint, Pin(r));
        }
        else
        {
            Assert.False(vm.Connected);
            Assert.Equal(old, Pin(r));
            Assert.Equal(L.T("router.cert.cancelled"), vm.ConnectionText);
        }
    }

    [Fact]
    public async Task Window_CertChanged_SshKeyNotPinned_NoSshAttempt_Asks()
    {
        // Ключ SSH ещё не закреплён — пароль по SSH неизвестно кому не шлём, сразу вопрос.
        using var r = new FakeRouter().WithHttps();
        var vm = Make(r);
        Assert.True(await vm.ConnectAsync("secret"));
        r.Certificate = FakeRouter.NewCertificate();
        var ssh = new FakeSsh { WebCertOutput = r.WebCertSshOutput };
        var conn = new FakeSshConnector(ssh);
        vm.SshConnector = conn;
        vm.TrustNewCert = _ => true;
        Assert.True(await vm.ConnectAsync(null));
        Assert.Null(conn.LastPassword);
        Assert.Equal(r.CertificateFingerprint, Pin(r));
    }
}
