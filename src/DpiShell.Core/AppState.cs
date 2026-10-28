using DpiShell.Core.Localization;
using System.Text.Json;

namespace DpiShell.Core;

/// <summary>Что помним между запусками: выбранная стратегия, свои сайты, группы, результаты по сетям.</summary>
public sealed class AppState
{
    public string? StrategyId { get; set; }
    public bool Enabled { get; set; }
    public List<string> UserTargets { get; set; } = [];
    /// <summary>
    /// Группы своих сайтов (API 1.18), только именованные: сайты, которых тут нет, — «Прочее». Необязательное поле: старые файлы читаются как есть,
    /// старая версия программы его не знает и игнорирует. <see cref="UserTargets"/> остаётся полным плоским списком — его читает всё остальное.
    /// </summary>
    public List<SiteGroup> UserGroups { get; set; } = [];
    /// <summary>Сайты, которые на этом ПК открываются без оптимизации (user-exclude.txt, API 1.10): «Открыть без оптимизации» и список на «Сайтах».</summary>
    public List<string> UserExclude { get; set; } = [];
    public List<string> Groups { get; set; } = ["youtube", "discord"];
    public Dictionary<string, NetworkMemory> Networks { get; set; } = [];

    /// <summary>Рабочие стратегии по рейтингу последнего подбора (до 3): на них переключается автопроверка (architecture.md «Проверка по таймеру и запасные»).</summary>
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
    /// <summary>"service" — служба Windows (с Windows, без окна); "process" — winws живёт, пока открыта программа.</summary>
    public string RunMode { get; set; } = "service";
    /// <summary>Временные метки TCP включили мы (были выключены) — «Удалить полностью» вернёт как было.</summary>
    public bool TimestampsEnabledByUs { get; set; }
    /// <summary>Адрес роутера (IP или имя); null — «Автоматически — основной шлюз» (router-api §2).</summary>
    public string? RouterAddress { get; set; }
    /// <summary>Пароль роутера, зашифрованный DPAPI (CurrentUser) — см. RouterSecrets. Открытым не хранится.</summary>
    public string? RouterPassword { get; set; }
    /// <summary>IP-список: "off" (заглушка, по умолчанию), "list" (загруженный + свои), "any" (все адреса, временно) — architecture.md «IP-списки и игровой режим».</summary>
    public string IpsetMode { get; set; } = "off";
    /// <summary>До какого момента (UTC) действует «Все адреса»; позже — считается прежний режим (BuildOptions.From).</summary>
    public DateTime? IpsetAnyUntil { get; set; }
    /// <summary>Режим, в который «Все адреса» возвращается сам.</summary>
    public string IpsetModeBeforeAny { get; set; } = "off";
    /// <summary>«Общий игровой режим»: "off" (по умолчанию), "all" (TCP+UDP 1024–65535 по IP-списку), "tcp"/"udp" — его части для отладочной консоли. Профили программ включаются каждый сам (<see cref="AppProfile.Enabled"/>).</summary>
    public string GameMode { get; set; } = "off";
    /// <summary>
    /// Версия записи игровых настроек: 0 — до режима «Свои профили» (профили действовали всегда), 1 — режим «Выкл / Свои профили / Общий» (профили — только при
    /// «Своих профилях» и «Общем»), 2 — профили включаются по отдельности. Чтение переводит старое в новое без потерь (<see cref="MigrateGameMode"/>); <see cref="Save"/> ставит 2.
    /// </summary>
    public int GameModeSchema { get; set; }
    /// <summary>«Разные стратегии» у профилей программ: выключено — все профили идут игровой частью стратегии ПК, выбор у профилей хранится.</summary>
    public bool ProfilesSeparate { get; set; }
    /// <summary>IP-список включил игровой режим (был «выкл»): при выключении игрового режима список выключается — как было до него. Выбор режима списка рукой сбрасывает.</summary>
    public bool IpsetByGame { get; set; }
    /// <summary>«Починить адреса» (hosts, architecture.md «Починить адреса») включено. По умолчанию выключено.</summary>
    public bool HostsFix { get; set; }
    /// <summary>Когда последний раз перепроверяли записи hosts (локальное время) — окно делает это раз в сутки и при старте.</summary>
    public DateTime? HostsCheckedAt { get; set; }

    /// <summary>Профили программ (ТЗ 13, «Игры и программы»): порты и адреса игры и параметры оптимизации для них. «Вернуть как было» — удалить профиль.</summary>
    public List<AppProfile> AppProfiles { get; set; } = [];

    /// <summary>Продвинутый режим «сайты → стратегия» (docs/research/mixed-strategies.md §6): выключен по умолчанию; строки хранятся и при выключенном режиме.</summary>
    public AdvancedConfig Advanced { get; set; } = new();

    /// <summary>Совместная работа с роутером (mixed-strategies.md §7): выключена по умолчанию; связка действует, пока окно её продлевает (<see cref="JointConfig.Until"/>).</summary>
    public JointConfig Joint { get; set; } = new();

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static AppState Load(AppPaths p)
    {
        try { return File.Exists(p.StateFile) ? (JsonSerializer.Deserialize<AppState>(File.ReadAllText(p.StateFile), Json) ?? new()).BareDomains().MigrateGameMode() : new(); }
        catch (JsonException) { return new(); }
    }

    public const int CurrentGameSchema = 2;

    /// <summary>
    /// Прежний режим → включённость профилей. Схема 0: профили действовали всегда — все включены. Схема 1: «Свои профили» — профили включены, «Общий» выкл;
    /// «Общий» — «Общий» вкл и профили вкл (при нём они тоже действовали); «Выкл» — профили выключены (они не действовали). Значение "profiles" уходит.
    /// </summary>
    AppState MigrateGameMode()
    {
        if (GameModeSchema < CurrentGameSchema)
        {
            bool on = GameModeSchema == 0 || GameMode != "off";
            foreach (var p in AppProfiles) p.Enabled = on;
            if (GameMode == "profiles") GameMode = "off";
        }
        GameModeSchema = CurrentGameSchema;
        return this;
    }

    /// <summary>Свои сайты до 0.11.3 хранились строкой целиком («https://site/путь?…»): приводим к голым доменам, как пишет теперь <see cref="Shell.SetUserTargets"/>.</summary>
    AppState BareDomains()
    {
        UserTargets = [.. UserTargets.Select(DataStore.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)];
        UserGroups = SiteGroups.Named(SiteGroups.Normalize(UserGroups, out _));
        return this;
    }

    public void Save(AppPaths p)
    {
        p.EnsureDirectories();
        GameModeSchema = CurrentGameSchema;
        var tmp = p.StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, p.StateFile, overwrite: true);
    }
}

public sealed record NetworkMemory(string StrategyId, DateTime CheckedAt, double Score);

public static class ResultStore
{
    /// <summary>Группа с адресами пользователя: «Свои сайты» и строки продвинутого режима (adv-N) — в историю идут без адресов.</summary>
    public static bool IsUserGroup(string groupId) => groupId == "user" || AdvancedSites.IsGroupId(groupId);

    public static string Save(AppPaths p, SelectionReport r)
    {
        p.EnsureDirectories();
        var file = Path.Combine(p.Results, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.json");

        // Свои сайты пользователя в историю не пишем: «свой адрес №N», хост вычищается и из текстов ошибок.
        var userNo = new Dictionary<string, int>();
        foreach (var v in r.Baseline.Concat(r.Scores.SelectMany(s => s.Verdicts.Concat(s.Blocked))).Where(v => IsUserGroup(v.GroupId)))
            userNo.TryAdd(v.Target.Url, userNo.Count + 1);
        // Имена групп задаёт человек — в историю идут номером («группа №N»), как адреса; «Прочее» — без имени. Сколько адресов в группе всего — для «проверено 10 из 120».
        var groupNo = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in r.Scores.SelectMany(s => s.Verdicts.Concat(s.Blocked)).Where(v => v.GroupId == "user" && v.Target.Group is { Length: > 0 }))
            groupNo.TryAdd(v.Target.Group!, groupNo.Count + 1);
        var control = r.TestedGroups.Where(g => g.Control).Select(g => g.Id).ToHashSet();
        var totals = r.TestedGroups.FirstOrDefault(g => g.Id == "user")?.Totals;
        string? Sub(TargetVerdict v) => v.GroupId == "user" && v.Target.Group is { Length: > 0 } g ? L.T("core.addr.group_n", new { n = groupNo[g] }) : null;
        int? SubTotal(TargetVerdict v) => v.GroupId == "user" && totals is not null && totals.TryGetValue(v.Target.Group ?? "", out var n) ? n : null;
        object Entry(TargetVerdict v, bool blocked) => new
        {
            v.GroupId, Url = Url(v), v.Ok, v.Target.Required, Control = control.Contains(v.GroupId), Seconds = Seconds(v), detail = Detail(v),
            Probes = v.Probes.Select(x => new { Tls = x.Tls.ToString(), Outcome = x.Outcome.ToString(), Detail = Hide(v, x.Detail), x.Seconds }),
            Sub = Sub(v), SubTotal = SubTotal(v), Blocked = blocked ? true : (bool?)null,
        };
        string Url(TargetVerdict v) => IsUserGroup(v.GroupId) ? L.T("core.addr.user_n", new { n = userNo[v.Target.Url] }) : v.Target.Url;
        IEnumerable<string> Detail(TargetVerdict v) => v.Probes.Select(x => Hide(v, $"{x.Tls}: {x.Outcome} {x.Detail}"));
        string Hide(TargetVerdict v, string d)
        {
            if (!IsUserGroup(v.GroupId)) return d;
            var host = Uri.TryCreate(v.Target.Url, UriKind.Absolute, out var u) ? u.Host : v.Target.Url;
            return d.Replace(v.Target.Url, "***").Replace(host, "***", StringComparison.OrdinalIgnoreCase);
        }
        static double Seconds(TargetVerdict v) => Math.Round(v.Ok ? v.BestSeconds : v.Probes.Select(p => p.Seconds).DefaultIfEmpty(0).Max(), 2);

        var doc = new
        {
            at = DateTime.Now,
            best = r.Best?.Strategy.Id,
            cancelled = r.Cancelled,
            baseline = r.Baseline.Select(v => new { v.GroupId, Url = Url(v), v.Ok, detail = Detail(v) }),
            // По адресам — для статистики по группам и «Подробнее» в списке стратегий (StrategyStats): группа, обязательный ли,
            // контрольный («Обычные сайты»), время и каждая версия TLS отдельно. detail — прежняя строка, для чтения глазами.
            scores = r.Scores.Select(s => new
            {
                s.Strategy.Id, s.GroupsPassed, s.GroupsTotal, s.TargetShare, s.AvgSeconds, s.Error,
                ControlBroken = s.Verdicts.Any(v => control.Contains(v.GroupId) && !v.Ok),
                // Blocked — адреса, закрытые по адресу (AddressBlocks): в оценку и статистику не входят, в списке — для показа.
                targets = s.Verdicts.Select(v => Entry(v, false)).Concat(s.Blocked.Select(v => Entry(v, true))),
            }),
        };
        File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return file;
    }

    /// <summary>«Очистить историю»: результаты подборов (а с ними статистика) и логи. Свои сайты и найденные стратегии не трогает.</summary>
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
