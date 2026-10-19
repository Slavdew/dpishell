using System.Security.Principal;
using Microsoft.Win32;

namespace DpiShell.Core;

/// <summary>Ветки реестра, к которым имеет доступ работа с прокси. Других путей нет — произвольный путь передать нельзя.</summary>
public enum ProxyKey
{
    /// <summary>HKCU (или HKU\&lt;SID&gt;) \Software\Microsoft\Windows\CurrentVersion\Internet Settings.</summary>
    UserSettings,
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings.</summary>
    MachineSettings,
    /// <summary>HKLM\SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings (ProxySettingsPerUser).</summary>
    MachinePolicy,
    /// <summary>HKLM\SOFTWARE\Policies\Microsoft\Internet Explorer\Control Panel (запрет менять прокси).</summary>
    MachineIePolicy,
    /// <summary>HKCU\Software\Policies\Microsoft\Internet Explorer\Control Panel.</summary>
    UserIePolicy,
}

/// <summary>Имена значений, которые можно читать и писать (белый список).</summary>
public static class ProxyNames
{
    public const string Enable = "ProxyEnable", Server = "ProxyServer", Override = "ProxyOverride", Pac = "AutoConfigURL";
    public const string PerUser = "ProxySettingsPerUser";
    public const string ConnectionsBlob = @"Connections\DefaultConnectionSettings";
    public const string IeProxy = "Proxy", IeConnectionsTab = "ConnectionsTab";

    static readonly HashSet<string> Writable = [Enable, Server, Override, Pac, PerUser];
    static readonly HashSet<string> Readable = [Enable, Server, Override, Pac, PerUser, ConnectionsBlob, IeProxy, IeConnectionsTab];

    public static bool CanRead(string name) => Readable.Contains(name);
    public static bool CanWrite(ProxyKey key, string name) =>
        Writable.Contains(name) && (name == PerUser ? key == ProxyKey.MachinePolicy : key is ProxyKey.UserSettings or ProxyKey.MachineSettings && name != PerUser);
}

/// <summary>
/// Реестр прокси. В работе — <see cref="WindowsProxyRegistry"/>, в тестах — <see cref="MemoryProxyRegistry"/>
/// (настоящий реестр тесты не трогают). <c>sid</c> — только для <see cref="ProxyKey.UserSettings"/>: null — HKCU, иначе HKU\SID.
/// </summary>
public interface IProxyRegistry
{
    /// <summary>string, int, byte[] или null (значения нет / ветки нет).</summary>
    object? Get(ProxyKey key, string? sid, string name);
    void SetString(ProxyKey key, string? sid, string name, string value);
    void SetDword(ProxyKey key, string? sid, string name, int value);
    void Delete(ProxyKey key, string? sid, string name);
    /// <summary>Загружена ли ветка пользователя (HKU\SID): без неё писать некуда.</summary>
    bool UserHiveLoaded(string sid);
}

public static class ProxySid
{
    /// <summary>SID обычного пользователя: разбирается и не системный/служебный.</summary>
    public static bool IsUserSid(string? sid)
    {
        if (string.IsNullOrWhiteSpace(sid) || sid.Length > 100) return false;
        try
        {
            var s = new SecurityIdentifier(sid);
            if (s.IsWellKnown(WellKnownSidType.LocalSystemSid) || s.IsWellKnown(WellKnownSidType.LocalServiceSid)
                || s.IsWellKnown(WellKnownSidType.NetworkServiceSid) || s.IsWellKnown(WellKnownSidType.AnonymousSid)) return false;
            return s.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) || s.Value.StartsWith("S-1-12-1-", StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }
}

/// <summary>Настоящий реестр Windows (HKCU/HKU/HKLM, 64-разрядный вид).</summary>
public sealed class WindowsProxyRegistry : IProxyRegistry
{
    const string Inet = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string MachineInet = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string Pol = @"SOFTWARE\Policies\Microsoft\Windows\CurrentVersion\Internet Settings";
    const string IePol = @"Policies\Microsoft\Internet Explorer\Control Panel";

    static RegistryKey? Open(ProxyKey key, string? sid, bool write, bool create = false)
    {
        if (key == ProxyKey.UserSettings && sid is not null && !ProxySid.IsUserSid(sid)) throw new ArgumentException("SID не пользовательский.");
        RegistryKey root;
        string path;
        switch (key)
        {
            case ProxyKey.UserSettings:
                root = sid is null ? Registry.CurrentUser : Registry.Users;
                path = sid is null ? Inet : sid + "\\" + Inet;
                break;
            case ProxyKey.UserIePolicy: root = Registry.CurrentUser; path = "Software\\" + IePol; break;
            case ProxyKey.MachineSettings: root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); path = MachineInet; break;
            case ProxyKey.MachinePolicy: root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); path = Pol; break;
            case ProxyKey.MachineIePolicy: root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64); path = "SOFTWARE\\" + IePol; break;
            default: throw new ArgumentOutOfRangeException(nameof(key));
        }
        return create ? root.CreateSubKey(path, writable: true) : root.OpenSubKey(path, write);
    }

    static (string Sub, string Value) Split(string name)
    {
        int i = name.LastIndexOf('\\');
        return i < 0 ? ("", name) : (name[..i], name[(i + 1)..]);
    }

    public object? Get(ProxyKey key, string? sid, string name)
    {
        if (!ProxyNames.CanRead(name)) throw new ArgumentException("Имя значения не из белого списка.");
        if (key == ProxyKey.UserSettings && sid is not null && !ProxySid.IsUserSid(sid)) throw new ArgumentException("SID не пользовательский.");
        SystemProxyGuard.Check("чтение " + key);
        try
        {
            using var k = Open(key, sid, write: false);
            if (k is null) return null;
            var (sub, value) = Split(name);
            if (sub.Length == 0) return k.GetValue(value);
            using var s = k.OpenSubKey(sub);
            return s?.GetValue(value);
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void SetString(ProxyKey key, string? sid, string name, string value) => Set(key, sid, name, value, RegistryValueKind.String);
    public void SetDword(ProxyKey key, string? sid, string name, int value) => Set(key, sid, name, value, RegistryValueKind.DWord);

    static void Set(ProxyKey key, string? sid, string name, object value, RegistryValueKind kind)
    {
        if (!ProxyNames.CanWrite(key, name)) throw new ArgumentException("Это значение менять нельзя.");
        if (key == ProxyKey.UserSettings && sid is not null && !ProxySid.IsUserSid(sid)) throw new ArgumentException("SID не пользовательский.");
        SystemProxyGuard.Check("запись " + key);
        using var k = Open(key, sid, write: true, create: true) ?? throw new IOException("Не удалось открыть ветку реестра.");
        k.SetValue(name, value, kind);
    }

    public void Delete(ProxyKey key, string? sid, string name)
    {
        if (!ProxyNames.CanWrite(key, name)) throw new ArgumentException("Это значение менять нельзя.");
        if (key == ProxyKey.UserSettings && sid is not null && !ProxySid.IsUserSid(sid)) throw new ArgumentException("SID не пользовательский.");
        SystemProxyGuard.Check("удаление " + key);
        using var k = Open(key, sid, write: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public bool UserHiveLoaded(string sid)
    {
        if (!ProxySid.IsUserSid(sid)) return false;
        SystemProxyGuard.Check("ветка пользователя");
        try { using var k = Registry.Users.OpenSubKey(sid); return k is not null; }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return false; }
    }
}

/// <summary>Реестр прокси в памяти — для тестов и как значение по умолчанию (чтобы окно в тестах не касалось настоящего).</summary>
public sealed class MemoryProxyRegistry : IProxyRegistry
{
    public Dictionary<(ProxyKey, string, string), object> Values { get; } = [];
    public HashSet<string> LoadedHives { get; } = [];
    /// <summary>Сколько раз писали (для проверки «ничего не менялось»).</summary>
    public int Writes { get; private set; }
    /// <summary>Имитация отказа в доступе при записи.</summary>
    public bool DenyWrites { get; set; }

    static (ProxyKey, string, string) K(ProxyKey key, string? sid, string name) => (key, key == ProxyKey.UserSettings ? sid ?? "" : "", name);

    public object? Get(ProxyKey key, string? sid, string name)
    {
        if (!ProxyNames.CanRead(name)) throw new ArgumentException("Имя значения не из белого списка.");
        return Values.GetValueOrDefault(K(key, sid, name));
    }

    public void SetString(ProxyKey key, string? sid, string name, string value) => Put(key, sid, name, value);
    public void SetDword(ProxyKey key, string? sid, string name, int value) => Put(key, sid, name, value);

    void Put(ProxyKey key, string? sid, string name, object value)
    {
        if (!ProxyNames.CanWrite(key, name)) throw new ArgumentException("Это значение менять нельзя.");
        if (DenyWrites) throw new UnauthorizedAccessException("Отказано в доступе.");
        Writes++;
        Values[K(key, sid, name)] = value;
    }

    public void Delete(ProxyKey key, string? sid, string name)
    {
        if (!ProxyNames.CanWrite(key, name)) throw new ArgumentException("Это значение менять нельзя.");
        if (DenyWrites) throw new UnauthorizedAccessException("Отказано в доступе.");
        Writes++;
        Values.Remove(K(key, sid, name));
    }

    public bool UserHiveLoaded(string sid) => LoadedHives.Contains(sid);
}

/// <summary>Чтение и запись одной ветки как <see cref="ProxySettings"/>.</summary>
public static class ProxyRegistryIo
{
    public static ProxySettings Read(IProxyRegistry r, ProxyKey key, string? sid = null)
    {
        bool enabled = r.Get(key, sid, ProxyNames.Enable) is int e && e != 0;
        return new ProxySettings(enabled, Str(r.Get(key, sid, ProxyNames.Server)), Str(r.Get(key, sid, ProxyNames.Override)), Str(r.Get(key, sid, ProxyNames.Pac)));
    }

    static string? Str(object? v) => v is string s && s.Length > 0 ? s : null;

    /// <summary>Флаг «автоматически определять параметры» из DefaultConnectionSettings (байт флагов со смещения 8, бит 0x08). Только чтение.</summary>
    public static bool AutoDetect(IProxyRegistry r, ProxyKey key, string? sid = null) =>
        r.Get(key, sid, ProxyNames.ConnectionsBlob) is byte[] b && b.Length > 8 && (b[8] & 0x08) != 0;

    /// <summary>Записать ровно эти значения: пустые — удалить; ProxyEnable пишется последним.</summary>
    public static void Write(IProxyRegistry r, ProxyKey key, string? sid, ProxySettings s)
    {
        Put(r, key, sid, ProxyNames.Server, s.Server);
        Put(r, key, sid, ProxyNames.Override, s.Bypass);
        Put(r, key, sid, ProxyNames.Pac, s.Pac);
        r.SetDword(key, sid, ProxyNames.Enable, s.Enabled ? 1 : 0);
    }

    static void Put(IProxyRegistry r, ProxyKey key, string? sid, string name, string? value)
    {
        if (value is null) r.Delete(key, sid, name); else r.SetString(key, sid, name, value);
    }

    public static int? PerUserPolicy(IProxyRegistry r) => r.Get(ProxyKey.MachinePolicy, null, ProxyNames.PerUser) is int i ? i : null;

    public static void SetPerUserPolicy(IProxyRegistry r, int? value)
    {
        if (value is { } v) r.SetDword(ProxyKey.MachinePolicy, null, ProxyNames.PerUser, v);
        else r.Delete(ProxyKey.MachinePolicy, null, ProxyNames.PerUser);
    }

    /// <summary>Политика «запретить менять прокси» (IE: Control Panel\Proxy / ConnectionsTab) в HKLM или HKCU.</summary>
    public static bool ChangeBlocked(IProxyRegistry r) =>
        new[] { ProxyKey.MachineIePolicy, ProxyKey.UserIePolicy }.Any(k =>
            r.Get(k, null, ProxyNames.IeProxy) is int p && p != 0 || r.Get(k, null, ProxyNames.IeConnectionsTab) is int c && c != 0);
}

/// <summary>Всё, что окно показывает на вкладке «Прокси» о текущем состоянии.</summary>
/// <param name="PerUserPolicy">ProxySettingsPerUser из политики HKLM (0 — одни настройки на всех, WinINET читает HKLM); null — не задано.</param>
/// <param name="PolicyBlocked">Политика запрещает менять прокси.</param>
public sealed record ProxyOverview(ProxySettings User, bool UserAutoDetect, ProxySettings Machine, bool MachineAutoDetect, int? PerUserPolicy,
    bool PolicyBlocked, WinHttpInfo WinHttp)
{
    /// <summary>Настройки общие для всех пользователей (политика), настройки пользователя WinINET не читает.</summary>
    public bool MachineWide => PerUserPolicy == 0;

    /// <summary>То, что реально действует для WinINET-программ этого пользователя.</summary>
    public ProxySettings Effective => MachineWide ? Machine : User;

    public static ProxyOverview Read(IProxyRegistry r, IWinHttpProxy winHttp, string? sid = null) => new(
        ProxyRegistryIo.Read(r, ProxyKey.UserSettings, sid), ProxyRegistryIo.AutoDetect(r, ProxyKey.UserSettings, sid),
        ProxyRegistryIo.Read(r, ProxyKey.MachineSettings), ProxyRegistryIo.AutoDetect(r, ProxyKey.MachineSettings),
        ProxyRegistryIo.PerUserPolicy(r), ProxyRegistryIo.ChangeBlocked(r), winHttp.Get());
}
