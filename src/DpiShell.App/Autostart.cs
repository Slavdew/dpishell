using System.IO;
using DpiShell.Core;
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

    /// <summary>Журнал окна (откат всего): ключ Run пользователя пишет окно, а не помощник.</summary>
    public IChangeRecorder Recorder { get; set; } = NullRecorder.Instance;

    /// <summary>Откат записи журнала: вернуть «нет ключа», если сейчас стоит то, что ставили мы.</summary>
    public UndoOutcome Undo(ChangeEntry e)
    {
        var now = Store.Get(ValueName);
        if (now is null) return UndoOutcome.Already("запуска вместе с Windows уже нет");
        if (now != e.After?["command"]?.GetValue<string>()) return UndoOutcome.Changed("запись автозапуска изменена после DpiShell");
        settings.Autostart = false;   // явный выбор: иначе по умолчанию (режим без службы) ключ вернётся при следующем запуске
        try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { Store.Remove(ValueName); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) { return UndoOutcome.Fail(ex.Message); }
        Raise(nameof(Enabled));
        return UndoOutcome.Restored("запуск вместе с Windows выключен");
    }

    /// <summary>Хранилище ключа Run; App ставит реестр, по умолчанию — память.</summary>
    public IRunKeyStore Store { get; set; } = new MemoryRunKeyStore();
    /// <summary>Путь к exe: portable-папку могут перенести — при каждом запуске запись поправляется.</summary>
    public string ExePath { get; set; } = Environment.ProcessPath ?? "DpiShell.exe";
    public Action<string> Say { get; set; } = _ => { };
    /// <summary>Ошибка операции: если окно передало <see cref="Fail"/> — тостом, иначе как <see cref="Say"/>.</summary>
    public Action<string>? Fail { get; set; }
    void Failed(string text) => (Fail ?? Say)(text);

    public string Command => $"\"{ExePath}\" {TrayArg}";

    /// <summary>Галочка «Запускать вместе с Windows».</summary>
    public bool Enabled
    {
        get => settings.Autostart ?? _serviceMode == false;
        set
        {
            if (settings.Autostart == value && Enabled == value) return;
            settings.Autostart = value;
            try { settings.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed(Texts.Friendly(e)); }
            Apply();
            Raise();
        }
    }

    public string Hint => _serviceMode == false
        ? "Программа запустится свёрнутой в трей — с ней включится и оптимизация."
        : "Программа запустится свёрнутой в трей. Оптимизация стартует и без неё.";

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
                if (Store.Get(ValueName) != Command)
                {
                    // Журнал отката: ключа Run не было — запись ДО записи в реестр; путь к exe поправили — обновляется «что поставили».
                    var json = new System.Text.Json.Nodes.JsonObject { ["command"] = Command };
                    string rec = "";
                    if (Recorder.Active(ChangeKinds.Autostart).FirstOrDefault() is { } open) Recorder.Update(open.Id, json);
                    else rec = Recorder.Begin(ChangeKinds.Autostart, "Включён запуск окна вместе с Windows (свёрнутым в трей)",
                        new System.Text.Json.Nodes.JsonObject { ["command"] = null }, json);
                    try { Store.Set(ValueName, Command); }
                    catch { Recorder.Abort(rec); throw; }
                    Recorder.Done(rec);
                }
            }
            else if (Store.Get(ValueName) is not null)
            {
                Store.Remove(ValueName);
                Recorder.Close(ChangeKinds.Autostart, "запуск вместе с Windows выключен в «Настройках»");
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Failed("Не удалось изменить автозапуск: " + e.Message);
        }
    }
}
