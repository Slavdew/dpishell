using System.Text.Json;
using System.Text.Json.Nodes;
using DpiShell.Core.Export;

namespace DpiShell.Core.Helper;

/// <summary>Выгрузка и загрузка своих настроек (API 1.11): чтение закрытых данных для окна и применение проверенного плана с записью в журнал изменений.</summary>
public sealed partial class ShellHelperBackend
{
    public SettingsState SettingsExportRead()
    {
        var st = shell.State;
        var all = shell.AllStrategies;
        var s = new SettingsState
        {
            Sites = [.. st.UserTargets],
            SiteGroups = shell.UserGroupMap() is { Count: > 0 } groups ? groups : null,
            Exclude = [.. st.UserExclude],
            Strategies = [.. all],
            Advanced = st.Advanced.Clone(),
            // Профиль — с параметрами, которыми он идёт сейчас (у «По умолчанию» своих нет): пакет настроек несёт сами параметры.
            Apps = [.. st.AppProfiles.Select(shell.EffectiveProfile)],
            Subnets = [.. shell.IpLists.Read(IpLists.UserFile)],
            SubnetsExclude = [.. shell.IpLists.Read(IpLists.UserExcludeFile)],
            Variants = [.. shell.ProfileVariants()],
        };
        foreach (var r in s.Advanced.Rows) r.Resolved = null;   // итог подбора — не настройка человека
        try
        {
            s.Hosts = [.. shell.Hosts.File.Read().UserLines().Select(l => new HostsEntry(l.Host, l.Ip).ToString()).Distinct()];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s.Hosts = []; }
        try
        {
            if (Directory.Exists(shell.Paths.Fake)) s.FakeFiles = [.. Directory.GetFiles(shell.Paths.Fake).Select(f => Path.GetFileName(f)!)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        s.Dns = CurrentDnsChoice();
        return s;
    }

    /// <summary>Выбранный вариант DNS: готовый или свой (адреса с адаптеров). Не выставленный нами («other») и автоматический — не настройка, в выгрузку не идут.</summary>
    DnsChoice? CurrentDnsChoice()
    {
        try
        {
            var d = Dns.Status();
            if (d.Current is DnsPresets.Auto or "other" or "") return null;
            if (d.Current != DnsPresets.Custom) return new() { Choice = d.Current };
            var ips = d.Adapters.FirstOrDefault()?.Servers.Select(DnsValues.NormalizeIp).OfType<string>().Distinct().Take(DnsValues.MaxServers).ToList();
            return ips is { Count: > 0 } ? new() { Choice = DnsPresets.Custom, Ips = ips } : null;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { return null; }
    }

    public async Task<ImportResult> SettingsImportAsync(string request, CancellationToken ct)
    {
        var req = ImportRequest.TryParse(request, out var problem) ?? throw new InvalidOperationException(problem);
        var env = SettingsExportRead();
        // Окну не доверяем: те же правила, что при чтении чужих файлов, уже с учётом этого ПК (файлы фейков, варианты параметров).
        if (req.Verify(env) is { } bad) throw new InvalidOperationException(bad);
        var plan = SettingsPlanner.PlanData(req.Data, env, req.Sections, req.Mode);
        return await SettingsApplier.ApplyAsync(plan, new ImportTarget(this), ct);
    }

    /// <summary>Запись настроек в состояние ПК и журнал. Каждая правка идёт через те же методы, что и обычные кнопки окна.</summary>
    sealed class ImportTarget(ShellHelperBackend b) : ISettingsTarget
    {
        Shell Sh => b.ShellRef;
        readonly List<string> _addedStrategies = [];
        static readonly JsonSerializerOptions Snap = new() { PropertyNameCaseInsensitive = true };

        static JsonArray Arr(IEnumerable<string> s) => new([.. s.Select(x => (JsonNode)x)]);

        public string? BeginImport(ImportPlan plan)
        {
            var ops = plan.Ops;
            var before = new JsonObject();
            var after = new JsonObject();
            if (ops.Sites is not null) { before["sites"] = Arr(Sh.State.UserTargets); after["sites"] = Arr(ops.Sites); }
            // Группы своих сайтов: до и после, чтобы «Отменить загрузку» вернула и их.
            if (ops.Sites is not null && ops.SiteGroups is not null)
            {
                before["site_groups"] = GroupsNode(Sh.UserGroupMap());
                after["site_groups"] = GroupsNode(ops.SiteGroups);
            }
            if (ops.Exclude is not null) { before["exclude"] = Arr(Sh.State.UserExclude); after["exclude"] = Arr(ops.Exclude); }
            if (ops.Subnets is not null) { before["subnets"] = Arr(Sh.IpLists.Read(IpLists.UserFile)); after["subnets"] = Arr(ops.Subnets); }
            if (ops.SubnetsExclude is not null) { before["subnets_exclude"] = Arr(Sh.IpLists.Read(IpLists.UserExcludeFile)); after["subnets_exclude"] = Arr(ops.SubnetsExclude); }
            if (ops.RemoveApps.Count > 0)
                before["apps"] = new JsonArray([.. Sh.State.AppProfiles.Where(p => ops.RemoveApps.Contains(p.Id)).Select(p => JsonSerializer.SerializeToNode(p, Snap))]);
            if (ops.NewStrategies.Count > 0) { before["strategies"] = new JsonArray(); after["strategies"] = new JsonArray(); }
            // Свои строки hosts и продвинутый режим ведут одну открытую запись на всё время: если она была до загрузки, загрузка лишь правит её.
            // Прежнее — здесь, чтобы «Отменить загрузку» вернула ровно то, что было до неё.
            if (ops.Hosts is not null) before["hosts"] = Arr(UserHosts());
            if (ops.Advanced is not null) before["advanced"] = Sh.State.Advanced.ToJson();
            var id = Sh.Journal.Begin(ChangeKinds.SettingsImport, SettingsApplier.JournalTitle(plan), before, after);
            // Записи hosts, DNS, профилей и продвинутого режима, сделанные дальше в этой загрузке, — её часть (changes_undo_group).
            Sh.Journal.BeginGroup(id);
            return id;
        }

        IEnumerable<string> UserHosts()
        {
            try { return [.. Sh.Hosts.File.Read().UserLines().Select(l => new HostsEntry(l.Host, l.Ip).ToString()).Distinct()]; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        }

        public void EndImport(string? record, ImportResult result)
        {
            Sh.Journal.EndGroup();
            if (string.IsNullOrEmpty(record)) return;
            if (!result.Sections.Any(s => s.Ok && (s.Added > 0 || s.Removed > 0))) { Sh.Journal.Abort(record); return; }
            var entry = Sh.Journal.Active(ChangeKinds.SettingsImport).FirstOrDefault(e => e.Id == record);
            var after = entry?.After?.DeepClone() as JsonObject ?? [];
            if (_addedStrategies.Count > 0) after["strategies"] = Arr(_addedStrategies);
            if (entry?.Before?["hosts"] is not null) after["hosts"] = Arr(UserHosts());
            if (entry?.Before?["advanced"] is not null) after["advanced"] = Sh.State.Advanced.ToJson();
            Sh.Journal.Done(record, after);
        }

        public void SetSites(IReadOnlyList<string> sites) => Sh.SetUserTargets(sites);
        public void SetSites(IReadOnlyList<string> sites, IReadOnlyDictionary<string, string> groups) => Sh.SetUserTargetsGrouped(sites, groups);

        static JsonObject GroupsNode(IReadOnlyDictionary<string, string> map)
        {
            var o = new JsonObject();
            foreach (var (site, name) in map) o[site] = name;
            return o;
        }
        public void SetExclude(IReadOnlyList<string> sites) => Sh.SetUserExclude(sites);

        public void SetSubnets(IReadOnlyList<string> lines, bool exclude)
        {
            var v = Sh.WriteUserIpset(lines, exclude, confirmed: true);
            if (!v.Accepted) throw new InvalidOperationException(string.Join("; ", v.Errors));
        }

        public string AddStrategy(string title, IReadOnlyDictionary<string, string> roles)
        {
            var s = UserStrategies.AddPersonal(Sh.Paths, title, roles, "pasted");
            _addedStrategies.Add(s.Id);
            return s.Id;
        }

        public void RemoveApp(string id)
        {
            Sh.ChangeAppProfiles([id], []);
        }

        public void AddApp(ImportApp a)
        {
            var variants = new SettingsState { Variants = [.. Sh.ProfileVariants()] };
            var variant = SettingsClean.FindVariant(variants, a.Tcp, a.Udp, a.TcpDesync, a.UdpDesync)
                ?? throw new InvalidOperationException(Localization.L.T("export.apps.no_variant", new { title = a.Title }));
            Sh.SetAppProfile(null, a.Title, a.Tcp, a.Udp, a.Addresses, variant);
        }

        public void SetAdvanced(bool enabled, string others, IReadOnlyList<AdvancedRowInput> rows) => Sh.SetAdvanced(enabled, others, rows);

        public async Task<HostsUserResult> SetHostsAsync(IReadOnlyList<HostsEntry> entries, CancellationToken ct) => await b.HostsUserSetAsync(entries, ct);

        public Task<DnsSetResult> SetDnsAsync(DnsChoice dns, CancellationToken ct) =>
            b.DnsSetAsync(dns.Choice, dns.Choice == DnsPresets.Custom ? dns.Ips : null, dns.Choice == DnsPresets.Custom ? dns.Doh : null, fallback: true, ct);
    }

    Shell ShellRef => shell;
}
