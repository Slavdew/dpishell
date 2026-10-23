namespace DpiShell.Core;

public enum NetCause { Ok, NoInternet, CaptivePortal, ClockSkew }

/// <param name="Skew">Часы компьютера минус время сервера (ClockSkew).</param>
/// <param name="PortalUrl">Куда вести за входом в сеть (CaptivePortal).</param>
public sealed record NetVerdict(NetCause Cause, TimeSpan Skew = default, string? PortalUrl = null);

/// <summary>Ответ сервера без перехода по перенаправлениям; Date — заголовок времени сервера.</summary>
public sealed record NetReply(int Status, string? Location, string? Body, DateTimeOffset? Date);

/// <summary>Один GET: null — ответа нет (соединение, сертификат, таймаут). В тестах — подмена в памяти.</summary>
public interface INetFetcher
{
    Task<NetReply?> GetAsync(string url, CancellationToken ct);
}

/// <summary>Настоящая сеть: без системного прокси, без перенаправлений, сертификаты проверяются как обычно.</summary>
public sealed class HttpNetFetcher : INetFetcher
{
    public async Task<NetReply?> GetAsync(string url, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(4), PooledConnectionLifetime = TimeSpan.Zero };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            string? body = null;
            if ((int)resp.StatusCode == 200)
            {
                var buf = new byte[512];
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                int n = await s.ReadAsync(buf, ct);
                body = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            }
            return new((int)resp.StatusCode, resp.Headers.Location?.ToString(), body, resp.Headers.Date);
        }
        catch (Exception e) when (!ct.IsCancellationRequested && (e is HttpRequestException or IOException or OperationCanceledException))
        {
            return null;
        }
    }
}

/// <summary>
/// Почему не открывается «всё»: вход в сеть (captive portal), нет интернета, сбитое время. Без прав, только чтение.
/// Порядок: страница проверки соединения Windows → контрольные обычные сайты (группа control) → при провале всех время по заголовку Date.
/// </summary>
public static class NetDiagnosis
{
    public const string PortalProbeUrl = "http://www.msftconnecttest.com/connecttest.txt";
    const string PortalBody = "Microsoft Connect Test";
    /// <summary>Расхождение часов, с которого проверки сертификатов уже не проходят (запас от обычного дрейфа).</summary>
    public static readonly TimeSpan SkewLimit = TimeSpan.FromMinutes(5);

    public static async Task<NetVerdict> RunAsync(IReadOnlyList<string> controlUrls, INetFetcher net, Func<DateTimeOffset>? now = null, CancellationToken ct = default)
    {
        now ??= () => DateTimeOffset.UtcNow;
        var portal = await net.GetAsync(PortalProbeUrl, ct);
        if (portal is not null && IsForeign(portal))
            return new(NetCause.CaptivePortal, PortalUrl: PortalUrlOf(portal));

        var results = await Task.WhenAll(controlUrls.Take(4).Select(u => net.GetAsync(u, ct)));
        if (results.Any(r => r is not null)) return new(NetCause.Ok);

        // Ни один обычный сайт не открылся. Время сверяем по обычному HTTP: заголовок Date приходит и от перенаправления.
        var date = portal?.Date;
        if (date is null)
            foreach (var u in controlUrls.Take(4))
            {
                var r = await net.GetAsync(Uri.TryCreate(u, UriKind.Absolute, out var uri) ? "http://" + uri.Host + "/" : u, ct);
                if (r?.Date is { } d) { date = d; break; }
            }
        if (date is { } server)
        {
            var skew = now() - server;
            if (skew.Duration() > SkewLimit) return new(NetCause.ClockSkew, skew);
        }
        return new(NetCause.NoInternet);
    }

    /// <summary>Страница проверки Windows ответила чужим: перенаправление или другое тело (вход в сеть).</summary>
    static bool IsForeign(NetReply r) =>
        r.Status is >= 300 and < 400 || (r.Status == 200 && r.Body?.Trim() != PortalBody);

    static string PortalUrlOf(NetReply r) =>
        r.Location is { } l && Uri.TryCreate(l, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? u.ToString() : PortalProbeUrl;
}
