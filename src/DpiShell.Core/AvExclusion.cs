using DpiShell.Core.Localization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace DpiShell.Core;

/// <summary>Антивирус из «Центра безопасности» (root\SecurityCenter2, AntiVirusProduct). <paramref name="IsDefender"/> — сам Защитник Windows.</summary>
public sealed record AvProduct(string Name, bool Enabled, bool IsDefender);

/// <summary>Режим Защитника (MSFT_MpComputerStatus): работает сам — Active; рядом главный сторонний — Passive; выключен — Off.</summary>
public enum DefenderMode { Unknown, Active, Passive, Off }

/// <param name="Paths">Исключённые папки (MSFT_MpPreference.ExclusionPath).</param>
/// <param name="Hidden">Список скрыт политикой (HideExclusionsFromLocalAdmins, «N/A: Must be an administrator…»): пустой список — не «нет», а «не знаем».</param>
public sealed record ExclusionList(IReadOnlyList<string> Paths, bool Hidden);

/// <summary>
/// Защитник Windows — только то, что нужно исключению папки ядра: кто антивирус, режим Защитника, список исключённых папок, добавить и снять папку.
/// Настоящий — <see cref="WmiDefender"/> (WMI, без запуска powershell.exe); в тестах — подмена в памяти.
/// </summary>
public interface IDefenderSystem
{
    /// <summary>Антивирусы «Центра безопасности»; пусто — не узнать (Windows Server, нет службы).</summary>
    IReadOnlyList<AvProduct> Products();
    DefenderMode Mode();
    /// <summary>Исключённые папки; null — не прочитать.</summary>
    ExclusionList? Exclusions();
    /// <summary>Добавить папку в исключения. Отказ (защита от изменений, политика, нет прав) — исключение.</summary>
    void AddPath(string path);
    /// <summary>Снять папку из исключений. Отказ — исключение.</summary>
    void RemovePath(string path);
    /// <summary>
    /// Обнаружения Защитника (MSFT_MpThreatDetection, имя — из MSFT_MpThreat) — подтвердить, что файл удалил именно он (дополнение 2 ТЗ av-exclusion).
    /// Только чтение; без прав или без Защитника — исключение WMI (вызывающий пишет «возможно, антивирус»).
    /// </summary>
    IReadOnlyList<DefenderThreat> Threats();
}

/// <summary>Состояние исключения папки ядра (поле <c>state</c> ответа <c>av_exclusion_status</c>).</summary>
public static class AvStates
{
    /// <summary>Папка в исключениях Защитника (сама или папка выше).</summary>
    public const string Excluded = "excluded";
    /// <summary>Защитник работает, папки в исключениях нет.</summary>
    public const string Missing = "missing";
    /// <summary>Защитник работает, список исключений скрыт или не читается: есть ли папка — не знаем.</summary>
    public const string Unknown = "unknown";
    /// <summary>Главный антивирус — сторонний: в Защитник не пишем, исключение — в его настройках.</summary>
    public const string ThirdParty = "third_party";
    /// <summary>Защитник выключен или в пассивном режиме, другого антивируса не видно: исключение не нужно.</summary>
    public const string DefenderOff = "defender_off";
    /// <summary>Ни Защитника, ни «Центра безопасности» не прочитать.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>Итог добавления или снятия (поле <c>outcome</c>).</summary>
public static class AvOutcomes
{
    public const string Added = "added";
    public const string Already = "already";
    public const string NotNeeded = "not_needed";
    public const string ThirdParty = "third_party";
    /// <summary>Защитник не дал (ошибка WMI или после добавления папки нет в списке) — добавить вручную.</summary>
    public const string Refused = "refused";
    public const string Removed = "removed";
    /// <summary>Снимать нечего: исключение добавлено не DpiShell (или его уже нет).</summary>
    public const string NotOurs = "not_ours";
}

/// <param name="State">Из <see cref="AvStates"/>.</param>
/// <param name="Path">Папка, которую исключает DpiShell: <c>%ProgramData%\DpiShell</c> (корень программы у помощника).</param>
/// <param name="Product">Сторонний антивирус (при <see cref="AvStates.ThirdParty"/>).</param>
/// <param name="Ours">Исключение добавил DpiShell (есть незакрытая запись журнала) — «Отменить всё» его снимет.</param>
public sealed record AvExclusionStatus(string State, string Path, string? Product = null, bool Ours = false);

/// <param name="Outcome">Из <see cref="AvOutcomes"/>.</param>
/// <param name="Message">Готовая фраза для окна.</param>
public sealed record AvExclusionResult(bool Ok, string Outcome, string Message, AvExclusionStatus Status);

/// <summary>
/// Исключение Защитника для папки ядра (ТЗ av-exclusion). Путь — только свой (<paramref name="path"/> = корень программы помощника), из запроса его не берут:
/// иначе пользователь без прав исключил бы через помощника что угодно. Добавляется только по нажатию, пишется в журнал до действия (<c>before.present</c> —
/// было ли исключение до нас); снимается только своё (<see cref="AvExclusionUndoer"/>). Папка программы не исключается — её может писать пользователь.
/// </summary>
public sealed class AvExclusions(IDefenderSystem sys, string path, IChangeRecorder journal)
{
    public string Path { get; } = path;

    /// <summary>Защитник уже не проверяет <paramref name="path"/>: в списке есть она сама или папка выше (без учёта регистра и конечной черты).</summary>
    public static bool Covers(IEnumerable<string> list, string path)
    {
        var p = Norm(path);
        foreach (var raw in list)
        {
            var e = Norm(raw);
            if (e.Length == 0) continue;
            if (p.Equals(e, StringComparison.OrdinalIgnoreCase)) return true;
            if (p.StartsWith(e.EndsWith('\\') ? e : e + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Ровно эта папка в списке (снимать — только её, не папку выше).</summary>
    public static bool HasExact(IEnumerable<string> list, string path) => list.Any(e => Norm(e).Equals(Norm(path), StringComparison.OrdinalIgnoreCase));

    static string Norm(string s)
    {
        var t = Environment.ExpandEnvironmentVariables(s.Trim()).Replace('/', '\\');
        return t.Length > 3 ? t.TrimEnd('\\') : t;
    }

    /// <summary>Сторонний включённый антивирус (не Защитник); нет — null.</summary>
    static AvProduct? ThirdParty(IReadOnlyList<AvProduct> products) => products.FirstOrDefault(p => p.Enabled && !p.IsDefender);

    public AvExclusionStatus Status()
    {
        IReadOnlyList<AvProduct> products;
        try { products = sys.Products(); }
        catch (Exception e) when (IsWmiError(e)) { products = []; }
        DefenderMode mode;
        try { mode = sys.Mode(); }
        catch (Exception e) when (IsWmiError(e)) { mode = DefenderMode.Unknown; }
        bool ours = journal.Active(ChangeKinds.AvExclusion).Count > 0;
        // Главный — сторонний: Защитник пассивен или выключен (или сторонний включён, а режим не прочитать).
        if (ThirdParty(products) is { } other && mode != DefenderMode.Active) return new(AvStates.ThirdParty, Path, other.Name, ours);
        if (mode is DefenderMode.Passive or DefenderMode.Off) return new(AvStates.DefenderOff, Path, Ours: ours);
        if (mode == DefenderMode.Unknown) return new(AvStates.Unavailable, Path, Ours: ours);
        ExclusionList? list;
        try { list = sys.Exclusions(); }
        catch (Exception e) when (IsWmiError(e)) { list = null; }
        if (list is null) return new(AvStates.Unknown, Path, Ours: ours);
        if (Covers(list.Paths, Path)) return new(AvStates.Excluded, Path, Ours: ours);
        return new(list.Hidden ? AvStates.Unknown : AvStates.Missing, Path, Ours: ours);
    }

    /// <summary>«Добавить в исключения»: только Защитник, только своя папка; запись в журнал до действия, проверка чтением после.</summary>
    public AvExclusionResult Add()
    {
        var st = Status();
        switch (st.State)
        {
            case AvStates.Excluded: return new(true, AvOutcomes.Already, L.T("core.av.already"), st);
            case AvStates.DefenderOff: return new(true, AvOutcomes.NotNeeded, L.T("core.av.not_needed"), st);
            case AvStates.ThirdParty: return new(false, AvOutcomes.ThirdParty, L.T("core.av.third_party", new { name = st.Product }), st);
            case AvStates.Unavailable: return new(false, AvOutcomes.Refused, L.T("core.av.unavailable"), st);
        }
        // Список скрыт — было ли исключение до нас, неизвестно: before.present = null, «Отменить всё» такое не снимет.
        var before = new JsonObject { ["path"] = Path, ["present"] = st.State == AvStates.Missing ? false : null };
        var id = journal.Begin(ChangeKinds.AvExclusion, L.T("core.av.journal", new { path = Path }), before, new JsonObject { ["path"] = Path, ["present"] = true });
        try { sys.AddPath(Path); }
        catch (Exception e) when (IsWmiError(e))
        {
            journal.Abort(id);
            return new(false, AvOutcomes.Refused, L.T("core.av.refused"), Status());
        }
        ExclusionList? after;
        try { after = sys.Exclusions(); }
        catch (Exception e) when (IsWmiError(e)) { after = null; }
        // Защита от изменений или политика могут принять вызов молча и не записать: папки в списке нет — не вышло.
        if (after is { Hidden: false } a && !Covers(a.Paths, Path))
        {
            journal.Abort(id);
            return new(false, AvOutcomes.Refused, L.T("core.av.refused"), Status());
        }
        journal.Done(id);
        return new(true, AvOutcomes.Added, L.T("core.av.added"), Status());
    }

    /// <summary>Ошибки WMI и COM, отказ в доступе, выключенная защита тестов — «не получилось», а не падение помощника.</summary>
    public static bool IsWmiError(Exception e) =>
        e is ManagementException or COMException or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException or PlatformNotSupportedException;
}

/// <summary>
/// Откат исключения папки ядра: снимается, только если его добавил DpiShell (<c>before.present = false</c>) и только ровно эта папка — путь берётся свой,
/// не из журнала. Было до нас или неизвестно — не трогаем.
/// </summary>
public sealed class AvExclusionUndoer(IDefenderSystem sys, string path) : IChangeUndoer
{
    public IReadOnlyCollection<string> Kinds { get; } = [ChangeKinds.AvExclusion];

    public Task<UndoOutcome> UndoAsync(ChangeEntry e, CancellationToken ct) => Task.FromResult(Undo(e));

    UndoOutcome Undo(ChangeEntry e)
    {
        var present = e.Before?["present"];
        if (present is not JsonValue v || !v.TryGetValue<bool>(out var was)) return UndoOutcome.Changed(L.T("core.undo.av_unknown"));
        if (was) return UndoOutcome.Changed(L.T("core.undo.av_before"));
        // Запись другой папки (перенос %ProgramData%) — не наша сейчас: не снимаем чужое.
        if (e.Before?["path"]?.GetValue<string>() is { } p && !AvExclusions.HasExact([p], path)) return UndoOutcome.Changed(L.T("core.undo.av_before"));
        ExclusionList? now;
        try { now = sys.Exclusions(); }
        catch (Exception ex) when (AvExclusions.IsWmiError(ex)) { now = null; }
        if (now is { Hidden: false } n && !AvExclusions.HasExact(n.Paths, path)) return UndoOutcome.Already(L.T("core.undo.av_gone"));
        try { sys.RemovePath(path); }
        catch (Exception ex) when (AvExclusions.IsWmiError(ex)) { return UndoOutcome.Fail(L.T("core.undo.av_refused")); }
        try { now = sys.Exclusions(); }
        catch (Exception ex) when (AvExclusions.IsWmiError(ex)) { now = null; }
        if (now is { Hidden: false } m && AvExclusions.HasExact(m.Paths, path)) return UndoOutcome.Fail(L.T("core.undo.av_refused"));
        return UndoOutcome.Restored(L.T("core.undo.av_removed"));
    }

    /// <summary>
    /// Снять только своё исключение по журналу <paramref name="journal"/> (удаление DpiShell: <c>remove_all</c>, <c>--uninstall-helper --purge</c> — до того,
    /// как журнал сотрётся вместе с папкой). Возвращает отчёт по записям; своих нет — пусто.
    /// </summary>
    public static UndoReport UndoOwn(ChangeJournal journal, IDefenderSystem sys, string path)
    {
        var engine = new ChangeUndoEngine(journal);
        engine.Register(new AvExclusionUndoer(sys, path));
        var items = new List<UndoItem>();
        foreach (var e in journal.Active(ChangeKinds.AvExclusion).Reverse())
            items.AddRange(engine.UndoAsync(e.Id).GetAwaiter().GetResult().Items);
        return new(items);
    }
}

/// <summary>
/// Настоящий Защитник через WMI: <c>root\Microsoft\Windows\Defender</c> (MSFT_MpComputerStatus, MSFT_MpPreference.Add/Remove с ExclusionPath) и
/// <c>root\SecurityCenter2</c> (AntiVirusProduct). powershell.exe с Add-MpPreference не запускается: такие командные строки — приём вредоносов,
/// на них срабатывают поведенческие правила. Запись — только у администратора или SYSTEM (помощник, установка).
/// </summary>
public sealed class WmiDefender : IDefenderSystem
{
    /// <summary>Защита тестов: настоящий Защитник под тестом — исключение (как у прокси).</summary>
    public static bool Forbidden { get; set; }

    const string DefenderNs = @"root\Microsoft\Windows\Defender";

    static void Guard(string what)
    {
        if (Forbidden) throw new InvalidOperationException("Настоящий Защитник в тесте — подмените IDefenderSystem: " + what);
    }

    public IReadOnlyList<AvProduct> Products()
    {
        Guard("products");
        var list = new List<AvProduct>();
        using var q = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct");
        using var found = q.Get();
        foreach (var mo in found.Cast<ManagementObject>())
        {
            using (mo)
            {
                var name = mo["displayName"] as string ?? "";
                var exe = mo["pathToSignedProductExe"] as string ?? "";
                uint state = mo["productState"] is { } s ? Convert.ToUInt32(s, System.Globalization.CultureInfo.InvariantCulture) : 0;
                list.Add(new(name, ProductEnabled(state), IsDefenderProduct(name, exe)));
            }
        }
        return list;
    }

    /// <summary>productState: второй байт — состояние движка (0x10 — включён, 0x11 — включён и зарегистрирован «вместо»); остальное — выключен или просрочен.</summary>
    public static bool ProductEnabled(uint productState) => ((productState >> 8) & 0x10) != 0;

    public static bool IsDefenderProduct(string name, string exe) =>
        exe.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Windows Defender", StringComparison.OrdinalIgnoreCase)
        || name.Contains("Microsoft Defender", StringComparison.OrdinalIgnoreCase);

    public DefenderMode Mode()
    {
        Guard("mode");
        using var q = new ManagementObjectSearcher(DefenderNs, "SELECT * FROM MSFT_MpComputerStatus");
        using var found = q.Get();
        var mo = found.Cast<ManagementObject>().FirstOrDefault();
        if (mo is null) return DefenderMode.Unknown;
        using (mo)
        {
            string? running = null;
            try { running = mo["AMRunningMode"] as string; } catch (ManagementException) { }
            bool? Flag(string n) { try { return mo[n] as bool?; } catch (ManagementException) { return null; } }
            return ParseMode(running, Flag("AMServiceEnabled"), Flag("AntivirusEnabled"), Flag("RealTimeProtectionEnabled"));
        }
    }

    /// <summary>AMRunningMode — «Normal», «Passive Mode», «SxS Passive Mode», «EDR Block Mode», «Not running»; без него — по флагам.</summary>
    public static DefenderMode ParseMode(string? running, bool? service, bool? antivirus, bool? realtime)
    {
        if (running is { Length: > 0 } r)
        {
            if (r.Contains("Passive", StringComparison.OrdinalIgnoreCase) || r.Contains("EDR", StringComparison.OrdinalIgnoreCase)) return DefenderMode.Passive;
            if (r.Contains("Not running", StringComparison.OrdinalIgnoreCase)) return DefenderMode.Off;
        }
        if (service == false || antivirus == false || realtime == false) return DefenderMode.Off;
        if (service is null && antivirus is null && realtime is null && running is null) return DefenderMode.Unknown;
        return DefenderMode.Active;
    }

    public ExclusionList? Exclusions()
    {
        Guard("exclusions");
        using var q = new ManagementObjectSearcher(DefenderNs, "SELECT * FROM MSFT_MpPreference");
        using var found = q.Get();
        var mo = found.Cast<ManagementObject>().FirstOrDefault();
        if (mo is null) return null;
        using (mo)
        {
            var paths = mo["ExclusionPath"] as string[] ?? [];
            bool hide = false;
            try { hide = mo["HideExclusionsFromLocalAdmins"] is true; } catch (ManagementException) { }
            return Parse(paths, hide);
        }
    }

    /// <summary>«N/A: Must be an administrator to view exclusions» вместо списка — скрыт.</summary>
    public static ExclusionList Parse(IReadOnlyList<string> raw, bool hiddenByPolicy)
    {
        bool na = raw.Any(p => p.StartsWith("N/A", StringComparison.OrdinalIgnoreCase));
        var paths = raw.Where(p => !p.StartsWith("N/A", StringComparison.OrdinalIgnoreCase) && p.Trim().Length > 0).ToList();
        return new(paths, na || (hiddenByPolicy && paths.Count == 0));
    }

    public void AddPath(string path) => Invoke("Add", path);
    public void RemovePath(string path) => Invoke("Remove", path);

    public IReadOnlyList<DefenderThreat> Threats()
    {
        Guard("threats");
        var names = new Dictionary<long, string>();
        using (var q = new ManagementObjectSearcher(DefenderNs, "SELECT ThreatID, ThreatName FROM MSFT_MpThreat"))
        using (var found = q.Get())
            foreach (var mo in found.Cast<ManagementObject>())
                using (mo)
                    if (mo["ThreatID"] is { } id && mo["ThreatName"] is string n)
                        names[Convert.ToInt64(id, System.Globalization.CultureInfo.InvariantCulture)] = n;
        var list = new List<DefenderThreat>();
        using (var q = new ManagementObjectSearcher(DefenderNs, "SELECT ThreatID, Resources, InitialDetectionTime FROM MSFT_MpThreatDetection"))
        using (var found = q.Get())
            foreach (var mo in found.Cast<ManagementObject>())
                using (mo)
                {
                    long id = mo["ThreatID"] is { } v ? Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    DateTime? at = null;
                    if (mo["InitialDetectionTime"] is string t)
                        try { at = ManagementDateTimeConverter.ToDateTime(t).ToUniversalTime(); } catch (ArgumentException) { }
                    list.Add(new(names.GetValueOrDefault(id, ""), mo["Resources"] as string[] ?? [], at));
                }
        return list;
    }

    static void Invoke(string method, string path)
    {
        Guard(method);
        using var cls = new ManagementClass(DefenderNs, "MSFT_MpPreference", null);
        using var input = cls.GetMethodParameters(method);
        input["ExclusionPath"] = new[] { path };
        using var output = cls.InvokeMethod(method, input, null);
        var code = output?["ReturnValue"] is { } rv ? Convert.ToInt64(rv, System.Globalization.CultureInfo.InvariantCulture) : 0;
        if (code != 0) throw new InvalidOperationException($"MSFT_MpPreference.{method}: {code}");
    }
}
