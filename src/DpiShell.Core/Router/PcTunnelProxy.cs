using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;

namespace DpiShell.Core.Router;

/// <summary>
/// Интернет роутеру через ПК на время сценария (router-api.md §5.5): HTTP-прокси на 127.0.0.1:случайный порт, к нему — <c>ssh -R</c>.
/// Умеет оба вида запросов: <c>CONNECT host:443</c> (curl, wget) и абсолютный <c>GET https://host/…</c> (apk) — для второго TLS к серверу
/// делает сам; соединение с роутером держит открытым для следующих запросов (keep-alive).
/// Пускает только хосты из белого списка (витрина, фиды прошивки, GitHub) и только на 443/80: роутер не может ходить через ПК куда угодно.
/// Подлинность не страдает: apk проверяет подписи и хеши сам, ядро сверяется по sha256.
/// </summary>
public sealed class PcTunnelProxy : IAsyncDisposable
{
    /// <summary>Всегда разрешены: витрина DpiShell, OpenWrt, релизы GitHub (github.com отдаёт файлы через *.githubusercontent.com).</summary>
    public static readonly IReadOnlyList<string> BaseHosts =
        ["slavdew.github.io", "downloads.openwrt.org", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    /// <summary>Самый большой принимаемый заголовок запроса и тело (apk и curl тела не шлют).</summary>
    const int MaxHead = 32 * 1024, MaxBody = 1024 * 1024;

    readonly HashSet<string> _allowed;
    readonly Func<string, int, CancellationToken, ValueTask<Stream>> _connect;
    readonly HttpClient _http;
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _cts = new();
    readonly List<TcpClient> _clients = [];
    Task? _accept;

    /// <param name="extraHosts">Хосты фидов роутера (<see cref="RouterSsh.FeedHostsAsync"/>).</param>
    /// <param name="connect">Как открыть TCP к серверу (тесты подменяют); по умолчанию — обычное подключение.</param>
    /// <param name="serverCertificateCheck">Только тестам: проверка сертификата сервера для абсолютных https-запросов.</param>
    public PcTunnelProxy(IEnumerable<string>? extraHosts = null,
        Func<string, int, CancellationToken, ValueTask<Stream>>? connect = null,
        RemoteCertificateValidationCallback? serverCertificateCheck = null)
    {
        _allowed = new HashSet<string>(BaseHosts.Concat(extraHosts ?? []).Select(h => h.Trim().TrimEnd('.').ToLowerInvariant()).Where(h => h.Length > 0),
            StringComparer.Ordinal);
        _connect = connect ?? DefaultConnectAsync;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,               // перенаправления — роутеру, он сам придёт за новым адресом (и тот проверится)
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(60),
            ConnectCallback = async (ctx, ct) => await _connect(ctx.DnsEndPoint.Host, ctx.DnsEndPoint.Port, ct),
        };
        if (serverCertificateCheck is not null) handler.SslOptions.RemoteCertificateValidationCallback = serverCertificateCheck;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
    }

    public int Port { get; private set; }
    public IReadOnlyCollection<string> AllowedHosts => _allowed;
    /// <summary>Отказы (хост:порт) — для журнала сценария.</summary>
    public List<string> Denied { get; } = [];
    public Action<string>? Log { get; init; }

    public PcTunnelProxy Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _accept = AcceptLoopAsync();
        return this;
    }

    public bool IsAllowed(string host, int port, bool tls) =>
        _allowed.Contains(host.Trim().TrimEnd('.').ToLowerInvariant()) && port == (tls ? 443 : 80);

    static async ValueTask<Stream> DefaultConnectAsync(string host, int port, CancellationToken ct)
    {
        var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try { await s.ConnectAsync(host, port, ct); }
        catch { s.Dispose(); throw; }
        return new NetworkStream(s, ownsSocket: true);
    }

    async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { return; }
            lock (_clients) _clients.Add(c);
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(c, _cts.Token); }
                catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
                finally { lock (_clients) _clients.Remove(c); c.Dispose(); }
            });
        }
    }

    void Deny(string what)
    {
        lock (Denied) Denied.Add(what);
        Log?.Invoke($"Туннель: отказ {what} — не из белого списка");
    }

    async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        client.NoDelay = true;
        var conn = new BufferedConn(client.GetStream());
        while (true)
        {
            var head = await conn.ReadHeadAsync(MaxHead, ct);
            if (head is null) return;
            var lines = head.Split("\r\n");
            var parts = lines[0].Split(' ');
            if (parts.Length != 3 || !parts[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) { await conn.WriteAsync(Status(400, "Bad Request", true), ct); return; }
            var (method, target, version) = (parts[0], parts[1], parts[2]);
            var headers = ParseHeaders(lines.Skip(1));

            if (method == "CONNECT")
            {
                await ConnectAsync(conn, target, ct);
                return;   // после туннеля соединение — поток TLS роутера, не HTTP
            }

            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                await conn.WriteAsync(Status(400, "Bad Request", true), ct);
                return;
            }
            bool close = version == "HTTP/1.0" ? !HasToken(headers, "Connection", "keep-alive") && !HasToken(headers, "Proxy-Connection", "keep-alive")
                                               : HasToken(headers, "Connection", "close") || HasToken(headers, "Proxy-Connection", "close");

            byte[]? body = null;
            if (headers.Any(h => h.Name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
            {
                await conn.WriteAsync(Status(501, "Not Implemented", true), ct);
                return;
            }
            if (Header(headers, "Content-Length") is { } cl)
            {
                if (!int.TryParse(cl, out var n) || n < 0 || n > MaxBody) { await conn.WriteAsync(Status(413, "Payload Too Large", true), ct); return; }
                body = await conn.ReadExactAsync(n, ct);
            }

            if (!IsAllowed(uri.IdnHost, uri.Port, uri.Scheme == "https"))
            {
                Deny($"{uri.IdnHost}:{uri.Port}");
                await conn.WriteAsync(Status(403, "Forbidden", close), ct);
                if (close) return;
                continue;
            }

            if (!await ForwardAsync(conn, method, uri, headers, body, close, ct) || close) return;
        }
    }

    async Task ConnectAsync(BufferedConn conn, string target, CancellationToken ct)
    {
        var i = target.LastIndexOf(':');
        if (i <= 0 || !int.TryParse(target[(i + 1)..], out var port)) { await conn.WriteAsync(Status(400, "Bad Request", true), ct); return; }
        var host = target[..i].Trim('[', ']');
        if (!IsAllowed(host, port, tls: true))
        {
            Deny($"{host}:{port}");
            await conn.WriteAsync(Status(403, "Forbidden", true), ct);
            return;
        }
        Stream up;
        try { up = await _connect(host, port, ct); }
        catch (Exception e) when (e is SocketException or IOException)
        {
            await conn.WriteAsync(Status(502, "Bad Gateway", true), ct);
            return;
        }
        await using (up)
        {
            await conn.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection established\r\n\r\n"), ct);
            using var done = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = conn.CopyToAsync(up, done.Token);
            var b = up.CopyToAsync(conn.Inner, done.Token);
            await Task.WhenAny(a, b);
            done.Cancel();
            try { await Task.WhenAll(a, b); } catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException) { }
        }
    }

    static readonly HashSet<string> HopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authorization", "Proxy-Authenticate", "TE", "Trailer", "Transfer-Encoding", "Upgrade", "Content-Length",
    };

    /// <summary>Запрос в абсолютной форме → серверу (TLS делает HttpClient), ответ → роутеру. false — соединение с роутером закрыть.</summary>
    async Task<bool> ForwardAsync(BufferedConn conn, string method, Uri uri, List<(string Name, string Value)> headers, byte[]? body, bool close, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        if (body is not null) req.Content = new ByteArrayContent(body);
        foreach (var (name, value) in headers)
        {
            if (HopHeaders.Contains(name)) continue;
            if (!req.Headers.TryAddWithoutValidation(name, value)) req.Content?.Headers.TryAddWithoutValidation(name, value);
        }

        HttpResponseMessage resp;
        try { resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e)
        {
            Log?.Invoke($"Туннель: {uri.IdnHost} — {e.Message}");
            await conn.WriteAsync(Status(502, "Bad Gateway", close), ct);
            return !close;
        }
        using (resp)
        {
            bool noBody = method == "HEAD" || (int)resp.StatusCode is 204 or 304 or (>= 100 and < 200);
            var length = resp.Content.Headers.ContentLength;
            var sb = new StringBuilder($"HTTP/1.1 {(int)resp.StatusCode} {resp.ReasonPhrase}\r\n");
            void Add(HttpHeaders hs)
            {
                foreach (var h in hs)
                    if (!HopHeaders.Contains(h.Key))
                        foreach (var v in h.Value) sb.Append(h.Key).Append(": ").Append(v).Append("\r\n");
            }
            Add(resp.Headers);
            Add(resp.Content.Headers);
            bool chunked = !noBody && length is null;
            if (length is { } n && (!noBody || method == "HEAD")) sb.Append("Content-Length: ").Append(n).Append("\r\n");
            if (chunked) sb.Append("Transfer-Encoding: chunked\r\n");
            sb.Append(close ? "Connection: close\r\n" : "Connection: keep-alive\r\n").Append("\r\n");
            await conn.WriteAsync(Encoding.Latin1.GetBytes(sb.ToString()), ct);
            if (noBody) return true;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            if (!chunked) { await src.CopyToAsync(conn.Inner, ct); return true; }
            var buf = new byte[64 * 1024];
            int r;
            while ((r = await src.ReadAsync(buf, ct)) > 0)
            {
                await conn.WriteAsync(Encoding.ASCII.GetBytes($"{r:x}\r\n"), ct);
                await conn.Inner.WriteAsync(buf.AsMemory(0, r), ct);
                await conn.WriteAsync("\r\n"u8.ToArray(), ct);
            }
            await conn.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
            return true;
        }
    }

    static byte[] Status(int code, string text, bool close) =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {text}\r\nContent-Length: 0\r\nConnection: {(close ? "close" : "keep-alive")}\r\n\r\n");

    static List<(string Name, string Value)> ParseHeaders(IEnumerable<string> lines)
    {
        var list = new List<(string, string)>();
        foreach (var l in lines)
        {
            var i = l.IndexOf(':');
            if (i > 0) list.Add((l[..i].Trim(), l[(i + 1)..].Trim()));
        }
        return list;
    }

    static string? Header(List<(string Name, string Value)> h, string name) =>
        h.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

    static bool HasToken(List<(string Name, string Value)> h, string name, string token) =>
        h.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && x.Value.Split(',').Any(t => t.Trim().Equals(token, StringComparison.OrdinalIgnoreCase)));

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        lock (_clients) foreach (var c in _clients) c.Dispose();
        if (_accept is not null) try { await _accept; } catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException) { }
        _http.Dispose();
        _cts.Dispose();
    }

    /// <summary>Поток соединения с буфером: заголовок читается до пустой строки, остаток — телу или следующему запросу.</summary>
    sealed class BufferedConn(Stream inner)
    {
        readonly byte[] _buf = new byte[16 * 1024];
        int _start, _end;
        public Stream Inner => inner;

        async ValueTask<bool> FillAsync(CancellationToken ct)
        {
            if (_start > 0 && _start == _end) _start = _end = 0;
            if (_end == _buf.Length)
            {
                if (_start == 0) return false;   // буфер полон, а конца строки нет
                Buffer.BlockCopy(_buf, _start, _buf, 0, _end - _start);
                _end -= _start; _start = 0;
            }
            var n = await inner.ReadAsync(_buf.AsMemory(_end), ct);
            if (n == 0) return false;
            _end += n;
            return true;
        }

        /// <summary>Заголовок запроса без пустой строки; null — соединение закрыто (или мусор/слишком длинно).</summary>
        public async Task<string?> ReadHeadAsync(int max, CancellationToken ct)
        {
            var sb = new StringBuilder();
            while (true)
            {
                var span = _buf.AsSpan(_start, _end - _start);
                var i = span.IndexOf("\r\n\r\n"u8);
                if (i >= 0)
                {
                    sb.Append(Encoding.Latin1.GetString(span[..i]));
                    _start += i + 4;
                    var s = sb.ToString().TrimStart('\r', '\n');
                    if (s.Length == 0) { sb.Clear(); continue; }
                    return s;
                }
                // Конец заголовка мог разрезаться между чтениями: оставляем 3 последних байта в буфере.
                if (span.Length > 3)
                {
                    sb.Append(Encoding.Latin1.GetString(span[..^3]));
                    _start = _end - 3;
                }
                if (sb.Length > max) return null;
                if (!await FillAsync(ct)) return null;
            }
        }

        public async Task<byte[]> ReadExactAsync(int n, CancellationToken ct)
        {
            var r = new byte[n];
            int got = 0;
            while (got < n)
            {
                if (_start == _end && !await FillAsync(ct)) throw new IOException("Соединение закрыто посреди тела запроса");
                var k = Math.Min(n - got, _end - _start);
                Buffer.BlockCopy(_buf, _start, r, got, k);
                _start += k; got += k;
            }
            return r;
        }

        /// <summary>Остаток буфера, затем всё, что придёт (туннель CONNECT).</summary>
        public async Task CopyToAsync(Stream dst, CancellationToken ct)
        {
            if (_end > _start) { await dst.WriteAsync(_buf.AsMemory(_start, _end - _start), ct); _start = _end; }
            await inner.CopyToAsync(dst, ct);
        }

        public ValueTask WriteAsync(byte[] data, CancellationToken ct) => inner.WriteAsync(data, ct);
    }
}

/// <summary>
/// Туннель на время сценария: прокси ПК + <c>ssh -R 127.0.0.1:RemotePort → 127.0.0.1:прокси</c>. В команду роутера —
/// <see cref="RouterSsh.ProxyEnv"/>(RemotePort). Освобождение закрывает проброс и прокси.
/// </summary>
public sealed class RouterTunnel : IAsyncDisposable
{
    readonly PcTunnelProxy _proxy;
    readonly IAsyncDisposable _forward;

    RouterTunnel(PcTunnelProxy proxy, IAsyncDisposable forward, int remotePort) { _proxy = proxy; _forward = forward; RemotePort = remotePort; }

    public int RemotePort { get; }
    public PcTunnelProxy Proxy => _proxy;

    /// <param name="feedHosts">Фиды роутера (<see cref="RouterSsh.FeedHostsAsync"/>) — к белому списку.</param>
    public static async Task<RouterTunnel> OpenAsync(ISshSession ssh, IEnumerable<string> feedHosts, Action<string>? log = null, CancellationToken ct = default)
    {
        var proxy = new PcTunnelProxy(feedHosts) { Log = log }.Start();
        RouterSshException? last = null;
        // Порт на роутере — случайный из верхнего диапазона; занят — другой.
        for (int i = 0; i < 5; i++)
        {
            var remote = Random.Shared.Next(20000, 60000);
            try { return new RouterTunnel(proxy, await ssh.ForwardRemoteAsync(remote, proxy.Port, ct), remote); }
            catch (RouterSshException e) { last = e; }
        }
        await proxy.DisposeAsync();
        throw last!;
    }

    public async ValueTask DisposeAsync()
    {
        await _forward.DisposeAsync();
        await _proxy.DisposeAsync();
    }
}
