using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace DpiShell.Core;

public sealed record ProxyProbe(string Target, bool Ok, int Ms, string? Error);

/// <param name="Kind">"http" (HTTP CONNECT) или "socks5".</param>
public sealed record ProxyCheckResult(bool Ok, string Kind, IReadOnlyList<ProxyProbe> Probes, string Summary);

/// <summary>
/// «Проверить прокси»: через сам прокси открыть туннель до пары адресов (HTTP CONNECT; SOCKS5 — для socks=). Данные по туннелю не идут —
/// проверяется только, что прокси отвечает и соединяет. Окну права не нужны.
/// </summary>
public static class ProxyChecker
{
    public static readonly IReadOnlyList<(string Host, int Port)> DefaultTargets = [("www.microsoft.com", 443), ("www.cloudflare.com", 443)];
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(6);

    public static async Task<ProxyCheckResult> CheckAsync(string server, IReadOnlyList<(string Host, int Port)>? targets = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var spec = ProxyValues.ParseServer(server);
        if (spec?.ForCheck() is not { } use || !ProxyValues.TryHostPort(use.HostPort, out var host, out var port))
            return new(false, "http", [], "Адрес прокси не подходит для проверки.");
        var list = targets ?? DefaultTargets;
        var probes = new List<ProxyProbe>();
        foreach (var (th, tp) in list)
        {
            ct.ThrowIfCancellationRequested();
            probes.Add(await ProbeAsync(use.Kind, host.Trim('[', ']'), port, th, tp, timeout ?? DefaultTimeout, ct));
        }
        int okCount = probes.Count(p => p.Ok);
        int avg = okCount == 0 ? 0 : (int)probes.Where(p => p.Ok).Average(p => p.Ms);
        var kindText = use.Kind == "socks5" ? "SOCKS5" : "HTTP";
        var summary = okCount == probes.Count ? $"работает ({kindText}): {okCount} из {probes.Count}, ≈{avg} мс"
            : okCount > 0 ? $"отвечает не на всё ({kindText}): {okCount} из {probes.Count}, ≈{avg} мс"
            : "не отвечает: " + (probes.FirstOrDefault()?.Error ?? "нет ответа");
        return new(okCount == probes.Count && probes.Count > 0, use.Kind, probes, summary);
    }

    static async Task<ProxyProbe> ProbeAsync(string kind, string proxyHost, int proxyPort, string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var target = $"{host}:{port}";
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(proxyHost, proxyPort, cts.Token);
            var s = tcp.GetStream();
            var error = kind == "socks5" ? await Socks5Async(s, host, port, cts.Token) : await ConnectAsync(s, host, port, cts.Token);
            sw.Stop();
            return error is null ? new(target, true, (int)sw.ElapsedMilliseconds, null) : new(target, false, (int)sw.ElapsedMilliseconds, error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(target, false, (int)timeout.TotalMilliseconds, timeout.TotalSeconds >= 1 ? $"нет ответа за {(int)timeout.TotalSeconds} с" : "нет ответа"); }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            return new(target, false, (int)sw.ElapsedMilliseconds, e is SocketException { SocketErrorCode: SocketError.ConnectionRefused } ? "прокси отказал в соединении (не запущен?)" : "ошибка соединения");
        }
    }

    static async Task<string?> ConnectAsync(NetworkStream s, string host, int port, CancellationToken ct)
    {
        var req = $"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\nProxy-Connection: keep-alive\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(req), ct);
        var buf = new byte[4096];
        int len = 0;
        while (len < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) break;
            len += n;
            var text = Encoding.ASCII.GetString(buf, 0, len);
            int eol = text.IndexOf("\r\n", StringComparison.Ordinal);
            if (eol < 0) continue;
            var parts = text[..eol].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || !int.TryParse(parts[1], out var code)) return "это не HTTP-прокси";
            return code switch
            {
                200 => null,
                407 => "прокси просит логин и пароль",
                403 => "прокси запретил этот адрес",
                _ => $"прокси ответил {code}",
            };
        }
        return "прокси закрыл соединение без ответа";
    }

    static async Task<string?> Socks5Async(NetworkStream s, string host, int port, CancellationToken ct)
    {
        await s.WriteAsync(new byte[] { 5, 1, 0 }, ct);
        var rep = new byte[2];
        if (!await ReadExactAsync(s, rep, ct) || rep[0] != 5) return "это не SOCKS5-прокси";
        if (rep[1] == 0xFF) return "SOCKS5-прокси не принял вход без пароля";
        if (rep[1] != 0) return "SOCKS5-прокси просит логин и пароль";
        var name = Encoding.ASCII.GetBytes(host);
        var req = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
        req.AddRange(name);
        req.Add((byte)(port >> 8)); req.Add((byte)(port & 0xFF));
        await s.WriteAsync(req.ToArray(), ct);
        var head = new byte[4];
        if (!await ReadExactAsync(s, head, ct)) return "прокси закрыл соединение без ответа";
        return head[1] == 0 ? null : $"SOCKS5-прокси отказал (код {head[1]})";
    }

    static async Task<bool> ReadExactAsync(NetworkStream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n == 0) return false;
            got += n;
        }
        return true;
    }
}
