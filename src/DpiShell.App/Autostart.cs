using System.IO;
using Microsoft.Win32;

namespace DpiShell.App;

/// <summary>Где записан автозапуск: в работе — ветка Run пользователя, в тестах — память (настоящий реестр тесты не трогают).</summary>
public interface IRunKeyStore
{
    string? Get(string name);
    void Set(string name, string command);
    void Remove(string name);
}

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run — прав администратора не нужно, только для этого пользователя.</summary>
public sealed class RegistryRunKeyStore : IRunKeyStore
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        using var k = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        k.SetValue(name, command, RegistryValueKind.String);
    }

    public void Remove(string name)
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

public sealed class MemoryRunKeyStore : IRunKeyStore
{
    public Dictionary<string, string> Values { get; } = [];
    public string? Get(string name) => Values.GetValueOrDefault(name);
    public void Set(string name, string command) => Values[name] = command;
    public void Remove(string name) => Values.Remove(name);
}

/// <summary>
/// Окно при входе в Windows — свёрнутым в трей (architecture.md «Автозапуск»). По умолчанию включено в режиме без службы:
/// там обход работает, только пока открыта программа, и без автозапуска после входа не поднялся бы. В режиме службы
/// обход стартует сам — окно по желанию, по умолчанию выключено. Явный выбор пользователя — в window.json.
/// </summary>
public sealed class Autostart(WindowSettings settings) : Observable
{
    public const string ValueName = "DpiShell";
    public const string TrayArg = "--tray";

    bool? _serviceMode;

    /// <summary>Хранилище ключа Run; App ставит реестр, по умолчанию — память.</summary>
    public IRunKeyStore Store { get; set; } = new MemoryRunKeyStore();
    /// <summary>Путь к exe: portable-папку могут перенести — при каждом запуске запись поправляется.</summary>
    public string ExePath { get; set; } = Environment.ProcessPath ?? "DpiShell.exe";
    public Action<string> Say { get; set; } = _ => { };

    public string Command => $"\"{ExePath}\" {TrayArg}";

    /// <summary>Галочка «Запускать вместе с Windows».</summary>
    public bool Enabled
    {
        get => settings.Autostart ?? _serviceMode == false;
        set
        {
            if (settings.Autostart == value && Enabled == value) return;
            settings.Autostart = value;
            try { settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Say(Texts.Friendly(e)); }
            Apply();
            Raise();
        }
    }

    public string Hint => _serviceMode == false
        ? "Без режима службы обход работает, пока открыта программа, — так он включится после входа."
        : "Окно откроется в трее после входа. Служба обхода стартует и без него.";

    /// <summary>Режим помощника стал известен или сменился — выбор по умолчанию следует за ним.</summary>
    public void OnRunMode(bool service)
    {
        if (_serviceMode == service) return;
        _serviceMode = service;
        Apply();
        Raise(nameof(Enabled));
        Raise(nameof(Hint));
    }

    /// <summary>
    /// Привести ключ Run к настройке. Пока ни выбора, ни режима нет (помощник не ответил) — не трогать:
    /// иначе ключ то пропадал бы, то появлялся при каждом запуске.
    /// </summary>
    public void Apply()
    {
        if (settings.Autostart is null && _serviceMode is null) return;
        try
        {
            if (Enabled)
            {
                if (Store.Get(ValueName) != Command) Store.Set(ValueName, Command);
            }
            else if (Store.Get(ValueName) is not null) Store.Remove(ValueName);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Say("Не удалось изменить автозапуск: " + e.Message);
        }
    }
}
