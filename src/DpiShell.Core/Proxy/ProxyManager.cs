using System.Text.Json;
using System.Text.Json.Nodes;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Итог изменения прокси. <see cref="Changed"/> — что-то записано в реестр.</summary>
public sealed record ProxyOutcome(bool Ok, string Message, bool Changed = false);

/// <summary>«Прежний» прокси машины до «Очистить» — для «Вернуть прежний». WinHttp — только если его тоже меняли.</summary>
public sealed record ProxyBackup(ProxySettings Machine, WinHttpInfo? WinHttp);

/// <summary>Прежний прокси машины в закрытой папке помощника (<c>proxy-backup.json</c>).</summary>
public sealed class ProxyBackupStore(string file)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ProxyBackup? Load()
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<ProxyBackup>(File.ReadAllText(file), Json) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(ProxyBackup b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(b, Json));
        File.Move(tmp, file, overwrite: true);
    }

    public void Forget()
    {
        try { File.Delete(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Снимки прокси для журнала изменений: <c>{enabled, server, bypass, pac, sid?, perUser?, winhttp?}</c>.</summary>
public static class ProxySnapshot
{
    public static JsonObject ToJson(ProxySettings s, string? sid = null, bool trackPerUser = false, int? perUser = null, WinHttpInfo? winHttp = null)
    {
        var o = new JsonObject { ["enabled"] = s.Enabled, ["server"] = s.Server, ["bypass"] = s.Bypass, ["pac"] = s.Pac };
        if (sid is not null) o["sid"] = sid;
        if (trackPerUser) o["perUser"] = perUser;
        if (winHttp is not null) o["winhttp"] = new JsonObject { ["proxy"] = winHttp.Proxy, ["bypass"] = winHttp.Bypass };
        return o;
    }

    static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static ProxySettings Settings(JsonNode? n) =>
        new(n?["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b) && b, S(n?["server"]), S(n?["bypass"]), S(n?["pac"]));

    public static string? Sid(JsonNode? n) => S(n?["sid"]);
    public static bool HasPerUser(JsonNode? n) => n is JsonObject o && o.ContainsKey("perUser");
    public static int? PerUser(JsonNode? n) => n?["perUser"] is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
    public static WinHttpInfo? WinHttp(JsonNode? n) =>
        n?["winhttp"] is JsonObject w ? new(S(w["proxy"]), S(w["bypass"])) : null;

    /// <summary>Пометка в «было»: значения ProxyEnable до DpiShell не было — откат удаляет его, а не пишет 0 (без остатков своего).</summary>
    public const string EnableAbsent = "enableAbsent";

    public static bool NoEnableBefore(JsonNode? before) => before?[EnableAbsent] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Сравнение снимков значений; пометка <see cref="EnableAbsent"/> — не значение и не сравнивается.</summary>
    public static bool Same(JsonNode? a, JsonNode? b) => Text(a) == Text(b);

    static string Text(JsonNode? n)
    {
        if (n is not JsonObject o || !o.ContainsKey(EnableAbsent)) return n?.ToJsonString() ?? "";
        var c = (JsonObject)o.DeepClone();
        c.Remove(EnableAbsent);
        return c.ToJsonString();
    }
}

/// <summary>
/// Изменение системного прокси WinINET (и WinHTTP) с записью в журнал ДО действия и проверкой результата чтением обратно.
/// Для пользователя (HKCU окна или HKU\SID у помощника) и для машины (HKLM + политика ProxySettingsPerUser=0 + по желанию WinHTTP).
/// Повторные правки одного и того же склеиваются в одну запись журнала (прежнее значение — самое первое).
/// </summary>
public sealed class ProxyManager(IProxyRegistry reg, IWinHttpProxy winHttp)
{
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;
    /// <summary>Хранилище «прежнего» прокси машины; у окна (пользователь) не нужно — оно хранит в настройках.</summary>
    public ProxyBackupStore? Backup { get; set; }
    public IProxyNotifier Notifier { get; set; } = new NullProxyNotifier();

    static bool IsIo(Exception e) => e is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException;

    public ProxyOverview Overview(string? sid = null) => ProxyOverview.Read(reg, winHttp, sid);

    // ---------- пользователь ----------

    /// <param name="sid">null — HKCU (окно); иначе HKU\SID (помощник по команде окна).</param>
    /// <param name="kind"><see cref="ChangeKinds.ProxyUser"/> для окна, <see cref="ChangeKinds.ProxyUserAdmin"/> для помощника.</param>
    public ProxyOutcome ApplyUser(string? sid, ProxySettings next, string kind)
    {
        if (sid is not null && !reg.UserHiveLoaded(sid)) return new(false, L.T("proxy.out.hive"));
        var before = ProxyRegistryIo.Read(reg, ProxyKey.UserSettings, sid);
        if (before == next) return new(true, L.T("proxy.out.already"));
        var error = Journal(kind, Title(kind, next), sid, ProxySnapshot.ToJson(before, sid), ProxySnapshot.ToJson(next, sid), EnableAbsent(ProxyKey.UserSettings, sid),
            () => ProxySnapshot.ToJson(ProxyRegistryIo.Read(reg, ProxyKey.UserSettings, sid), sid),
            () => ProxyRegistryIo.Write(reg, ProxyKey.UserSettings, sid, next));
        Notifier.Changed();
        var now = ProxyRegistryIo.Read(reg, ProxyKey.UserSettings, sid);
        if (now != next) return new(false, error is null ? L.T("proxy.out.rejected") : L.T("proxy.out.write_failed", new { error }), now != before);
        return new(true, next.IsOn ? L.T("proxy.out.user_on") : L.T("proxy.out.user_off"), true);
    }

    static string Title(string kind, ProxySettings next) => (kind, next.IsOn) switch
    {
        (ChangeKinds.ProxyMachine, true) => L.T("core.proxy.title.machine_on"),
        (ChangeKinds.ProxyMachine, false) => L.T("core.proxy.title.machine_off"),
        (_, true) => L.T("core.proxy.title.user_on"),
        _ => L.T("core.proxy.title.user_off"),
    };

    // ---------- вся машина ----------

    /// <param name="winhttp">Что поставить в WinHTTP (для служб); null — не трогать.</param>
    public ProxyOutcome ApplyMachine(ProxySettings next, WinHttpInfo? winhttp)
    {
        var before = ProxyRegistryIo.Read(reg, ProxyKey.MachineSettings);
        var beforePer = ProxyRegistryIo.PerUserPolicy(reg);
        var beforeW = winHttp.Get();
        // Включаем — настройки общие для всех (ProxySettingsPerUser=0); выключаем — политику оставляем как есть.
        int? nextPer = next.IsOn ? 0 : beforePer;
        if (before == next && beforePer == nextPer && (winhttp is null || beforeW == winhttp)) return new(true, L.T("proxy.out.already"));

        bool trackW = winhttp is not null;
        JsonObject Snap(ProxySettings s, int? per, WinHttpInfo? w) => ProxySnapshot.ToJson(s, null, true, per, trackW ? w : null);
        var error = Journal(ChangeKinds.ProxyMachine, Title(ChangeKinds.ProxyMachine, next), null, Snap(before, beforePer, beforeW), Snap(next, nextPer, winhttp),
            EnableAbsent(ProxyKey.MachineSettings, null),
            () => Snap(ProxyRegistryIo.Read(reg, ProxyKey.MachineSettings), ProxyRegistryIo.PerUserPolicy(reg), winHttp.Get()),
            () =>
            {
                ProxyRegistryIo.Write(reg, ProxyKey.MachineSettings, null, next);
                if (ProxyRegistryIo.PerUserPolicy(reg) != nextPer) ProxyRegistryIo.SetPerUserPolicy(reg, nextPer);
                if (winhttp is not null && !winHttp.Set(winhttp)) throw new IOException(L.T("core.proxy.winhttp_refused"));
            });
        Notifier.Changed();

        var nowSet = ProxyRegistryIo.Read(reg, ProxyKey.MachineSettings);
        var nowW = winHttp.Get();
        bool ok = nowSet == next && ProxyRegistryIo.PerUserPolicy(reg) == nextPer && (winhttp is null || nowW == winhttp);
        if (!ok) return new(false, error is null ? L.T("proxy.out.rejected") : L.T("proxy.out.write_failed", new { error }), true);

        if (!next.IsOn && before.IsOn) Backup?.Save(new ProxyBackup(before, trackW && !beforeW.IsDirect ? beforeW : null));
        return new(true, next.IsOn ? L.T("proxy.out.machine_on") : L.T("proxy.out.machine_off"), true);
    }

    /// <summary>«Вернуть прежний»: поставить то, что было до последнего «Очистить» (и WinHTTP, если его тоже меняли).</summary>
    public ProxyOutcome RestoreMachine()
    {
        var b = Backup?.Load();
        if (b is null) return new(false, L.T("proxy.out.no_previous"));
        var r = ApplyMachine(b.Machine, b.WinHttp);
        if (r.Ok) Backup!.Forget();
        return r;
    }

    // ---------- журнал ----------

    bool EnableAbsent(ProxyKey key, string? sid)
    {
        try { return reg.Get(key, sid, ProxyNames.Enable) is null; }
        catch (Exception e) when (IsIo(e)) { return false; }
    }

    /// <summary>
    /// Запись ДО действия; итог — по факту (read-back). Тот же объект правки повторно — одна запись (первое «до» сохраняется).
    /// <paramref name="enableAbsent"/> — значения ProxyEnable ещё нет: в записи помечается, откат его удалит.
    /// </summary>
    string? Journal(string kind, string title, string? sid, JsonObject before, JsonObject planned, bool enableAbsent, Func<JsonObject> snapshot, Action act)
    {
        var open = Recorder.Active(kind).LastOrDefault(e => ProxySnapshot.Sid(e.After) == sid && ProxySnapshot.Same(e.After, before));
        var stored = before;
        if (open is null && enableAbsent) { stored = (JsonObject)before.DeepClone(); stored[ProxySnapshot.EnableAbsent] = true; }
        string id = open?.Id ?? Recorder.Begin(kind, title, stored, planned);
        string? error = null;
        try { act(); }
        catch (Exception e) when (IsIo(e)) { error = string.IsNullOrWhiteSpace(e.Message) ? L.T("core.proxy.write_error") : e.Message; }
        var actual = snapshot();
        if (open is null)
        {
            if (ProxySnapshot.Same(actual, before)) Recorder.Abort(id); else Recorder.Done(id, actual);
        }
        else if (ProxySnapshot.Same(actual, open.Before)) Recorder.Close(kind, L.T("core.proxy.value_back"), e => e.Id == open.Id);
        else if (!ProxySnapshot.Same(actual, open.After)) Recorder.Update(open.Id, actual);
        return error;
    }
}

/// <summary>
/// Откат изменений прокси: значение возвращается, только если сейчас стоит то, что ставили мы (<see cref="ChangeEntry.After"/>).
/// Для машины части независимы: настройки, политика ProxySettingsPerUser, WinHTTP.
/// </summary>
public sealed class ProxyUndoer(IProxyRegistry reg, IWinHttpProxy winHttp, IProxyNotifier? notifier = null) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.ProxyUser, ChangeKinds.ProxyUserAdmin, ChangeKinds.ProxyMachine];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct)
    {
        var restored = new List<string>(); var changed = new List<string>(); var same = new List<string>();
        try
        {
            bool machine = e.Kind == ChangeKinds.ProxyMachine;
            var key = machine ? ProxyKey.MachineSettings : ProxyKey.UserSettings;
            var sid = machine ? null : ProxySnapshot.Sid(e.After);
            if (sid is not null && !reg.UserHiveLoaded(sid)) return Task.FromResult(UndoOutcome.Fail(L.T("core.proxy.undo.hive")));

            Part(L.T("core.proxy.undo.part_settings"), ProxyRegistryIo.Read(reg, key, sid), ProxySnapshot.Settings(e.After), ProxySnapshot.Settings(e.Before),
                v =>
                {
                    ProxyRegistryIo.Write(reg, key, sid, v);
                    // ProxyEnable до DpiShell не было — не оставлять своё «0».
                    if (!v.Enabled && ProxySnapshot.NoEnableBefore(e.Before)) reg.Delete(key, sid, ProxyNames.Enable);
                });
            if (machine && ProxySnapshot.HasPerUser(e.After))
                Part(L.T("core.proxy.undo.part_policy"), ProxyRegistryIo.PerUserPolicy(reg), ProxySnapshot.PerUser(e.After), ProxySnapshot.PerUser(e.Before),
                    v => ProxyRegistryIo.SetPerUserPolicy(reg, v));
            if (machine && ProxySnapshot.WinHttp(e.After) is { } wAfter)
                Part(L.T("core.proxy.undo.part_winhttp"), winHttp.Get(), wAfter, ProxySnapshot.WinHttp(e.Before) ?? WinHttpInfo.Direct,
                    v => { if (!winHttp.Set(v)) throw new IOException(L.T("core.proxy.undo.winhttp_refused")); });
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException or ArgumentException)
        {
            return Task.FromResult(UndoOutcome.Fail(ex.Message));
        }

        if (restored.Count > 0) notifier?.Changed();
        var skipped = changed.Count > 0 ? L.T("core.proxy.undo.skipped", new { list = string.Join(", ", changed) }) : "";
        if (restored.Count > 0) return Task.FromResult(UndoOutcome.Restored(L.T("core.proxy.undo.restored", new { list = string.Join(", ", restored), skipped })));
        if (changed.Count > 0) return Task.FromResult(UndoOutcome.Changed(L.T("core.proxy.undo.changed", new { list = string.Join(", ", changed) })));
        return Task.FromResult(UndoOutcome.Already(L.T("core.proxy.undo.already")));

        void Part<T>(string name, T cur, T? ours, T? was, Action<T> write)
        {
            if (EqualityComparer<T?>.Default.Equals(cur, was)) { same.Add(name); return; }
            if (EqualityComparer<T?>.Default.Equals(cur, ours)) { write(was!); restored.Add(name); return; }
            changed.Add(name);
        }
    }
}
