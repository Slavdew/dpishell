using System.Text.Json;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

public enum PlanAction { Add, Same, Remove, Skip }

/// <param name="Text">Запись для показа (сайт, название стратегии, строка hosts…).</param>
/// <param name="Reason">Почему пропущено (только у <see cref="PlanAction.Skip"/>).</param>
public sealed record PlanItem(PlanAction Action, string Text, string? Reason = null);

/// <summary>Что произойдёт с одним разделом: что добавится, что заменится (уйдёт), что уже есть, что пропущено.</summary>
public sealed class SectionPlan
{
    public string Id { get; init; } = "";
    public string Title => SettingsSections.Title(Id);
    public bool Risky { get; init; }
    public List<PlanItem> Items { get; } = [];
    public int Added => Items.Count(i => i.Action == PlanAction.Add);
    public int Removed => Items.Count(i => i.Action == PlanAction.Remove);
    public int Same => Items.Count(i => i.Action == PlanAction.Same);
    public int Skipped => Items.Count(i => i.Action == PlanAction.Skip);
    public bool Changes => Added > 0 || Removed > 0;
}

sealed record AdvancedFinalRow(List<string> Domains, string? StrategyId, string? NewName);
sealed record AdvancedFinal(bool Enabled, string Others, List<AdvancedFinalRow> Rows);

/// <summary>Что именно записать: итоговые списки и операции. Строится только из проверенных данных и своего состояния.</summary>
sealed class PlanOps
{
    public List<string>? Sites, Exclude, Subnets, SubnetsExclude, Hosts;
    /// <summary>Группы итогового списка своих сайтов (домен → имя); null — пакет групп не принёс, прежние группы остаются у оставшихся сайтов.</summary>
    public Dictionary<string, string>? SiteGroups;
    public List<ImportStrategy> NewStrategies = [];
    public AdvancedFinal? Advanced;
    public List<ImportApp> NewApps = [];
    public List<string> RemoveApps = [];
    public DnsChoice? Dns;
}

/// <summary>Запрос помощнику <c>settings_import</c>: режим, выбранные разделы и проверенные данные. Помощник пересчитывает план сам.</summary>
public sealed class ImportRequest
{
    public ImportMode Mode { get; set; }
    public List<string> Sections { get; set; } = [];
    public ImportData Data { get; set; } = new();

    /// <summary>Разобрать запрос строго; null и причина — если это не запрос (лишние и повторные поля, чужие разделы).</summary>
    public static ImportRequest? TryParse(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > SettingsSections.MaxRequestChars) { problem = L.T("export.req.bad"); return null; }
        try
        {
            var r = JsonSerializer.Deserialize<ImportRequest>(json, SettingsSections.Json);
            if (r is null) { problem = L.T("export.req.bad"); return null; }
            r.Sections ??= []; r.Data ??= new();
            return r;
        }
        catch (JsonException) { problem = L.T("export.req.bad"); return null; }
    }

    /// <summary>
    /// Проверка плана, который прислало окно: разделы известны, данных вне выбранных разделов нет, и каждая запись проходит те же правила, что и при чтении
    /// чужих файлов, без единой правки. Подделанный план (лишняя запись, неразрешённый параметр стратегии, домен с пробелом) — отказ целиком.
    /// <paramref name="env"/> — сведения этого ПК (фейки, варианты); null — только по самим данным.
    /// </summary>
    public string? Verify(SettingsState? env)
    {
        if (!Enum.IsDefined(Mode)) return L.T("export.req.bad");
        if (Sections.Count == 0 || Sections.Any(s => !SettingsSections.IsKnown(s)) || Sections.Distinct().Count() != Sections.Count) return L.T("export.req.sections");
        if (Data.Only(Sections).Canon() != Data.Canon()) return L.T("export.req.extra");
        var problems = new List<SectionProblem>();
        var clean = SettingsClean.Clean(Data, env, problems);
        if (problems.Count > 0) return L.T("export.req.altered", new { reason = problems[0].ToString() });
        if (clean.Canon() != Data.Canon()) return L.T("export.req.altered", new { reason = L.T("export.req.not_canonical") });
        return null;
    }
}

/// <summary>Предпросмотр и состав загрузки. Применяется через <see cref="SettingsImport.ApplyAsync"/> (помощник — по <see cref="ToRequest"/>).</summary>
public sealed class ImportPlan
{
    public ImportMode Mode { get; init; }
    public IReadOnlyList<SectionPlan> Sections { get; init; } = [];
    /// <summary>Проверенные данные выбранных разделов — ровно то, что уйдёт помощнику.</summary>
    public ImportData Data { get; init; } = new();
    /// <summary>Записи, отброшенные при сверке с этим ПК (нет файла фейка, нет подходящего варианта параметров…).</summary>
    public IReadOnlyList<SectionProblem> Problems { get; init; } = [];
    internal PlanOps Ops { get; init; } = new();

    public bool HasChanges => Sections.Any(s => s.Changes);

    public SectionPlan? Section(string id) => Sections.FirstOrDefault(s => s.Id == id);

    /// <summary>Запрос помощнику (JSON): разделы с изменениями, данные только по ним.</summary>
    public string ToRequest(bool groups = true)
    {
        var ids = Sections.Where(s => s.Changes).Select(s => s.Id).ToList();
        var data = Data.Only(ids);
        if (!groups) data.SiteGroups = null;   // помощник до 1.18 лишних полей не принимает
        return JsonSerializer.Serialize(new ImportRequest { Mode = Mode, Sections = ids, Data = data }, SettingsSections.Json);
    }
}

public sealed record SectionResult(string Id, bool Ok, int Added, int Removed, int Skipped, string? Message = null);

/// <param name="JournalId">Запись журнала изменений («Отменить всё» вернёт прежнее); null — менять было нечего.</param>
public sealed record ImportResult(IReadOnlyList<SectionResult> Sections, string? JournalId)
{
    public bool AllOk => Sections.All(s => s.Ok);
}

/// <summary>Что умеет записывать загрузку настроек. В помощнике — над настоящим состоянием ПК, в тестах — подмена. Всё, что сюда приходит, уже проверено.</summary>
public interface ISettingsTarget
{
    /// <summary>Записать в журнал изменений «до» и вернуть id записи (null — журнал не ведётся).</summary>
    string? BeginImport(ImportPlan plan);
    void EndImport(string? record, ImportResult result);
    void SetSites(IReadOnlyList<string> sites);
    /// <summary>Свои сайты с группами (домен → имя; остальные — «Прочее»). Цель без групп (подмена в тестах) пишет плоский список.</summary>
    void SetSites(IReadOnlyList<string> sites, IReadOnlyDictionary<string, string> groups) => SetSites(sites);
    void SetExclude(IReadOnlyList<string> sites);
    void SetSubnets(IReadOnlyList<string> lines, bool exclude);
    /// <summary>Сохранить стратегию как вставленную; возвращает её новый id.</summary>
    string AddStrategy(string title, IReadOnlyDictionary<string, string> roles);
    void RemoveApp(string id);
    void AddApp(ImportApp app);
    void SetAdvanced(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows);
    Task<HostsUserResult> SetHostsAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct);
    Task<DnsSetResult> SetDnsAsync(DnsChoice dns, CancellationToken ct);
}

public static class SettingsPlanner
{
    /// <summary>
    /// Что будет, если загрузить выбранные разделы пакета в <paramref name="mode"/>. Сверяется с <paramref name="current"/> — своими данными на этом ПК.
    /// Разделы без записей и пропущенные при чтении не планируются.
    /// </summary>
    public static ImportPlan Plan(ParsedPackage package, SettingsState current, IEnumerable<string> sections, ImportMode mode) =>
        PlanData(package.Data, current, sections, mode, package.Ok);

    /// <summary>То же по проверенным данным (помощник строит план из запроса окна).</summary>
    public static ImportPlan PlanData(ImportData data, SettingsState current, IEnumerable<string> sections, ImportMode mode, bool packageOk = true)
    {
        var chosen = packageOk ? sections.Where(SettingsSections.IsKnown).Distinct().Where(data.Has).ToList() : [];
        var problems = new List<SectionProblem>();
        var clean = SettingsClean.Clean(data.Only(chosen), current, problems);
        // Стратегии, на которые ссылаются «свои настройки для сайтов», сверяются со всем пакетом (даже если раздел стратегий не выбран).
        var allStrategies = SettingsClean.Strategies(data.Strategies ?? [], current, []);
        var ops = new PlanOps();
        var plans = new List<SectionPlan>();
        foreach (var id in SettingsSections.All.Where(chosen.Contains))
        {
            var sp = new SectionPlan { Id = id, Risky = SettingsSections.IsRisky(id, clean.Dns) };
            if (clean.Count(id) == 0)
            {
                // Ни одной принятой записи: «заменить» не должно стирать свои настройки ради пустоты — раздел только сообщает, что пропущено.
                foreach (var p in problems.Where(p => p.Section == id)) sp.Items.Add(new(PlanAction.Skip, p.File ?? "", p.Reason));
                plans.Add(sp);
                continue;
            }
            switch (id)
            {
                case SettingsSections.Sites:
                    ops.Sites = Merge(sp, current.Sites, clean.Sites!, SettingsSections.MaxSites, mode, x => x);
                    ops.SiteGroups = MergeGroups(current, clean.SiteGroups, ops.Sites, mode);
                    break;
                case SettingsSections.Exclude: ops.Exclude = Merge(sp, current.Exclude, clean.Exclude!, SettingsSections.MaxExclude, mode, x => x); break;
                case SettingsSections.Hosts: ops.Hosts = Merge(sp, current.Hosts, clean.Hosts!, SettingsSections.MaxHosts, mode, x => x); break;
                case SettingsSections.Subnets:
                    if (clean.Subnets is not null) ops.Subnets = Merge(sp, current.Subnets, clean.Subnets, SettingsSections.MaxSubnets, mode, x => x);
                    if (clean.SubnetsExclude is not null)
                        ops.SubnetsExclude = Merge(sp, current.SubnetsExclude, clean.SubnetsExclude, SettingsSections.MaxSubnets, mode, x => L.T("export.item.excluded", new { text = x }));
                    break;
                case SettingsSections.Strategies: PlanStrategies(sp, current, clean.Strategies!, ops); break;
                case SettingsSections.Apps: PlanApps(sp, current, clean.Apps!, mode, ops); break;
                case SettingsSections.Advanced: PlanAdvanced(sp, current, clean.Advanced!, allStrategies, ops, mode); break;
                case SettingsSections.Dns: PlanDns(sp, current, clean.Dns!, ops); break;
            }
            foreach (var p in problems.Where(p => p.Section == id)) sp.Items.Add(new(PlanAction.Skip, p.File ?? "", p.Reason));
            plans.Add(sp);
        }
        return new() { Mode = mode, Sections = plans, Data = clean, Problems = problems, Ops = ops };
    }

    /// <summary>
    /// Группы итогового списка. «Добавить» — свои сайты остаются в своих группах, новые берут группу из пакета; «заменить» — как в пакете.
    /// Пакет групп не принёс — null (прежние группы остаются у сайтов, что остались).
    /// </summary>
    static Dictionary<string, string>? MergeGroups(SettingsState current, IReadOnlyDictionary<string, string>? incoming, IReadOnlyList<string> final, ImportMode mode)
    {
        if (incoming is not { Count: > 0 }) return null;
        var res = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var site in final)
        {
            if (mode == ImportMode.Add && current.SiteGroups is { } mine && mine.TryGetValue(site, out var own)) res[site] = own;
            else if (incoming.TryGetValue(site, out var name)) res[site] = name;
        }
        return res;
    }

    /// <summary>Слияние списка: «добавить» — свои остаются и дополняются, «заменить» — остаётся только пришедшее. Итог — в возврате.</summary>
    static List<string> Merge(SectionPlan sp, IReadOnlyList<string> existing, IReadOnlyList<string> incoming, int cap, ImportMode mode, Func<string, string> show)
    {
        var have = new HashSet<string>(existing, StringComparer.Ordinal);
        var final = mode == ImportMode.Add ? new List<string>(existing) : [];
        var inSet = new HashSet<string>(incoming, StringComparer.Ordinal);
        foreach (var x in incoming)
        {
            bool known = have.Contains(x);
            if (final.Count >= cap && !(mode == ImportMode.Add && known))
            { sp.Items.Add(new(PlanAction.Skip, show(x), L.T("export.item.limit", new { max = cap }))); continue; }
            if (known) { sp.Items.Add(new(PlanAction.Same, show(x))); if (mode == ImportMode.Replace) final.Add(x); }
            else { sp.Items.Add(new(PlanAction.Add, show(x))); final.Add(x); }
        }
        if (mode == ImportMode.Replace)
            foreach (var x in existing.Where(x => !inSet.Contains(x))) sp.Items.Add(new(PlanAction.Remove, show(x)));
        return final;
    }

    static bool RolesEqual(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    // Стратегии не удаляются загрузкой ни в каком режиме (выбранная и запасные ссылаются на них): «заменить» для них — то же, что «добавить».
    static void PlanStrategies(SectionPlan sp, SettingsState cur, List<ImportStrategy> incoming, PlanOps ops)
    {
        int personal = cur.Strategies.Count(s => UserStrategies.IsPersonal(s.Source)), added = 0;
        foreach (var s in incoming)
        {
            if (cur.Strategies.Any(x => RolesEqual(x.Roles, s.Roles))) { sp.Items.Add(new(PlanAction.Same, s.Title)); continue; }
            if (personal + added >= SettingsSections.MaxStrategies)
            { sp.Items.Add(new(PlanAction.Skip, s.Title, L.T("export.item.limit", new { max = SettingsSections.MaxStrategies }))); continue; }
            sp.Items.Add(new(PlanAction.Add, s.Title));
            ops.NewStrategies.Add(s);
            added++;
        }
    }

    static string AppKey(string title, string tcp, string udp, IEnumerable<string> addresses) =>
        title + "|" + tcp + "|" + udp + "|" + string.Join(',', addresses.Order(StringComparer.Ordinal));

    static void PlanApps(SectionPlan sp, SettingsState cur, List<ImportApp> incoming, ImportMode mode, PlanOps ops)
    {
        var curKeys = cur.Apps.ToDictionary(a => a.Id, a => AppKey(a.Title, a.TcpPorts, a.UdpPorts, a.Addresses));
        var inKeys = incoming.Select(a => AppKey(a.Title, a.Tcp, a.Udp, a.Addresses)).ToHashSet();
        int count = cur.Apps.Count;
        if (mode == ImportMode.Replace)
        {
            foreach (var a in cur.Apps.Where(a => !inKeys.Contains(curKeys[a.Id])))
            { sp.Items.Add(new(PlanAction.Remove, a.Title)); ops.RemoveApps.Add(a.Id); count--; }
        }
        var have = curKeys.Values.ToHashSet();
        foreach (var a in incoming)
        {
            if (have.Contains(AppKey(a.Title, a.Tcp, a.Udp, a.Addresses))) { sp.Items.Add(new(PlanAction.Same, a.Title)); continue; }
            if (count >= SettingsSections.MaxApps)
            { sp.Items.Add(new(PlanAction.Skip, a.Title, L.T("export.item.limit", new { max = SettingsSections.MaxApps }))); continue; }
            sp.Items.Add(new(PlanAction.Add, a.Title));
            ops.NewApps.Add(a);
            count++;
        }
    }

    static string RowText(IEnumerable<string> domains, string strategy)
    {
        var d = domains.ToList();
        return $"{string.Join(", ", d.Take(3))}{(d.Count > 3 ? "…" : "")} → {strategy}";
    }

    static void PlanAdvanced(SectionPlan sp, SettingsState cur, ImportAdvanced inc, List<ImportStrategy> allPackageStrategies, PlanOps ops, ImportMode mode)
    {
        // Новые стратегии выбранного раздела (PlanStrategies уже прошёл) и одинаковые по параметрам уже имеющиеся — на них можно сослаться.
        var newNames = ops.NewStrategies.ToDictionary(s => s.Name, s => s.Title);
        string TitleOf(string? id) => cur.Strategies.FirstOrDefault(s => s.Id == id)?.Title ?? id ?? "";
        string Key(List<string> d, string? sid, string? nn) => string.Join(',', d) + "|" + (sid ?? "file:" + nn);

        var incoming = new List<(AdvancedFinalRow Row, string Text)>();
        var skips = new List<PlanItem>();
        foreach (var r in inc.Rows)
        {
            string? id = r.Strategy, newName = null;
            string label = r.Strategy ?? "";
            if (r.StrategyFile is not null)
            {
                var pkg = allPackageStrategies.FirstOrDefault(s => s.Name == r.StrategyFile);
                var existing = pkg is null ? null : cur.Strategies.FirstOrDefault(x => RolesEqual(x.Roles, pkg.Roles));
                if (existing is not null) { id = existing.Id; label = existing.Title; }
                else if (pkg is not null && newNames.ContainsKey(pkg.Name)) { newName = pkg.Name; label = pkg.Title; }
                else { skips.Add(new(PlanAction.Skip, RowText(r.Domains, r.StrategyFile), L.T("export.advanced.no_strategy", new { name = r.StrategyFile }))); continue; }
            }
            else label = TitleOf(id);
            incoming.Add((new(r.Domains, id, newName), RowText(r.Domains, label)));
        }

        var curRows = cur.Advanced.Rows.Select(r => new AdvancedFinalRow([.. r.Domains], r.Strategy, null)).ToList();
        var final = mode == ImportMode.Add ? new List<AdvancedFinalRow>(curRows) : [];
        var finalKeys = final.Select(r => Key(r.Domains, r.StrategyId, r.NewName)).ToHashSet();
        var curKeys = curRows.Select(r => Key(r.Domains, r.StrategyId, r.NewName)).ToHashSet();
        foreach (var (row, text) in incoming)
        {
            var key = Key(row.Domains, row.StrategyId, row.NewName);
            if (finalKeys.Contains(key)) { sp.Items.Add(new(PlanAction.Same, text)); continue; }
            if (final.Count >= AdvancedSites.MaxRows) { sp.Items.Add(new(PlanAction.Skip, text, L.T("export.item.limit", new { max = AdvancedSites.MaxRows }))); continue; }
            sp.Items.Add(new(curKeys.Contains(key) ? PlanAction.Same : PlanAction.Add, text));
            final.Add(row); finalKeys.Add(key);
        }
        sp.Items.AddRange(skips);
        if (mode == ImportMode.Replace)
            foreach (var r in curRows.Where(r => !finalKeys.Contains(Key(r.Domains, r.StrategyId, r.NewName))))
                sp.Items.Add(new(PlanAction.Remove, RowText(r.Domains, TitleOf(r.StrategyId))));

        // Режим работы: «заменить» берёт режим пакета; «добавить» оставляет свой, а если своих строк нет и режим выключен — берёт режим пакета.
        bool useIncoming = mode == ImportMode.Replace || cur.Advanced.Rows.Count == 0 && !cur.Advanced.Enabled;
        var enabled = useIncoming ? inc.Enabled : cur.Advanced.Enabled;
        var others = useIncoming ? inc.Others : cur.Advanced.Others;
        ops.Advanced = new(enabled, others, final);
        // Сменился только режим при тех же строках — тоже изменение.
        if (!sp.Changes && final.Count > 0 && (enabled != cur.Advanced.Enabled || others != cur.Advanced.Others))
            sp.Items.Add(new(PlanAction.Add, L.T("export.advanced.mode", new { state = enabled ? L.T("export.on") : L.T("export.off") })));
    }

    static void PlanDns(SectionPlan sp, SettingsState cur, DnsChoice inc, PlanOps ops)
    {
        // Выбор DNS один: «добавить» и «заменить» совпадают — ставится пришедший вариант, если он не тот, что уже стоит.
        if (inc.Same(cur.Dns)) { sp.Items.Add(new(PlanAction.Same, inc.Describe())); return; }
        sp.Items.Add(new(PlanAction.Add, inc.Describe()));
        ops.Dns = inc;
    }
}
