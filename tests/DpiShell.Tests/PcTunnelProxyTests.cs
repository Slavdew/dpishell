using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// Прокси туннеля — настоящий, на loopback; «сервер» витрины — локальный TLS с самоподписанным сертификатом.
/// Имена feed.test/evil.test в сеть не уходят: прокси подключается через подменённый connect.
/// </summary>
public sealed class PcTunnelProxyTests : IAsyncLifetime
{
    TlsTestServer _server = null!;
    PcTunnelProxy _proxy = null!;
    readonly List<string> _connects = [];

    public Task InitializeAsync()
    {
        _server = new TlsTestServer("feed.test");
        _proxy = new PcTunnelProxy(["feed.test"], ConnectAsync, (_, cert, _, _) => cert?.Subject == "CN=feed.test").Start();
        return Task.CompletedTask;
    }

    async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        lock (_connects) _connects.Add($"{host}:{port}");
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _server.Port, ct);
        return c.GetStream();
    }

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();
        _server.Dispose();
    }

    [Fact]
    public void Allowlist_BaseHostsAndFeeds_OnlyTlsPort443()
    {
        Assert.True(_proxy.IsAllowed("slavdew.github.io", 443, true));
        Assert.True(_proxy.IsAllowed("GitHub.com", 443, true));
        Assert.True(_proxy.IsAllowed("objects.githubusercontent.com", 443, true));
        Assert.True(_proxy.IsAllowed("downloads.openwrt.org", 80, false));
        Assert.True(_proxy.IsAllowed("feed.test", 443, true));
        Assert.False(_proxy.IsAllowed("github.com", 22, true));
        Assert.False(_proxy.IsAllowed("evil.github.com", 443, true));
        Assert.False(_proxy.IsAllowed("140.82.121.3", 443, true));
        Assert.True(_proxy.Port > 0);
    }

    [Fact]
    public async Task Connect_Tunnel_ForCurl()
    {
        using var http = new HttpClient(new HttpClientHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{_proxy.Port}"),
            UseProxy = true,
            ServerCertificateCustomValidationCallback = (_, c, _, _) => c?.Subject == "CN=feed.test",
        });
        var body = await http.GetStringAsync("https://feed.test/dpishell/feed/dpishell.pem");
        Assert.StartsWith("path=/dpishell/feed/dpishell.pem", body);
        Assert.Equal(["feed.test:443"], _connects);
    }

    [Fact]
    public async Task AbsoluteGet_ProxyDoesTls_KeepAlive()
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        var s = c.GetStream();
        var r = new RawHttp(s);

        await r.SendAsync("GET https://feed.test/a/packages.adb HTTP/1.1\r\nHost: feed.test\r\nUser-Agent: apk\r\n\r\n");
        var (code1, body1) = await r.ReadResponseAsync();
        await r.SendAsync("GET https://feed.test/b/dpishell-0.2.0-r3.apk HTTP/1.1\r\nHost: feed.test\r\n\r\n");
        var (code2, body2) = await r.ReadResponseAsync();
        // Ответ сервера без длины (chunked) прокси отдаёт тоже chunked — соединение не рвётся.
        await r.SendAsync("GET https://feed.test/chunked HTTP/1.1\r\nHost: feed.test\r\n\r\n");
        var (code3, body3) = await r.ReadResponseAsync();

        Assert.Equal((200, 200, 200), (code1, code2, code3));
        Assert.StartsWith("path=/a/packages.adb", body1);
        Assert.Contains("ua=apk", body1);
        Assert.StartsWith("path=/b/dpishell-0.2.0-r3.apk", body2);
        Assert.Equal("часть1часть2", body3);
        // Три запроса роутера — одно соединение к прокси и одно TLS-соединение к серверу.
        Assert.Equal(1, _server.Connections);
        Assert.Equal(3, _server.Requests);
    }

    [Fact]
    public async Task NotInAllowlist_Denied_Connect()
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        var r = new RawHttp(c.GetStream());
        await r.SendAsync("CONNECT evil.test:443 HTTP/1.1\r\nHost: evil.test:443\r\n\r\n");
        var (code, _) = await r.ReadResponseAsync();
        Assert.Equal(403, code);
        Assert.Empty(_connects);
        Assert.Contains("evil.test:443", _proxy.Denied);
    }

    [Fact]
    public async Task NotInAllowlist_Denied_AbsoluteGet_ConnectionStaysForNext()
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        var r = new RawHttp(c.GetStream());
        await r.SendAsync("GET https://evil.test/steal HTTP/1.1\r\nHost: evil.test\r\n\r\n");
        Assert.Equal(403, (await r.ReadResponseAsync()).Code);
        await r.SendAsync("GET https://feed.test/ok HTTP/1.1\r\nHost: feed.test\r\n\r\n");
        var (code, body) = await r.ReadResponseAsync();
        Assert.Equal(200, code);
        Assert.StartsWith("path=/ok", body);
        Assert.DoesNotContain(_connects, x => x.StartsWith("evil"));
    }

    [Theory]
    [InlineData("CONNECT feed.test:22 HTTP/1.1\r\n\r\n", 403)]                    // только 443
    [InlineData("GET http://feed.test:8080/ HTTP/1.1\r\nHost: x\r\n\r\n", 403)]   // http только на 80
    [InlineData("GET /local HTTP/1.1\r\nHost: feed.test\r\n\r\n", 400)]            // не прокси-запрос
    public async Task BadTargets_Rejected(string request, int expected)
    {
        using var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, _proxy.Port);
        var r = new RawHttp(c.GetStream());
        await r.SendAsync(request);
        Assert.Equal(expected, (await r.ReadResponseAsync()).Code);
        Assert.Empty(_connects);
    }
}

public class RouterTunnelTests
{
    [Fact]
    public async Task Open_ForwardsRandomRemotePortToProxy_DisposeCloses()
    {
        var ssh = new FakeSsh();
        var t = await RouterTunnel.OpenAsync(ssh, ["mirror.example.org"]);
        var (remote, local) = Assert.Single(ssh.Forwards);
        Assert.Equal(t.RemotePort, remote);
        Assert.Equal(t.Proxy.Port, local);
        Assert.InRange(remote, 20000, 59999);
        Assert.True(t.Proxy.IsAllowed("mirror.example.org", 443, true));
        Assert.Equal($"https_proxy=http://127.0.0.1:{remote} http_proxy=http://127.0.0.1:{remote} ", RouterSsh.ProxyEnv(remote));
        await t.DisposeAsync();
        Assert.Equal(1, ssh.ForwardsClosed);
    }
}

/// <summary>Мини-клиент HTTP/1.1 поверх сокета: Content-Length и chunked.</summary>
sealed class RawHttp(Stream s)
{
    readonly List<byte> _buf = [];

    public Task SendAsync(string text) => s.WriteAsync(Encoding.ASCII.GetBytes(text)).AsTask();

    async Task<string> LineAsync()
    {
        while (true)
        {
            for (int i = 0; i + 1 < _buf.Count; i++)
                if (_buf[i] == '\r' && _buf[i + 1] == '\n')
                {
                    var line = Encoding.UTF8.GetString(_buf.GetRange(0, i).ToArray());
                    _buf.RemoveRange(0, i + 2);
                    return line;
                }
            await MoreAsync();
        }
    }

    async Task MoreAsync()
    {
        var b = new byte[4096];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var n = await s.ReadAsync(b, cts.Token);
        if (n == 0) throw new IOException("закрыто");
        _buf.AddRange(b.AsSpan(0, n).ToArray());
    }

    async Task<byte[]> BytesAsync(int n)
    {
        while (_buf.Count < n) await MoreAsync();
        var r = _buf.GetRange(0, n).ToArray();
        _buf.RemoveRange(0, n);
        return r;
    }

    public async Task<(int Code, string Body)> ReadResponseAsync()
    {
        var status = await LineAsync();
        var code = int.Parse(status.Split(' ')[1]);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var l = await LineAsync(); l.Length > 0; l = await LineAsync())
            headers[l[..l.IndexOf(':')]] = l[(l.IndexOf(':') + 1)..].Trim();
        var body = new List<byte>();
        if (headers.TryGetValue("Transfer-Encoding", out var te) && te == "chunked")
        {
            while (true)
            {
                var n = Convert.ToInt32(await LineAsync(), 16);
                if (n == 0) { await LineAsync(); break; }
                body.AddRange(await BytesAsync(n));
                await LineAsync();
            }
        }
        else if (headers.TryGetValue("Content-Length", out var cl)) body.AddRange(await BytesAsync(int.Parse(cl)));
        return (code, Encoding.UTF8.GetString(body.ToArray()));
    }
}

/// <summary>HTTPS-сервер на loopback с самоподписанным сертификатом: keep-alive, ответ — путь и User-Agent; /chunked — по частям.</summary>
sealed class TlsTestServer : IDisposable
{
    readonly TcpListener _l = new(IPAddress.Loopback, 0);
    readonly X509Certificate2 _cert;
    readonly CancellationTokenSource _cts = new();
    int _connections, _requests;

    public int Port { get; }
    public int Connections => _connections;
    public int Requests => _requests;

    public TlsTestServer(string name)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(name);
        req.CertificateExtensions.Add(san.Build());
        using var self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
        _l.Start();
        Port = ((IPEndPoint)_l.LocalEndpoint).Port;
        _ = Task.Run(AcceptAsync);
    }

    async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            Interlocked.Increment(ref _connections);
            _ = Task.Run(async () =>
            {
                using (c)
                    try { await ServeAsync(c); } catch { }
            });
        }
    }

    async Task ServeAsync(TcpClient c)
    {
        using var ssl = new SslStream(c.GetStream());
        await ssl.AuthenticateAsServerAsync(_cert);
        var raw = new RawHttpServerReader(ssl);
        while (await raw.ReadRequestAsync() is { } req)
        {
            Interlocked.Increment(ref _requests);
            var (path, ua) = req;
            if (path == "/chunked")
            {
                var parts = new[] { "часть1", "часть2" }.Select(Encoding.UTF8.GetBytes).ToArray();
                var sb = new StringBuilder("HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n");
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()));
                foreach (var p in parts)
                {
                    await ssl.WriteAsync(Encoding.ASCII.GetBytes($"{p.Length:x}\r\n"));
                    await ssl.WriteAsync(p);
                    await ssl.WriteAsync("\r\n"u8.ToArray());
                }
                await ssl.WriteAsync("0\r\n\r\n"u8.ToArray());
                continue;
            }
            var body = Encoding.UTF8.GetBytes($"path={path};ua={ua}");
            await ssl.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/plain\r\nContent-Length: {body.Length}\r\n\r\n"));
            await ssl.WriteAsync(body);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _l.Stop();
        _cert.Dispose();
    }
}

sealed class RawHttpServerReader(Stream s)
{
    readonly StringBuilder _sb = new();

    public async Task<(string Path, string? Ua)?> ReadRequestAsync()
    {
        var b = new byte[1];
        while (true)
        {
            var text = _sb.ToString();
            var i = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (i >= 0)
            {
                _sb.Remove(0, i + 4);
                var lines = text[..i].Split("\r\n");
                var ua = lines.FirstOrDefault(l => l.StartsWith("User-Agent:", StringComparison.OrdinalIgnoreCase))?[11..].Trim();
                return (lines[0].Split(' ')[1], ua);
            }
            if (await s.ReadAsync(b) == 0) return null;
            _sb.Append((char)b[0]);
        }
    }
}
