using DpiShell.Core.Localization;
using System.Text.Json.Nodes;

namespace DpiShell.Core;

/// <summary>
/// Совместная работа ПК и роутера, сторона ПК (docs/research/mixed-strategies.md §7, договор роутера §5.2 pc_link_*).
/// Состояние на ПК, которое хранит помощник: связка действует, и какие сайты ПК отдал роутеру («ведёт роутер»). Живёт в state.json.
/// </summary>
public sealed class JointConfig
{
    public bool Active { get; set; }
    /// <summary>Сайты ПК (свои сайты и строки продвинутого режима), которые ведёт роутер: на ПК они исключены и строки с ними не применяются.</summary>
    public List<string> Owned { get; set; } = [];
    /// <summary>До какого момента (UTC) связка действует без продления окном. Вышел — ПК работает по полной своей настройке (<see cref="BuildOptions.From"/>).</summary>
    public DateTime? Until { get; set; }
    /// <summary>Постоянный номер этого ПК для роутера (GUID), создаётся при первом обращении.</summary>
    public string? PcId { get; set; }

    public JointConfig Clone() => new() { Active = Active, Owned = [.. Owned], Until = Until, PcId = PcId };

    /// <summary>То, что меняет командную строку (срок и номер — нет).</summary>
    public bool SameBuild(JointConfig o) => Active == o.Active && Owned.Count == o.Owned.Count && Owned.Order(StringComparer.Ordinal).SequenceEqual(o.Owned.Order(StringComparer.Ordinal));

    public JsonObject ToJson() => new()
    {
        ["active"] = Active,
        ["owned"] = new JsonArray([.. Owned.Select(d => (JsonNode)d)]),
    };

    public static JointConfig FromJson(JsonNode? n)
    {
        var c = new JointConfig();
        if (n is not JsonObject o) return c;
        c.Active = o["active"]?.GetValue<bool>() ?? false;
        if (o["owned"] is JsonArray a) c.Owned = [.. a.Select(x => x?.GetValue<string>() ?? "").Where(AdvancedSites.IsDomain)];
        return c;
    }
}

/// <summary>Что в командной строке при действующей связке: сайты ПК, которые ведёт роутер.</summary>
public sealed record JointBuild(IReadOnlyList<string> Owned);

/// <summary>
/// Ответ <c>joint_get</c> (протокол помощника 1.9): что этот ПК готов взять на себя при совместной работе и действует ли связка.
/// Считает помощник — у него стратегии, фейки, свои сайты и строки продвинутого режима.
/// </summary>
/// <param name="PcId">Постоянный номер ПК для роутера.</param>
/// <param name="Active">Связка действует (ПК обрабатывает только своё).</param>
/// <param name="Owned">Сайты ПК, которые сейчас отданы роутеру.</param>
/// <param name="Own">Свои сайты ПК (список доменов).</param>
/// <param name="Rows">Сайты строк продвинутого режима (режим включён; в том числе строки «напрямую»).</param>
/// <param name="Decoys">Имена приманок ПК (см. <see cref="JointDecoys"/>).</param>
/// <param name="DecoysReliable">Имена приманок определены точно; иначе в <paramref name="Decoys"/> все имена фейков из поставки.</param>
/// <param name="DecoyNotes">Почему не точно (для журнала и отчёта).</param>
/// <param name="StrategyId">Стратегия ПК, по которой считались приманки.</param>
/// <param name="Enabled">Оптимизация на ПК включена.</param>
/// <param name="Work">Сайты, которые ПК реально оптимизирует при связке: свои сайты и строки со своей стратегией (без строк «напрямую»).</param>
/// <param name="Profiles">Профили программ («Игры и программы») — они остаются на ПК при связке.</param>
public sealed record JointInfo(string PcId, bool Active, IReadOnlyList<string> Owned, IReadOnlyList<string> Own, IReadOnlyList<string> Rows,
    IReadOnlyList<string> Decoys, bool DecoysReliable, IReadOnlyList<string> DecoyNotes, string? StrategyId, bool Enabled,
    IReadOnlyList<string> Work, int Profiles);

/// <summary>Правило «у адреса один хозяин»: какие имена считаются одним адресом, и разбор сайтов ПК на «свои» и «ведёт роутер».</summary>
public static class JointRules
{
    /// <summary>Сколько доменов роутер принимает в <c>pc_link_set</c>.</summary>
    public const int MaxDomains = 500;
    /// <summary>Сколько роутер хранит MAC.</summary>
    public const int MaxMacs = 8;
    /// <summary>Как часто окно продлевает связку на роутере (роутеру достаточно раза в сутки; договор §5.2 — «не реже раза в час»).</summary>
    public static readonly TimeSpan RenewEvery = TimeSpan.FromMinutes(30);
    /// <summary>Сколько помощник держит связку без продления окном (окно умерло, ноутбук ушёл из дома): потом ПК работает сам.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(150);

    /// <summary>
    /// Один адрес для роутера: сам домен, поддомен и надомен (как в <c>pc_link_set</c>, договор §5.2): youtube.com ~ m.youtube.com ~ youtube.com;
    /// www.google.com и play.google.com — разные адреса (общий родитель google.com им не мешает).
    /// </summary>
    public static bool Related(string a, string b) =>
        a == b || a.EndsWith("." + b, StringComparison.Ordinal) || b.EndsWith("." + a, StringComparison.Ordinal);

    public static bool OwnedBy(string domain, IEnumerable<string> routerNames) => routerNames.Any(n => Related(domain, n));

    /// <summary>Домены в нормальном виде (строчные, без повторов); негодные пропускаются. Имена приманок не обрезаются: «www.» в них значим.</summary>
    public static List<string> Clean(IEnumerable<string> names)
    {
        var list = new List<string>();
        foreach (var raw in names)
        {
            var n = raw?.Trim().ToLowerInvariant();
            if (n is not null && AdvancedSites.IsDomain(n) && !list.Contains(n)) list.Add(n);
        }
        return list;
    }

    /// <summary>
    /// Что отдать роутеру в <c>domains</c> и что роутер ведёт сам. Сайты ПК, совпавшие с адресами роутера (<paramref name="routerNames"/> — его
    /// встроенные списки YouTube и Discord из <c>lists_get</c> плюс то, что роутер уже вернул в <c>ignored</c>), в связку не идут и не применяются на ПК.
    /// Имена приманок идут первыми: без них связка не работает. Больше <see cref="MaxDomains"/> — лишние сайты отбрасываются.
    /// </summary>
    public static JointSelection Select(JointInfo pc, IEnumerable<string> routerNames)
    {
        var router = Clean(routerNames);
        var sites = Clean(pc.Own.Concat(pc.Rows));
        var routerOwned = sites.Where(s => OwnedBy(s, router)).ToList();
        var mine = sites.Where(s => !routerOwned.Contains(s)).ToList();
        var decoys = Clean(pc.Decoys);
        var gaps = decoys.Where(d => OwnedBy(d, router)).ToList();   // приманка совпала с адресом роутера: исключить её для ПК нельзя
        var send = decoys.Where(d => !gaps.Contains(d)).ToList();
        int room = Math.Max(0, MaxDomains - send.Count);
        var kept = mine.Take(room).ToList();
        send.AddRange(kept.Where(s => !send.Contains(s)));
        return new(send, routerOwned, kept, mine.Count - kept.Count, gaps);
    }
}

/// <summary>Итог <see cref="JointRules.Select"/>: <see cref="Send"/> — в <c>pc_link_set</c>; <see cref="RouterOwned"/> — «ведёт роутер»; <see cref="Mine"/> — сайты ПК.</summary>
public sealed record JointSelection(IReadOnlyList<string> Send, IReadOnlyList<string> RouterOwned, IReadOnlyList<string> Mine, int Trimmed, IReadOnlyList<string> DecoyGaps)
{
    /// <summary>Что роутер вернул в <c>ignored</c> — тоже его адреса. Пересчитывает сайты ПК.</summary>
    public JointSelection WithIgnored(IEnumerable<string> ignored)
    {
        var ign = Clean(ignored);
        if (ign.Count == 0) return this;
        var owned = RouterOwned.Concat(ign.Where(i => Mine.Contains(i))).Distinct().ToList();
        var gaps = DecoyGaps.Concat(ign.Where(i => Send.Contains(i) && !Mine.Contains(i))).Distinct().ToList();
        return this with { RouterOwned = owned, Mine = [.. Mine.Where(m => !ign.Contains(m))], DecoyGaps = gaps };
    }

    static List<string> Clean(IEnumerable<string> names) => JointRules.Clean(names);
}

/// <summary>Тексты совместной работы для окна и журнала. Без «обход» и без обещаний «у всех».</summary>
public static class JointTexts
{
    public static string NothingToDo => L.T("core.joint.nothing_to_do");
    public static string JournalTitle => L.T("core.joint.journal");
    public static string RouterJournalTitle => L.T("core.joint.router_journal");

    public static string Plural(int n) => L.Plural("core.joint.sites", n);

    /// <summary>«Роутер ведёт YouTube и Discord, этот ПК — свои сайты (3)».</summary>
    public static string Active(int mine) => L.T("core.joint.active", new { mine });

    public static string Fallback => L.T("core.joint.fallback");
    public static string FallbackDisabled => L.T("core.joint.fallback_disabled");
    public static string FallbackLogin => L.T("core.joint.fallback_login");
    public static string FallbackOld => L.T("core.joint.fallback_old");
    public static string FallbackNotInstalled => L.T("core.joint.fallback_helper");
    public static string FallbackRenew => L.T("core.joint.fallback_renew");
    public static string FallbackNoMac => L.T("core.joint.fallback_no_mac");
    public static string WaitingPc => L.T("core.joint.waiting_pc");
    public static string NoOwnSites => L.T("core.joint.no_own_sites");
    public static string Starting => L.T("core.joint.starting");
    public static string StartFailed => L.T("core.joint.start_failed");
}
