using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Цель проверки. handshake_only — достаточно TLS и заголовков ответа (у адреса нет стабильного большого тела).</summary>
public sealed record Target(string Url, int MinBytes = 0, bool HandshakeOnly = false, bool Required = true);

public sealed record TargetGroup(string Id, string Title, List<Target> Targets);

public sealed record TargetSet(List<TargetGroup> Groups)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static TargetSet Load(string path) =>
        JsonSerializer.Deserialize<TargetSet>(File.ReadAllText(path), Json) ?? new([]);

    /// <summary>Группа «Свои адреса» из доменов/URL пользователя: каждый адрес обязателен, тело ≥ 1 байта не требуем.</summary>
    public static TargetGroup UserGroup(IEnumerable<string> entries) => new("user", "Свои адреса",
        entries.Select(e => e.Trim()).Where(e => e.Length > 0)
            .Select(e => new Target(e.Contains("://") ? e : $"https://{e}/")).ToList());
}

public enum ProbeOutcome { Ok, Timeout, ConnectFailed, TlsFailed, Reset, Truncated, BadStatus, ForeignRedirect, Error }

public sealed record ProbeResult(Target Target, SslProtocols Tls, ProbeOutcome Outcome, double Seconds, long Bytes, string Detail)
{
    public bool Ok => Outcome == ProbeOutcome.Ok;
}

/// <summary>
/// Проверка «сайт реально открывается», а не «сервер что-то ответил»:
/// ТСПУ обычно пропускает рукопожатие и обрывает поток после ~16 КБ, поэтому тело читается целиком.
/// </summary>
public sealed class HttpProbe
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(6);
    public int MaxBytes { get; init; } = 1 << 20;
    public int Attempts { get; init; } = 2;

    public static readonly SslProtocols[] DefaultTls = [SslProtocols.Tls13, SslProtocols.Tls12];

    /// <summary>Цель проверяется для каждой версии TLS; успех — только если прошли все версии.</summary>
    public async Task<IReadOnlyList<ProbeResult>> CheckAsync(Target t, IEnumerable<SslProtocols>? tls = null, CancellationToken ct = default)
    {
        var list = new List<ProbeResult>();
        foreach (var v in tls ?? DefaultTls)
        {
            ProbeResult? best = null;
            for (int i = 0; i < Attempts; i++)
            {
                var r = await OnceAsync(t, v, ct);
                if (r.Ok) { best = best is { Ok: true } && best.Seconds <= r.Seconds ? best : r; break; }
                best ??= r;
                if (i + 1 < Attempts) await Task.Delay(500, ct);
            }
            list.Add(best!);
        }
        return list;
    }

    public async Task<ProbeResult> OnceAsync(Target t, SslProtocols tls, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        // Новый обработчик на каждую попытку: никаких переиспользованных соединений между стратегиями.
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectTimeout = ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.Zero,
            AutomaticDecompression = DecompressionMethods.None,
            SslOptions = { EnabledSslProtocols = tls },
        };
        using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ConnectTimeout + ReadTimeout);
        long bytes = 0;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, t.Url);
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0 Safari/537.36");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)resp.StatusCode;

            if (t.HandshakeOnly)
                return new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, 0, $"HTTP {code}");

            if (code is >= 300 and < 400)
            {
                var loc = resp.Headers.Location;
                var target = loc is null ? null : (loc.IsAbsoluteUri ? loc : new Uri(new Uri(t.Url), loc));
                // Редирект на страницу-заглушку провайдера — признак блокировки.
                return target is not null && SameSite(new Uri(t.Url).Host, target.Host)
                    ? new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, 0, $"HTTP {code} → {target.Host}")
                    : new(t, tls, ProbeOutcome.ForeignRedirect, sw.Elapsed.TotalSeconds, 0, $"редирект на {target?.Host ?? "?"}");
            }
            if (code == 400 || code >= 500)
                return new(t, tls, ProbeOutcome.BadStatus, sw.Elapsed.TotalSeconds, 0, $"HTTP {code}");

            var expected = resp.Content.Headers.ContentLength;
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[16384];
            int n;
            while (bytes < MaxBytes && (n = await stream.ReadAsync(buf, cts.Token)) > 0) bytes += n;

            if (bytes < MaxBytes && expected is { } len && bytes < len)
                return new(t, tls, ProbeOutcome.Truncated, sw.Elapsed.TotalSeconds, bytes, $"получено {bytes} из {len}");
            if (bytes < t.MinBytes)
                return new(t, tls, ProbeOutcome.Truncated, sw.Elapsed.TotalSeconds, bytes, $"получено {bytes}, нужно ≥ {t.MinBytes}");
            return new(t, tls, ProbeOutcome.Ok, sw.Elapsed.TotalSeconds, bytes, $"HTTP {code}, {bytes / 1024} КБ");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(t, tls, bytes > 0 ? ProbeOutcome.Truncated : ProbeOutcome.Timeout, sw.Elapsed.TotalSeconds, bytes,
                bytes > 0 ? $"поток оборвался на {bytes} байтах" : "таймаут");
        }
        catch (HttpRequestException e)
        {
            return new(t, tls, Classify(e), sw.Elapsed.TotalSeconds, bytes, Flatten(e));
        }
        catch (IOException e)
        {
            return new(t, tls, bytes > 0 ? ProbeOutcome.Truncated : ProbeOutcome.Reset, sw.Elapsed.TotalSeconds, bytes, Flatten(e));
        }
    }

    static ProbeOutcome Classify(HttpRequestException e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
        {
            if (x is AuthenticationException) return ProbeOutcome.TlsFailed;
            if (x is System.Net.Sockets.SocketException se)
                return se.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionReset ? ProbeOutcome.Reset : ProbeOutcome.ConnectFailed;
            if (x is IOException) return ProbeOutcome.Reset;
        }
        return e.HttpRequestError switch
        {
            HttpRequestError.SecureConnectionError => ProbeOutcome.TlsFailed,
            HttpRequestError.ConnectionError => ProbeOutcome.ConnectFailed,
            HttpRequestError.NameResolutionError => ProbeOutcome.ConnectFailed,
            _ => ProbeOutcome.Error,
        };
    }

    static string Flatten(Exception e)
    {
        var parts = new List<string>();
        for (Exception? x = e; x is not null; x = x.InnerException) parts.Add(x.Message);
        return string.Join(" / ", parts.Distinct());
    }

    /// <summary>youtube.com → www.youtube.com — один сайт; youtube.com → warning.rt.ru — нет.</summary>
    public static bool SameSite(string a, string b)
    {
        static string Base(string h)
        {
            var p = h.ToLowerInvariant().Split('.');
            return p.Length >= 2 ? $"{p[^2]}.{p[^1]}" : h;
        }
        return Base(a) == Base(b);
    }
}
