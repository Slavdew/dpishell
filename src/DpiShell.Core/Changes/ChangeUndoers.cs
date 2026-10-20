using System.Text.Json.Nodes;

namespace DpiShell.Core;

/// <summary>Снимок DNS адаптеров для журнала: <c>{adapters:{id:{name, ipv4Auto, ipv4[], ipv6Auto, ipv6[], doh:{ip:{flags,template}}}}, addedDoh:[ip]}</c>.</summary>
public static class DnsSnapshot
{
    public static JsonObject ToJson(IEnumerable<NetAdapter> adapters, IReadOnlyDictionary<string, AdapterDns> dns, IReadOnlyList<string>? addedDoh)
    {
        var all = new JsonObject();
        foreach (var a in adapters)
        {
            var d = dns[a.Id];
            var doh = new JsonObject();
            foreach (var (ip, s) in d.Doh) doh[ip] = new JsonObject { ["flags"] = s.Flags.ToString(), ["template"] = s.Template };
            all[a.Id] = new JsonObject
            {
                ["name"] = a.Name, ["ipv4Auto"] = d.Ipv4Auto, ["ipv4"] = new JsonArray([.. d.Ipv4.Select(x => (JsonNode)x)]),
                ["ipv6Auto"] = d.Ipv6Auto, ["ipv6"] = new JsonArray([.. d.Ipv6.Select(x => (JsonNode)x)]), ["doh"] = doh,
            };
        }
        var root = new JsonObject { ["adapters"] = all };
        if (addedDoh is not null) root["addedDoh"] = new JsonArray([.. addedDoh.Select(x => (JsonNode)x)]);
        return root;
    }

    public static Dictionary<string, (string Name, AdapterDns Dns)> Adapters(JsonNode? node)
    {
        var map = new Dictionary<string, (string, AdapterDns)>();
        if (node?["adapters"] is not JsonObject all) return map;
        foreach (var (id, v) in all)
        {
            if (v is not JsonObject o) continue;
            List<string> List(string key) => o[key] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "")] : [];
            var doh = new Dictionary<string, DohSetting>();
            if (o["doh"] is JsonObject d)
                foreach (var (ip, s) in d)
                    doh[ip] = new DohSetting(ulong.TryParse(s?["flags"]?.GetValue<string>(), out var f) ? f : 0, s?["template"]?.GetValue<string>());
            map[id] = (o["name"]?.GetValue<string>() ?? id,
                new AdapterDns(o["ipv4Auto"]?.GetValue<bool>() ?? false, List("ipv4"), o["ipv6Auto"]?.GetValue<bool>() ?? false, List("ipv6"), doh));
        }
        return map;
    }

    public static List<string> AddedDoh(JsonNode? node) =>
        node?["addedDoh"] is JsonArray a ? [.. a.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0)] : [];

    /// <summary>Совпадают адреса серверов и «автоматически» (DoH-флаги Windows нормализует по-своему — не сравниваем).</summary>
    public static bool SameAddresses(AdapterDns a, AdapterDns b) =>
        a.Ipv4Auto == b.Ipv4Auto && a.Ipv6Auto == b.Ipv6Auto && a.Ipv4.SequenceEqual(b.Ipv4) && a.Ipv6.SequenceEqual(b.Ipv6, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Служба оптимизации: удалить, если стоит наша (с нашим winws). Чужая под тем же именем — не трогаем.</summary>
public sealed class ServiceUndoer(IServiceOps ops) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.Service];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        if (ops.State == SvcState.NotInstalled) return Task.FromResult(UndoOutcome.Already("службы уже нет"));
        var (exe, _) = ZapretDetector.SplitCommandLine(ops.ImagePath);
        var ours = e.After?["exe"]?.GetValue<string>();
        if (exe is null || ours is null || !exe.Equals(ours, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(UndoOutcome.Changed("служба с этим именем теперь запускает другую программу"));
        ops.Remove();
        return Task.FromResult(ops.State == SvcState.NotInstalled ? UndoOutcome.Restored("служба удалена")
            : UndoOutcome.Restored("служба удалена, пропадёт из списка после перезагрузки Windows"));
    }
}

/// <summary>Временные метки TCP: выключить, если сейчас включены (их включили мы).</summary>
public sealed class TcpTimestampsUndoer(ITcpTimestampsSystem sys, Action? restored = null) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.TcpTimestamps];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        switch (sys.IsEnabled())
        {
            case null: return Task.FromResult(UndoOutcome.Fail("не удалось узнать состояние настройки Windows"));
            case false: restored?.Invoke(); return Task.FromResult(UndoOutcome.Already("настройка уже выключена"));
        }
        if (!sys.Set(false)) return Task.FromResult(UndoOutcome.Fail("Windows не приняла выключение настройки"));
        restored?.Invoke();
        return Task.FromResult(UndoOutcome.Restored("настройка выключена, как до DpiShell"));
    }
}

/// <summary>DNS адаптеров: прежние адреса возвращаются адаптеру, у которого сейчас стоит то, что поставили мы.</summary>
public sealed class DnsUndoer(IDnsSystem sys) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.Dns];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var before = DnsSnapshot.Adapters(e.Before);
        var after = DnsSnapshot.Adapters(e.After);
        var now = sys.Adapters().ToDictionary(a => a.Id);
        var restored = new List<string>(); var same = new List<string>(); var changed = new List<string>();
        foreach (var (id, (name, was)) in before)
        {
            ct.ThrowIfCancellationRequested();
            if (!now.TryGetValue(id, out var a)) continue;   // адаптера больше нет — возвращать некуда
            var cur = sys.Read(a);
            if (DnsSnapshot.SameAddresses(cur, was)) same.Add(name);
            else if (after.TryGetValue(id, out var ours) && DnsSnapshot.SameAddresses(cur, ours.Dns)) { sys.Write(a, was); restored.Add(name); }
            else changed.Add(name);
        }
        if (restored.Count > 0)
        {
            if (changed.Count == 0) foreach (var ip in DnsSnapshot.AddedDoh(e.After)) sys.UnregisterDoh(ip);
            sys.FlushCache();
        }
        var skipped = changed.Count > 0 ? $"; не тронуто (DNS менялся после DpiShell): {string.Join(", ", changed)}" : "";
        if (restored.Count > 0) return Task.FromResult(UndoOutcome.Restored($"DNS как было: {string.Join(", ", restored)}{skipped}"));
        if (changed.Count > 0) return Task.FromResult(UndoOutcome.Changed($"DNS изменён после DpiShell: {string.Join(", ", changed)}"));
        return Task.FromResult(UndoOutcome.Already("DNS уже как раньше"));
    }
}

/// <summary>hosts: каждая наша строка узнаётся по пометке, поэтому откат вызывает готовые «Вернуть как было» и не задевает чужие строки.</summary>
public sealed class HostsUndoer(Func<HostsManager> hosts) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.HostsBlock, ChangeKinds.HostsUser, ChangeKinds.HostsComment];

    public async Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var m = hosts();
        switch (e.Kind)
        {
            case ChangeKinds.HostsBlock:
            {
                var w = m.Revert();
                return w.Changed ? UndoOutcome.Restored("блок DpiShell убран из hosts") : UndoOutcome.Already("блока DpiShell в hosts уже нет");
            }
            case ChangeKinds.HostsUser:
            {
                var r = await m.SetUserAsync([], ct);
                return r.Changed ? UndoOutcome.Restored("свои строки убраны из hosts") : UndoOutcome.Already("своих строк в hosts уже нет");
            }
            default:
            {
                var r = m.Uncomment();
                return r.Changed ? UndoOutcome.Restored($"возвращено строк: {r.Commented.Count}") : UndoOutcome.Already("закомментированных нами строк уже нет");
            }
        }
    }
}

/// <summary>Ядро оптимизации: файлы по манифесту удаляются, если они целы (хеши сходятся); изменённые — не трогаем.</summary>
public sealed class CoreUndoer(Func<CoreInstaller> installer, AppPaths paths) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.Core];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var inst = installer();
        var check = inst.Check();
        if (check.State == CoreState.Missing) return Task.FromResult(UndoOutcome.Already("ядра уже нет"));
        if (check.State == CoreState.Modified)
            return Task.FromResult(UndoOutcome.Changed("файлы ядра изменены после установки: " + string.Join(", ", check.BadFiles)));
        try
        {
            foreach (var name in inst.Manifest.Files.Keys) File.Delete(Path.Combine(paths.Core, name));
            var left = Directory.GetFileSystemEntries(paths.Core);
            if (left.Length == 0) Directory.Delete(paths.Core);
            return Task.FromResult(UndoOutcome.Restored(left.Length == 0 ? "ядро удалено" : "ядро удалено, чужие файлы в папке остались"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(UndoOutcome.Fail("файлы ядра заняты (оптимизация ещё работает?): " + ex.Message));
        }
    }
}

/// <summary>
/// Продвинутый режим «сайты → стратегия»: прежние строки и режим возвращаются, если сейчас стоит то, что поставили мы.
/// Изменили после нас (другой записью того же вида или руками в state.json) — не трогаем.
/// </summary>
public sealed class AdvancedUndoer(Func<AdvancedConfig> current, Action<AdvancedConfig> restore) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.Advanced];

    static string Describe(AdvancedConfig c) => c.Enabled ? $"включён, строк: {c.Rows.Count}" : $"выключен, строк: {c.Rows.Count}";

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var was = AdvancedConfig.FromJson(e.Before);
        var ours = AdvancedConfig.FromJson(e.After);
        var now = current();
        if (now.SameChoice(was)) return Task.FromResult(UndoOutcome.Already("продвинутый режим уже такой, как был"));
        if (!now.SameChoice(ours)) return Task.FromResult(UndoOutcome.Changed("продвинутый режим изменён после DpiShell"));
        restore(was);
        return Task.FromResult(UndoOutcome.Restored("продвинутый режим как был: " + Describe(was)));
    }
}

/// <summary>
/// Совместная работа с роутером на ПК: связка выключается, ПК работает по полной настройке. Если связка уже выключена (вышел срок, выключили кнопкой) —
/// «уже как было». Сама связка на роутере — запись окна (<see cref="ChangeKinds.RouterJoint"/>).
/// </summary>
public sealed class JointUndoer(Func<JointConfig> current, Action restoreOff) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.Joint];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        if (!current().Active) return Task.FromResult(UndoOutcome.Already("совместная работа уже выключена"));
        restoreOff();
        return Task.FromResult(UndoOutcome.Restored("совместная работа выключена: этот ПК работает по полной настройке"));
    }
}

/// <summary>Профиль программы: удаляется, если он такой, каким мы его записали.</summary>
public sealed class AppProfileUndoer(Func<string, AppProfile?> find, Func<string, bool> remove) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.AppProfile];

    public static JsonObject Snapshot(AppProfile p) => new()
    {
        ["id"] = p.Id, ["title"] = p.Title, ["tcp"] = p.TcpPorts, ["udp"] = p.UdpPorts,
        ["addresses"] = new JsonArray([.. p.Addresses.Select(a => (JsonNode)a)]),
    };

    public static bool Same(AppProfile p, JsonNode? s) =>
        s is not null && s["title"]?.GetValue<string>() == p.Title && s["tcp"]?.GetValue<string>() == p.TcpPorts && s["udp"]?.GetValue<string>() == p.UdpPorts
        && s["addresses"] is JsonArray a && a.Select(x => x?.GetValue<string>()).SequenceEqual(p.Addresses);

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var id = e.After?["id"]?.GetValue<string>() ?? "";
        if (find(id) is not { } p) return Task.FromResult(UndoOutcome.Already("профиля уже нет"));
        if (!Same(p, e.After)) return Task.FromResult(UndoOutcome.Changed("профиль изменён после записи"));
        return Task.FromResult(remove(id) ? UndoOutcome.Restored("профиль удалён") : UndoOutcome.Already("профиля уже нет"));
    }
}
