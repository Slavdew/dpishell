using System.Text.Json;
using System.Text.Json.Nodes;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Export;

/// <summary>
/// Откат загрузки настроек: возвращает прежние свои сайты, исключения и подсети (если сейчас стоит то, что поставила загрузка — иначе не трогаем),
/// убирает добавленные стратегии (кроме выбранной сейчас) и возвращает профили, которые «заменить» убрало. Новые профили, продвинутый режим, hosts и DNS
/// откатываются своими записями журнала, сделанными в той же загрузке.
/// </summary>
public sealed class SettingsImportUndoer(Shell shell) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.SettingsImport];

    static readonly JsonSerializerOptions Snap = new() { PropertyNameCaseInsensitive = true };

    static List<string> Strings(JsonNode? n) => n is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "")] : [];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var before = e.Before as JsonObject ?? [];
        var after = e.After as JsonObject ?? [];
        var restored = new List<string>();
        var changed = new List<string>();

        void Part(string key, string title, Func<IReadOnlyList<string>> now, Action<List<string>> restore)
        {
            if (before[key] is null) return;
            var was = Strings(before[key]);
            var cur = now();
            if (cur.SequenceEqual(was)) return;
            if (!cur.SequenceEqual(Strings(after[key]))) { changed.Add(title); return; }
            restore(was);
            restored.Add(title);
        }

        Part("sites", SettingsSections.Title(SettingsSections.Sites), () => shell.State.UserTargets, v => shell.SetUserTargets(v));
        Part("exclude", SettingsSections.Title(SettingsSections.Exclude), () => shell.State.UserExclude, v => shell.SetUserExclude(v));
        Part("subnets", SettingsSections.Title(SettingsSections.Subnets), () => shell.IpLists.Read(IpLists.UserFile), v => shell.WriteUserIpset(v, false, true));
        Part("subnets_exclude", L.T("export.undo.subnets_exclude"),
            () => shell.IpLists.Read(IpLists.UserExcludeFile), v => shell.WriteUserIpset(v, true, true));

        // Добавленные стратегии: выбранную сейчас и запасные не трогаем — на них ссылается работающая настройка.
        var ids = Strings(after["strategies"]);
        int removedStrategies = 0, keptStrategies = 0;
        foreach (var id in ids)
        {
            if (shell.State.StrategyId == id) { keptStrategies++; continue; }
            if (UserStrategies.Remove(shell.Paths, id)) { removedStrategies++; if (shell.State.Fallbacks.Remove(id)) shell.SaveState(); }
        }
        if (removedStrategies > 0) restored.Add(SettingsSections.Title(SettingsSections.Strategies));
        if (keptStrategies > 0) changed.Add(SettingsSections.Title(SettingsSections.Strategies));

        // Профили, убранные загрузкой («заменить»), возвращаются с теми же номерами.
        if (before["apps"] is JsonArray apps)
        {
            var back = apps.Select(n => n.Deserialize<AppProfile>(Snap)).OfType<AppProfile>()
                .Where(p => shell.State.AppProfiles.All(x => x.Id != p.Id)).ToList();
            if (back.Count > 0)
            {
                try { shell.ChangeAppProfiles([], back); restored.Add(SettingsSections.Title(SettingsSections.Apps)); }
                catch (InvalidOperationException) { changed.Add(SettingsSections.Title(SettingsSections.Apps)); }
            }
        }

        var skipped = changed.Count > 0 ? L.T("export.undo.changed", new { list = string.Join(", ", changed) }) : "";
        if (restored.Count > 0) return Task.FromResult(UndoOutcome.Restored(L.T("export.undo.restored", new { list = string.Join(", ", restored) }) + (skipped.Length > 0 ? "; " + skipped : "")));
        if (changed.Count > 0) return Task.FromResult(UndoOutcome.Changed(skipped));
        return Task.FromResult(UndoOutcome.Already(L.T("export.undo.already")));
    }
}
