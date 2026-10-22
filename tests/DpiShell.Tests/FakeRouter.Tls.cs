using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DpiShell.Core.Router;

namespace DpiShell.Tests;

/// <summary>
/// HTTPS у подделки роутера (как uhttpd с самоподписанным сертификатом): TLS снимается на своём порту, запрос уходит в тот же HttpListener
/// с пометкой X-Fake-Tls. HTTP при этом может перенаправлять на https:// (<see cref="RedirectToHttps"/>) или не отвечать (<see cref="HttpClosed"/>).
/// </summary>
public sealed partial class FakeRouter
{
    const string TlsMark = "X-Fake-Tls";
    TcpListener? _tls, _tls4;

    public int HttpPort { get; private set; }

    /// <summary>HTTP отвечает 301 на https://localhost:TlsPort/… (uhttpd redirect_https).</summary>
    public bool RedirectToHttps { get; set; }
    /// <summary>HTTP не отвечает (обрыв соединения), работает только HTTPS.</summary>
    public bool HttpClosed { get; set; }
    /// <summary>Схема каждого принятого запроса: http или https.</summary>
    public List<string> Schemes { get; } = [];

    X509Certificate2? _cert;
    /// <summary>Сертификат HTTPS; смена — как перевыпуск сертификата на роутере.</summary>
    public X509Certificate2 Certificate { get => _cert ??= NewCertificate(); set => _cert = value; }

    /// <summary>Отпечаток в виде окна (SHA-256 DER).</summary>
    public string CertificateFingerprint => RouterCert.Fingerprint(Certificate.RawData);

    /// <summary>Так отвечает роутер на <see cref="RouterSsh.WebCertCommand"/> (DER: «der sha256»).</summary>
    public string WebCertSshOutput => "der " + Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData)) + "\n";

    /// <summary>Порт HTTPS; первый запрос запускает приём.</summary>
    public int TlsPort
    {
        get
        {
            if (_tls is null)
            {
                // localhost — сначала ::1: без слушателя там Windows ждёт отказ ~2 с. Поэтому оба адреса, один порт.
                _tls = new TcpListener(IPAddress.IPv6Loopback, 0);
                _tls.Start();
                _tls4 = new TcpListener(IPAddress.Loopback, ((IPEndPoint)_tls.LocalEndpoint).Port);
                try { _tls4.Start(); _ = Task.Run(() => TlsLoopAsync(_tls4)); } catch (SocketException) { _tls4 = null; }
                _ = Task.Run(() => TlsLoopAsync(_tls));
            }
            return ((IPEndPoint)_tls.LocalEndpoint).Port;
        }
    }

    /// <summary>Как роутер с включённым HTTPS и перенаправлением с HTTP.</summary>
    public FakeRouter WithHttps(bool redirect = true)
    {
        _ = TlsPort;
        RedirectToHttps = redirect;
        return this;
    }

    public static X509Certificate2 NewCertificate(string cn = "OpenWrt")
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", key, HashAlgorithmName.SHA256);
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));
        // Ключ эфемерный — SChannel его не видит; через PFX — видит.
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx), null);
    }

    static bool ViaTls(HttpListenerContext ctx) => ctx.Request.Headers[TlsMark] == "1";

    bool PlainRefused(HttpListenerContext ctx)
    {
        if (!HttpClosed || ViaTls(ctx)) return false;
        lock (Schemes) Schemes.Add("http");
        try { ctx.Response.Abort(); } catch { }
        return true;
    }

    bool PlainRedirected(HttpListenerContext ctx)
    {
        var tls = ViaTls(ctx);
        lock (Schemes) Schemes.Add(tls ? "https" : "http");
        if (tls || !RedirectToHttps) return false;
        ctx.Response.StatusCode = 301;
        ctx.Response.RedirectLocation = $"https://localhost:{TlsPort}{ctx.Request.RawUrl}";
        return true;
    }

    async Task TlsLoopAsync(TcpListener l)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await l.AcceptTcpClientAsync(_cts.Token); } catch { return; }
            _ = Task.Run(() => ServeTlsAsync(c));
        }
    }

    /// <summary>Один запрос на соединение: снять TLS, переписать Host и пометить, отдать HttpListener, ответ — обратно.</summary>
    async Task ServeTlsAsync(TcpClient client)
    {
        using var _c = client;
        try
        {
            await using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = Certificate }, _cts.Token);
            var head = new MemoryStream();
            var buf = new byte[1];
            while (!EndsWithBlankLine(head))
            {
                if (await ssl.ReadAsync(buf, _cts.Token) == 0) return;
                head.WriteByte(buf[0]);
            }
            var lines = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n").Where(l => l.Length > 0).ToList();
            int length = 0;
            var sb = new StringBuilder(lines[0]).Append("\r\n");
            foreach (var l in lines.Skip(1))
            {
                var name = l.Split(':', 2)[0].Trim();
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase) || name.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) length = int.Parse(l.Split(':', 2)[1].Trim());
                sb.Append(l).Append("\r\n");
            }
            sb.Append($"Host: localhost:{HttpPort}\r\n{TlsMark}: 1\r\nConnection: close\r\n\r\n");
            var body = new byte[length];
            for (int read = 0; read < length;)
            {
                var n = await ssl.ReadAsync(body.AsMemory(read), _cts.Token);
                if (n == 0) return;
                read += n;
            }
            using var back = new TcpClient();
            await back.ConnectAsync(IPAddress.Loopback, HttpPort, _cts.Token);
            var bs = back.GetStream();
            await bs.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), _cts.Token);
            await bs.WriteAsync(body, _cts.Token);
            await bs.CopyToAsync(ssl, _cts.Token);
        }
        catch { }
    }

    static bool EndsWithBlankLine(MemoryStream m)
    {
        if (m.Length < 4) return false;
        var b = m.GetBuffer();
        var n = (int)m.Length;
        return b[n - 4] == '\r' && b[n - 3] == '\n' && b[n - 2] == '\r' && b[n - 1] == '\n';
    }

    void StopTls()
    {
        try { _tls?.Stop(); } catch { }
        try { _tls4?.Stop(); } catch { }
    }
}
