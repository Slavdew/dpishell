using System.Security;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

public static class SettingsApplier
{
    static bool Expected(Exception e) =>
        e is InvalidOperationException or IOException or SecurityException or InvalidDataException or ArgumentException or UnauthorizedAccessException or ConflictException;

    /// <summary>
    /// Записать план. Разделы идут по <see cref="SettingsSections.ApplyOrder"/>; сбой одного раздела (не поднялась оптимизация, hosts не принял) не мешает остальным,
    /// причина — в результате раздела. «До» записывается в журнал изменений до первой правки (<see cref="ISettingsTarget.BeginImport"/>).
    /// </summary>
    public static async Task<ImportResult> ApplyAsync(ImportPlan plan, ISettingsTarget target, CancellationToken ct = default)
    {
        var todo = plan.Sections.Where(s => s.Changes).ToList();
        if (todo.Count == 0) return new([], null);
        var rec = target.BeginImport(plan);
        var results = new List<SectionResult>();
        var ops = plan.Ops;
        var newIds = new Dictionary<string, string>();
        foreach (var id in SettingsSections.ApplyOrder)
        {
            var sp = todo.FirstOrDefault(s => s.Id == id);
            if (sp is null) continue;
            ct.ThrowIfCancellationRequested();
            int added = sp.Added, removed = sp.Removed, skipped = sp.Skipped;
            string? message = null;
            bool ok = true;
            try
            {
                switch (id)
                {
                    case SettingsSections.Sites: target.SetSites(ops.Sites!); break;
                    case SettingsSections.Exclude: target.SetExclude(ops.Exclude!); break;
                    case SettingsSections.Subnets:
                        if (ops.Subnets is not null) target.SetSubnets(ops.Subnets, false);
                        if (ops.SubnetsExclude is not null) target.SetSubnets(ops.SubnetsExclude, true);
                        break;
                    case SettingsSections.Strategies:
                        added = 0;
                        foreach (var s in ops.NewStrategies)
                        {
                            try { newIds[s.Name] = target.AddStrategy(s.Title, s.Roles); added++; }
                            catch (Exception e) when (Expected(e)) { skipped++; message ??= e.Message; }
                        }
                        ok = skipped == sp.Skipped;
                        break;
                    case SettingsSections.Apps:
                        removed = 0; added = 0;
                        foreach (var r in ops.RemoveApps) { target.RemoveApp(r); removed++; }
                        foreach (var a in ops.NewApps)
                        {
                            try { target.AddApp(a); added++; }
                            catch (Exception e) when (Expected(e)) { skipped++; message ??= e.Message; }
                        }
                        ok = skipped == sp.Skipped;
                        break;
                    case SettingsSections.Advanced:
                    {
                        var rows = new List<AdvancedRowInput>();
                        foreach (var r in ops.Advanced!.Rows)
                        {
                            var sid = r.StrategyId ?? (r.NewName is not null && newIds.TryGetValue(r.NewName, out var nid) ? nid : null);
                            if (sid is null) { skipped++; added = Math.Max(0, added - 1); continue; }   // стратегию не удалось сохранить — строка без неё не нужна
                            rows.Add(new(r.Domains, sid));
                        }
                        target.SetAdvanced(ops.Advanced.Enabled, ops.Advanced.Others, rows);
                        break;
                    }
                    case SettingsSections.Hosts:
                    {
                        var r = await target.SetHostsAsync([.. ops.Hosts!.Select(Entry)], ct);
                        added = r.Added.Count; removed = r.Removed.Count;
                        skipped += r.Rejected.Count;
                        if (r.Rejected.Count > 0) { message = string.Join("; ", r.Rejected.Take(5).Select(x => $"{x.Ip} {x.Host}: {x.Reason}")); }
                        break;
                    }
                    case SettingsSections.Dns:
                    {
                        var r = await target.SetDnsAsync(ops.Dns!, ct);
                        ok = r.Ok; message = r.Message;
                        if (!r.Ok) added = 0;
                        break;
                    }
                }
            }
            catch (Exception e) when (Expected(e))
            {
                ok = false; message = e.Message; added = 0; removed = 0;
            }
            results.Add(new(id, ok, added, removed, skipped, message));
        }
        var result = new ImportResult(results, rec);
        target.EndImport(rec, result);
        return new(results, rec);
    }

    static HostsEntry Entry(string line)
    {
        var p = line.Split(' ');
        return new(p[1], p[0]);
    }

    /// <summary>Заголовок записи журнала: «Загружены настройки: свои сайты, исключения…».</summary>
    public static string JournalTitle(ImportPlan plan) =>
        L.T("export.journal.title", new { sections = string.Join(", ", plan.Sections.Where(s => s.Changes).Select(s => s.Title.ToLowerInvariant())) });
}
