using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Статистика прошлых подборов (results\*.json): доля пройденных групп по каждой стратегии, свежие прогоны весят больше.
/// Нужна только для порядка проверки — стабильные идут первыми, быстрый подбор заканчивается раньше. На выбор лучшей не влияет.
/// </summary>
public static class StrategyStats
{
    const int Runs = 10;
    const double Unknown = 0.5;   // новые стратегии — после проверенно рабочих, но до проверенно нерабочих

    /// <summary>Score — взвешенная доля групп (для порядка); Runs — в скольких подборах участвовала; FullOk — во скольких открыла всё.</summary>
    public sealed record Stat(double Score, int Runs, int FullOk);

    public static IReadOnlyDictionary<string, double> Load(AppPaths p) =>
        LoadDetailed(p).ToDictionary(kv => kv.Key, kv => kv.Value.Score);

    public static IReadOnlyDictionary<string, Stat> LoadDetailed(AppPaths p)
    {
        var sum = new Dictionary<string, (double W, double V, int Runs, int Full)>();
        if (!Directory.Exists(p.Results)) return new Dictionary<string, Stat>();
        var files = Directory.GetFiles(p.Results, "*.json").OrderByDescending(f => f, StringComparer.Ordinal).Take(Runs).ToList();
        for (int i = 0; i < files.Count; i++)
        {
            double weight = 1.0 / (i + 1);
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(files[i]));
                if (!doc.RootElement.TryGetProperty("scores", out var scores)) continue;
                foreach (var s in scores.EnumerateArray())
                {
                    var id = s.GetProperty("Id").GetString();
                    var total = s.GetProperty("GroupsTotal").GetInt32();
                    if (id is null || total == 0 || s.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String) continue;
                    var passed = s.GetProperty("GroupsPassed").GetInt32();
                    var share = (double)passed / total;
                    var (w, v, runs, full) = sum.GetValueOrDefault(id);
                    sum[id] = (w + weight, v + share * weight, runs + 1, full + (passed == total ? 1 : 0));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException) { }
        }
        return sum.ToDictionary(kv => kv.Key, kv => new Stat(kv.Value.V / kv.Value.W, kv.Value.Runs, kv.Value.Full));
    }

    /// <summary>Порядок проверки: по статистике, при равенстве — исходный порядок (сначала готовые, потом найденные).</summary>
    public static IReadOnlyList<Strategy> Order(IReadOnlyList<Strategy> strategies, IReadOnlyDictionary<string, double> stats) =>
        strategies.Select((s, i) => (s, i))
            .OrderByDescending(x => stats.TryGetValue(x.s.Id, out var v) ? v : Unknown)
            .ThenBy(x => x.i)
            .Select(x => x.s).ToList();
}
