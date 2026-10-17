using System.Security.Authentication;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>
/// Кандидаты для глубокого поиска (architecture.md «Глубокий поиск»). Только параметры из белого списка <see cref="WinwsArgs"/>;
/// значения сверены с docs zapret v72.13 (readme.md, раздел nfqws).
/// </summary>
public static class CandidateGenerator
{
    const string Google = "{fake}/tls_clienthello_www_google_com.bin";
    static readonly string[] TlsFakes = [Google, "{fake}/tls_clienthello_4pda_to.bin", "{fake}/tls_clienthello_max_ru.bin", "!"];

    /// <summary>Грубый проход: по одному-двум представителям каждого семейства приёмов.</summary>
    public static IReadOnlyList<string> Coarse() =>
    [
        "--dpi-desync=multisplit --dpi-desync-split-pos=1",
        "--dpi-desync=multisplit --dpi-desync-split-pos=2,sniext+1",
        "--dpi-desync=multisplit --dpi-desync-split-pos=midsld",
        "--dpi-desync=multidisorder --dpi-desync-split-pos=1,midsld",
        $"--dpi-desync=multisplit --dpi-desync-split-seqovl=681 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={Google}",
        "--dpi-desync=multisplit --dpi-desync-split-seqovl=568 --dpi-desync-split-pos=1 --dpi-desync-split-seqovl-pattern={fake}/tls_clienthello_4pda_to.bin",
        $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-autottl=2 --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1 --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake,multisplit --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-split-pos=1 --dpi-desync-fake-tls={Google}",
        $"--dpi-desync=fake,multidisorder --dpi-desync-repeats=6 --dpi-desync-fooling=badseq --dpi-desync-split-pos=midsld --dpi-desync-fake-tls={Google}",
        "--dpi-desync=fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-split-pos=1 --dpi-desync-fakedsplit-pattern=0x00",
        $"--dpi-desync=fake,fakedsplit --dpi-desync-repeats=6 --dpi-desync-fooling=ts --dpi-desync-fakedsplit-pattern=0x00 --dpi-desync-fake-tls={Google}",
        "--dpi-desync=fakeddisorder --dpi-desync-repeats=6 --dpi-desync-fooling=md5sig --dpi-desync-split-pos=midsld",
        "--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=ts --dpi-desync-hostfakesplit-mod=host=www.google.com",
        "--dpi-desync=hostfakesplit --dpi-desync-repeats=4 --dpi-desync-fooling=md5sig --dpi-desync-hostfakesplit-mod=host=ya.ru",
    ];

    /// <summary>Соседи кандидата: меняем по одному параметру (жадный поиск).</summary>
    public static IEnumerable<string> Neighbours(string desync)
    {
        var t = Parse(desync);
        var mode = Get(t, "--dpi-desync") ?? "";
        bool hasFake = mode.Split(',').Any(m => m is "fake" or "fakedsplit" or "fakeddisorder" or "hostfakesplit");

        if (Get(t, "--dpi-desync-split-pos") is not null)
            foreach (var v in new[] { "1", "2", "midsld", "sniext+1", "1,midsld", "2,sniext+1", "host+1" })
                yield return Set(t, "--dpi-desync-split-pos", v);
        if (Get(t, "--dpi-desync-split-seqovl") is not null)
            foreach (var v in new[] { "568", "681", "336" })
                yield return Set(t, "--dpi-desync-split-seqovl", v);
        if (Get(t, "--dpi-desync-split-seqovl-pattern") is not null)
            foreach (var f in TlsFakes.Where(f => f != "!"))
                yield return Set(t, "--dpi-desync-split-seqovl-pattern", f);
        if (hasFake)
        {
            foreach (var v in new[] { "ts", "md5sig", "badseq", "badsum", "datanoack" })
                yield return Set(Remove(t, "--dpi-desync-autottl"), "--dpi-desync-fooling", v);
            foreach (var v in new[] { "2", "3" })
                yield return Set(Remove(t, "--dpi-desync-fooling"), "--dpi-desync-autottl", v);
            foreach (var v in new[] { "2", "6", "11" })
                yield return Set(t, "--dpi-desync-repeats", v);
        }
        if (Get(t, "--dpi-desync-fake-tls") is not null)
        {
            foreach (var f in TlsFakes)
                yield return Set(t, "--dpi-desync-fake-tls", f);
            yield return Set(t, "--dpi-desync-fake-tls-mod", "rnd,dupsid,sni=www.google.com");
        }
    }

    /// <summary>Сколько «шума» создаёт стратегия: при равном результате выбираем тише.</summary>
    public static double Cost(string desync)
    {
        var t = Parse(desync);
        var mode = Get(t, "--dpi-desync") ?? "";
        double repeats = int.TryParse(Get(t, "--dpi-desync-repeats"), out var r) ? r : 1;
        bool fake = mode.Contains("fake");
        return (fake ? repeats : 0) + mode.Split(',').Length + t.Count * 0.1;
    }

    public static List<(string Name, string Value)> Parse(string desync) =>
        desync.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(tok => { var p = tok.Split('=', 2); return (p[0], p.Length > 1 ? p[1] : ""); }).ToList();

    static string? Get(List<(string Name, string Value)> t, string name) => t.Where(x => x.Name == name).Select(x => x.Value).FirstOrDefault();

    static List<(string, string)> Remove(List<(string Name, string Value)> t, string name) => t.Where(x => x.Name != name).ToList();

    static string Set(List<(string Name, string Value)> t, string name, string value)
    {
        var r = t.ToList();
        var i = r.FindIndex(x => x.Name == name);
        if (i >= 0) r[i] = (name, value); else r.Add((name, value));
        return Format(r);
    }

    static string Format(IEnumerable<(string Name, string Value)> t) =>
        string.Join(' ', t.Select(x => x.Value.Length > 0 ? $"{x.Name}={x.Value}" : x.Name));
}

public sealed record CandidateResult(string Role, string Desync, int Ok, int Total, double Seconds)
{
    public double Share => Total == 0 ? 0 : (double)Ok / Total;
    public double Cost => CandidateGenerator.Cost(Desync);
}

public sealed record SearchReport(
    IReadOnlyDictionary<string, IReadOnlyList<CandidateResult>> ByRole,
    IReadOnlyList<Strategy> Generated,
    SelectionReport? Final,
    string? Stopped);

/// <summary>Глубокий поиск: роли подбираются раздельно, лучшие собираются в стратегии и проходят полную проверку вместе с базовой.</summary>
public sealed class StrategySearch(AppPaths paths, Func<HttpProbe> newProbe)
{
    /// <summary>Какая роль обслуживает какие группы целей (по спискам: google.txt — YouTube, general.txt + свои — остальное).</summary>
    public static readonly IReadOnlyDictionary<string, string[]> RoleGroups = new Dictionary<string, string[]>
    {
        ["google"] = ["youtube"],
        ["general"] = ["discord", "user"],
    };

    public int RefineBudget { get; init; } = 30;
    public TimeSpan Pause { get; init; } = TimeSpan.FromMilliseconds(300);

    public async Task<SearchReport> RunAsync(Strategy baseStrategy, IReadOnlyList<TargetGroup> groups,
        IProgress<SelectorProgress>? progress = null, CancellationToken ct = default)
    {
        var byRole = new Dictionary<string, IReadOnlyList<CandidateResult>>();
        string? stopped = null;
        // Быстрая оценка: обязательные цели, одна попытка, короткие таймауты. Обе версии TLS: живой прогон 2026-10-16 показал,
        // что «тихий» multidisorder проходит по TLS 1.3 и не проходит по 1.2 — проверка только 1.3 пропускала такие находки.
        var quick = new Selector(paths, QuickProbe()) { Tls = HttpProbe.DefaultTls };

        foreach (var (role, groupIds) in RoleGroups)
        {
            var roleGroups = groups.Where(g => groupIds.Contains(g.Id))
                .Select(g => g with { Targets = g.Targets.Where(t => t.Required).ToList() })
                .Where(g => g.Targets.Count > 0).ToList();
            if (roleGroups.Count == 0) continue;

            var results = new List<CandidateResult>();
            var tried = new HashSet<string>();
            async Task<CandidateResult?> Try(string desync)
            {
                if (!tried.Add(desync)) return null;
                var s = baseStrategy with { Id = "search", Title = "поиск", Roles = new(baseStrategy.Roles) { [role] = desync } };
                WinwsArgs.Validate(s);
                var score = await quick.RunOneAsync(s, roleGroups, ct);
                var ok = score.Verdicts.Count(v => v.Ok);
                var r = new CandidateResult(role, desync, score.Error is null ? ok : 0, score.Verdicts.Count,
                    score.Verdicts.Where(v => v.Ok).Select(v => v.BestSeconds).DefaultIfEmpty(99).Average());
                results.Add(r);
                progress?.Report(new(results.Count, 0, $"{role}: {r.Ok}/{r.Total} {Short(desync)}"));
                await Task.Delay(Pause, ct);
                return r;
            }

            // Контроль: если базовая роль работала и вдруг перестала — провайдер, похоже, режет нас самих.
            var control = await Try(baseStrategy.Roles.GetValueOrDefault(role) ?? CandidateGenerator.Coarse()[0]);
            bool controlWorks = control is { Share: 1 };
            int sinceControl = 0, controlFails = 0;
            async Task<bool> ControlStillOk()
            {
                if (!controlWorks || ++sinceControl < 10) return true;
                sinceControl = 0;
                tried.Remove(control!.Desync);
                var c = await Try(control.Desync);
                controlFails = c is { Share: 1 } ? 0 : controlFails + 1;
                return controlFails < 2;
            }

            try
            {
                foreach (var c in CandidateGenerator.Coarse())
                {
                    await Try(c);
                    if (!await ControlStillOk()) { stopped = "провайдер, похоже, временно ограничил соединения — продолжите поиск позже"; break; }
                }

                if (!await RefineAsync(Best(results, 3).Where(r => r.Ok > 0).ToList(), Try, ControlStillOk, RefineBudget))
                    stopped = "провайдер, похоже, временно ограничил соединения — продолжите поиск позже";
            }
            catch (OperationCanceledException) { stopped = "остановлено"; }
            byRole[role] = results;
            if (stopped is not null) break;
        }

        // Сборка: лучшие 2 по каждой роли, все сочетания + базовая стратегия, полная проверка.
        var generated = new List<Strategy>();
        var tops = RoleGroups.Keys.ToDictionary(r => r, r => byRole.TryGetValue(r, out var l) ? Best(l.Where(x => x.Ok > 0), 2).ToList() : []);
        var stamp = DateTime.Now.ToString("yyMMdd-HHmm");
        foreach (var g in tops["google"].DefaultIfEmpty(null))
            foreach (var d in tops["general"].DefaultIfEmpty(null))
            {
                if (g is null && d is null) continue;
                var roles = new Dictionary<string, string>(baseStrategy.Roles);
                if (g is not null) roles["google"] = g.Desync;
                if (d is not null) roles["general"] = d.Desync;
                // ipset-профиль общего трафика — тот же приём, что и general.
                if (d is not null && roles.ContainsKey("ipset_tcp")) roles["ipset_tcp"] = d.Desync;
                // Игровой TCP-профиль базы построен на её general — с новым general он выводится заново (WinwsArgs.DeriveGameTcp).
                if (d is not null) roles.Remove("game_tcp");
                var s = new Strategy($"found-{stamp}-{generated.Count + 1}", $"Найденная {generated.Count + 1} ({stamp})", "search", roles);
                WinwsArgs.Validate(s);
                generated.Add(s);
            }

        SelectionReport? final = null;
        if (generated.Count > 0 && !ct.IsCancellationRequested)
        {
            var full = new Selector(paths, newProbe());
            try { final = await full.RunAsync([baseStrategy, .. generated], groups, quick: false, progress, ct); }
            catch (OperationCanceledException) { stopped ??= "остановлено"; }
        }
        return new(byRole, generated, final, stopped);
    }

    HttpProbe QuickProbe()
    {
        var p = newProbe();
        return new HttpProbe { Resolver = p.Resolver, Attempts = 1, ConnectTimeout = TimeSpan.FromSeconds(2), ReadTimeout = TimeSpan.FromSeconds(4) };
    }

    static IEnumerable<CandidateResult> Best(IEnumerable<CandidateResult> l, int n) =>
        l.OrderByDescending(r => r.Share).ThenBy(r => r.Cost).ThenBy(r => r.Seconds).Take(n);

    /// <summary>
    /// Уточнение вокруг лучших семейств, по одному параметру за шаг, «первое улучшение»: нашли соседа лучше —
    /// сразу продолжаем от него. Прежний обход слоями тратил бюджет на первый слой соседей (у трёх лучших ~45),
    /// и улучшение в два шага (fooling, потом repeats) не находилось никогда (сессия роутера, 2026-10-17; на роутере — так же).
    /// Бюджет общий на все семейства; уже проверенное не повторяется (<paramref name="tryOne"/> отдаёт null).
    /// False — контроль сказал, что провайдер режет нас самих.
    /// </summary>
    public static async Task<bool> RefineAsync(IReadOnlyList<CandidateResult> seeds, Func<string, Task<CandidateResult?>> tryOne,
        Func<Task<bool>> stillOk, int budget)
    {
        foreach (var seed in seeds)
        {
            var current = seed;
            bool improved = true;
            while (improved && budget > 0)
            {
                improved = false;
                foreach (var n in CandidateGenerator.Neighbours(current.Desync))
                {
                    if (budget <= 0) break;
                    var r = await tryOne(n);
                    if (r is null) continue;
                    budget--;
                    if (!await stillOk()) return false;
                    if (Better(r, current)) { current = r; improved = true; break; }
                }
            }
            if (budget <= 0) break;
        }
        return true;
    }

    static bool Better(CandidateResult a, CandidateResult b) =>
        a.Share > b.Share || a.Share == b.Share && (a.Cost < b.Cost - 0.01 || a.Cost <= b.Cost && a.Seconds < b.Seconds * 0.8);

    static string Short(string s) => s.Replace("--dpi-desync-", "").Replace("{fake}/", "");
}

/// <summary>Свои найденные стратегии пользователя: отдельный файл, та же проверка, что и у каталога.</summary>
public static class UserStrategies
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static string File(AppPaths p) => Path.Combine(p.Root, "user-strategies.json");

    public static List<Strategy> Load(AppPaths p)
    {
        try
        {
            if (!System.IO.File.Exists(File(p))) return [];
            var list = JsonSerializer.Deserialize<List<Strategy>>(System.IO.File.ReadAllText(File(p)), Json) ?? [];
            list.ForEach(WinwsArgs.Validate);
            return list;
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { return []; }
    }

    public static void Add(AppPaths p, IEnumerable<Strategy> found)
    {
        var all = Load(p);
        foreach (var s in found) { all.RemoveAll(x => x.Id == s.Id); all.Add(s); }
        p.EnsureDirectories();
        System.IO.File.WriteAllText(File(p), JsonSerializer.Serialize(all, Json));
    }
}
