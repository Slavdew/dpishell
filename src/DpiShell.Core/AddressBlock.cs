namespace DpiShell.Core;

/// <summary>
/// «Не открылось ни с одной стратегией»: адрес, который провалился у всех проверенных стратегий, и каждый раз — без ответа
/// (таймаут, нет соединения, сброс). Стратегия меняет только то, как выглядит поток для DPI; когда не отвечает сам адрес
/// (его IP закрыт, сервер недоступен), ни одна стратегия не поможет — искать глубже бессмысленно.
/// Признак: адрес проверен не менее <see cref="MinChecks"/> раз, ни разу не открылся, и во всех проверках обе версии TLS —
/// без ответа. 403/404, заглушка, 451 и «поток оборван после 16 КБ» сюда не входят: это не молчание, а ответ (или
/// работа DPI, которую стратегия как раз лечит).
/// </summary>
public sealed class AddressBlockTracker
{
    /// <summary>Меньше проверок — вывод ненадёжен (мало стратегий пробовали).</summary>
    public const int MinChecks = 5;

    sealed class Stat { public int Checks; public bool EverOk; public bool AllSilent = true; }

    readonly Dictionary<string, Stat> _urls = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    public void Clear() { _urls.Clear(); _seen.Clear(); }

    /// <summary>Одна проверка адреса одной стратегией. Повтор той же пары (стратегия, адрес) не считается.</summary>
    public void Add(string strategyId, string url, bool ok, bool silent)
    {
        if (!_seen.Add(strategyId + "|" + url)) return;
        if (!_urls.TryGetValue(url, out var s)) _urls[url] = s = new();
        s.Checks++;
        if (ok) s.EverOk = true;
        else if (!silent) s.AllSilent = false;
    }

    public void Add(string strategyId, TargetVerdict v) =>
        Add(strategyId, v.Target.Url, v.Ok, !v.Ok && IsSilent(v.Probes));

    public void Add(StrategyScore s)
    {
        if (s.Error is not null) return;   // не запустилась — адреса не проверялись
        foreach (var v in s.Verdicts) Add(s.Strategy.Id, v);
    }

    /// <summary>Все проверки адреса — без ответа: таймаут, нет соединения или сброс (по обеим версиям TLS).</summary>
    public static bool IsSilent(IReadOnlyList<ProbeResult> probes) =>
        probes.Count > 0 && probes.All(p => p.Outcome is ProbeOutcome.Timeout or ProbeOutcome.ConnectFailed or ProbeOutcome.Reset);

    static readonly string[] SilentWords = ["таймаут", "нет ответа", "timed out", "timeout", "reset", "сброшено", "failed to connect", "could not connect", "нет соединения"];

    /// <summary>
    /// Роутер: исход по адресу — одна строка «TLS 1.3: таймаут; TLS 1.2: таймаут» или «таймаут». Без ответа — если каждая часть
    /// про молчание сервера; «поток оборвался на N байтах», коды HTTP, редиректы и ошибки TLS — нет.
    /// </summary>
    public static bool IsSilent(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return false;
        var parts = detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 0 && parts.All(p =>
        {
            var low = p.ToLowerInvariant();
            if (low.Contains("оборвал") || low.Contains("http ") || low.Contains("редирект")) return false;
            return SilentWords.Any(low.Contains);
        });
    }

    /// <summary>Адреса, не открывшиеся ни разу и всегда без ответа, в порядке первой проверки.</summary>
    public IReadOnlyList<string> Blocked => _urls.Where(kv => kv.Value is { EverOk: false, AllSilent: true } s && s.Checks >= MinChecks).Select(kv => kv.Key).ToList();

    /// <summary>Адрес хоть раз открылся.</summary>
    public bool Opened(Func<string, bool> hostIs) => _urls.Any(kv => kv.Value.EverOk && hostIs(HostOf(kv.Key)));

    public static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    /// <summary>
    /// «Не открылось ни с одной стратегией: a, b — похоже на блокировку по адресу, подбор стратегии тут, скорее всего, не поможет.»
    /// Для YouTube — ещё что сайт откроется, а видео может нет. Пусто, когда таких адресов нет.
    /// </summary>
    public string Note()
    {
        var hosts = Blocked.Select(HostOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (hosts.Count == 0) return "";
        var shown = string.Join(", ", hosts.Take(4)) + (hosts.Count > 4 ? $" и ещё {hosts.Count - 4}" : "");
        var text = $"Не открылось ни с одной стратегией: {shown} — похоже на блокировку по адресу, подбор стратегии тут, скорее всего, не поможет.";
        if (hosts.Any(h => h.EndsWith("googlevideo.com", StringComparison.OrdinalIgnoreCase)) && Opened(h => h is "www.youtube.com" or "youtube.com"))
            text += " Сайт YouTube откроется, а видео может не воспроизводиться.";
        return text;
    }

    /// <summary>Весь провал лучшей стратегии — из-за таких адресов: глубокий поиск ради них не нужен.</summary>
    public bool ExplainsAllFailures(IEnumerable<string> failedUrls)
    {
        var blocked = Blocked.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = failedUrls.ToList();
        return blocked.Count > 0 && failed.Count > 0 && failed.All(blocked.Contains);
    }
}
