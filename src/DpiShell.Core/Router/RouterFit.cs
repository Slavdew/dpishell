namespace DpiShell.Core.Router;

/// <summary>Ответ «Проверить, подходит ли мой роутер»: да (похоже на OpenWrt), нет (отвечает, но это другая прошивка), не знаю (не ответил).</summary>
public enum RouterFitResult { Yes, No, Unknown }

/// <summary>
/// Первая проверка без пароля и без SSH: человек не знает слов «OpenWrt» и «LuCI», поэтому программа сама открывает страницу роутера на основном шлюзе
/// (по HTTP или HTTPS, без прокси) и смотрит, есть ли на ней признаки LuCI — страницы входа OpenWrt. Ничего не меняет и не отправляет, кроме одного GET.
/// Точный ответ — «Проверить роутер» в окне установки (по SSH); здесь — «похоже / не похоже / не отвечает».
/// </summary>
public static class RouterFit
{
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Признаки страницы входа LuCI в адресе и теле ответа.</summary>
    public static bool LooksLikeLuci(string? finalPath, string? body) =>
        (finalPath?.Contains("luci", StringComparison.OrdinalIgnoreCase) ?? false)
        || (body is not null && (body.Contains("luci-static", StringComparison.OrdinalIgnoreCase)
                                 || body.Contains("/cgi-bin/luci", StringComparison.OrdinalIgnoreCase)
                                 || body.Contains("LuCI", StringComparison.Ordinal)));

    /// <summary>
    /// Открыть <c>http://адрес/</c> (с переходами; HTTP закрыт — <c>https://адрес/</c>) и судить по странице. Сеть не ответила — <see cref="RouterFitResult.Unknown"/>.
    /// Самоподписанный сертификат роутера здесь принимается: это один GET без пароля и без данных, только «похоже ли на LuCI».
    /// Вход в окне проверяет сертификат по закреплённому отпечатку (<see cref="RouterCert"/>).
    /// </summary>
    public static async Task<RouterFitResult> ProbeAsync(string address, HttpClient? http = null, CancellationToken ct = default)
    {
        bool own = http is null;
        http ??= new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3), AllowAutoRedirect = true, MaxAutomaticRedirections = 3,
            SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
        });
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(Timeout);
            HttpResponseMessage resp;
            try { resp = await http.GetAsync(new Uri($"http://{address}/"), cts.Token); }
            catch (HttpRequestException) when (own && !cts.IsCancellationRequested) { resp = await http.GetAsync(new Uri($"https://{address}/"), cts.Token); }
            using (resp)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                if (body.Length > 200_000) body = body[..200_000];
                return LooksLikeLuci(resp.RequestMessage?.RequestUri?.AbsolutePath, body) ? RouterFitResult.Yes : RouterFitResult.No;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return RouterFitResult.Unknown;
        }
        finally { if (own) http.Dispose(); }
    }
}
