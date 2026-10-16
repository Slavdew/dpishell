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
        var doc = new
        {
            at = DateTime.Now,
            best = r.Best?.Strategy.Id,
            cancelled = r.Cancelled,
            baseline = r.Baseline.Select(v => new { v.GroupId, v.Target.Url, v.Ok, detail = v.Probes.Select(x => $"{x.Tls}: {x.Outcome} {x.Detail}") }),
            scores = r.Scores.Select(s => new
            {
                s.Strategy.Id, s.GroupsPassed, s.GroupsTotal, s.TargetShare, s.AvgSeconds, s.Error,
                targets = s.Verdicts.Select(v => new { v.Target.Url, v.Ok, detail = v.Probes.Select(x => $"{x.Tls}: {x.Outcome} {x.Detail}") }),
            }),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return file;
    }
}
