using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Что помним между запусками: выбранная стратегия, свои адреса, группы, результаты по сетям.</summary>
public sealed class AppState
{
    public string? StrategyId { get; set; }
    public bool Enabled { get; set; }
    public List<string> UserTargets { get; set; } = [];
    public List<string> Groups { get; set; } = ["youtube", "discord"];
    public Dictionary<string, NetworkMemory> Networks { get; set; } = [];

    /// <summary>Рабочие стратегии по рейтингу последнего подбора (до 3): на них переключается автопроверка (ТЗ §9 п. 15).</summary>
    public List<string> Fallbacks { get; set; } = [];
    /// <summary>Когда последний раз была автопроверка (локальное время).</summary>
    public DateTime? WatchCheckedAt { get; set; }
    /// <summary>Неудачных автопроверок подряд у текущей стратегии.</summary>
    public int WatchFails { get; set; }
    /// <summary>Запасные кончились, пользователю уже сказали «подберите заново» — больше не дёргаем до нового подбора или успеха.</summary>
    public bool WatchGaveUp { get; set; }
    /// <summary>Оформление окна: "system" (как в Windows), "light" или "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>Крестик окна: "tray" — свернуть в трей (по умолчанию), "exit" — закрыть программу. Служба обхода работает в обоих случаях.</summary>
    public string CloseAction { get; set; } = "tray";

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static AppState Load(AppPaths p)
    {
        try { return File.Exists(p.StateFile) ? JsonSerializer.Deserialize<AppState>(File.ReadAllText(p.StateFile), Json) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public void Save(AppPaths p)
    {
        p.EnsureDirectories();
        var tmp = p.StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, p.StateFile, overwrite: true);
    }
}

public sealed record NetworkMemory(string StrategyId, DateTime CheckedAt, double Score);

public static class ResultStore
{
    public static string Save(AppPaths p, SelectionReport r)
    {
        p.EnsureDirectories();
        var file = Path.Combine(p.Results, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");

        // Свои адреса пользователя в историю не пишем: «свой адрес №N», хост вычищается и из текстов ошибок.
        var userNo = new Dictionary<string, int>();
        foreach (var v in r.Baseline.Concat(r.Scores.SelectMany(s => s.Verdicts)).Where(v => v.GroupId == "user"))
            userNo.TryAdd(v.Target.Url, userNo.Count + 1);
        string Url(TargetVerdict v) => v.GroupId == "user" ? $"свой адрес №{userNo[v.Target.Url]}" : v.Target.Url;
        IEnumerable<string> Detail(TargetVerdict v) => v.Probes.Select(x =>
        {
            var d = $"{x.Tls}: {x.Outcome} {x.Detail}";
            if (v.GroupId != "user") return d;
            var host = Uri.TryCreate(v.Target.Url, UriKind.Absolute, out var u) ? u.Host : v.Target.Url;
            return d.Replace(v.Target.Url, "***").Replace(host, "***", StringComparison.OrdinalIgnoreCase);
        });

        var doc = new
        {
            at = DateTime.Now,
            best = r.Best?.Strategy.Id,
            cancelled = r.Cancelled,
            baseline = r.Baseline.Select(v => new { v.GroupId, Url = Url(v), v.Ok, detail = Detail(v) }),
            scores = r.Scores.Select(s => new
            {
                s.Strategy.Id, s.GroupsPassed, s.GroupsTotal, s.TargetShare, s.AvgSeconds, s.Error,
                targets = s.Verdicts.Select(v => new { Url = Url(v), v.Ok, detail = Detail(v) }),
            }),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return file;
    }

    /// <summary>«Очистить историю»: результаты подборов (а с ними статистика) и логи. Свои адреса и найденные стратегии не трогает.</summary>
    public static int ClearHistory(AppPaths p)
    {
        int n = 0;
        foreach (var dir in new[] { p.Results, p.Logs })
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir))
            {
                try { File.Delete(f); n++; }
                catch (IOException) { }   // лог, открытый прямо сейчас, — останется до следующего раза
            }
        }
        return n;
    }
}
