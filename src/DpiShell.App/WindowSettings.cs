using System.IO;
using System.Text.Json;
using DpiShell.Core;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>
/// Настройки окна — %LocalAppData%\DpiShell\window.json (helper-protocol §4): принадлежат пользователю, права не нужны.
/// Тема, крестик, адрес и пароль роутера (пароль — DPAPI CurrentUser, см. RouterSecrets). Обходные настройки — у помощника.
/// </summary>
/// <summary>Роутер, на котором работает обход: адрес и MAC (null — не узнали; тогда «тот же роутер» не подтвердить).</summary>
public sealed record RouterBypassMark(string Address, string? Mac);

public sealed class WindowSettings
{
    /// <summary>"system" / "light" / "dark".</summary>
    public string Theme { get; set; } = "system";
    /// <summary>"tray" — крестик сворачивает в трей, "exit" — закрывает программу.</summary>
    public string CloseAction { get; set; } = "tray";
    /// <summary>Адрес роутера; null — основной шлюз.</summary>
    public string? RouterAddress { get; set; }
    /// <summary>Пароль роутера под DPAPI (CurrentUser); открытым не хранится.</summary>
    public string? RouterPassword { get; set; }
    /// <summary>
    /// Обход «живёт» на роутере: в последний раз роутер ответил «включён» (router-api §6). Адрес и MAC — узнать тот же роутер,
    /// когда он не отвечает (обновление, перезагрузка): тогда при старте окно не оставляет и не включает обход на ПК. null — не живёт.
    /// </summary>
    public RouterBypassMark? RouterBypass { get; set; }
    /// <summary>Показывать инструменты для опытных (пока — «Отладка подключений» на вкладке «Игры и программы»).</summary>
    public bool ShowAdvanced { get; set; }
    /// <summary>Подсказку «свернулась в трей» уже показывали.</summary>
    public bool TrayHintShown { get; set; }
    /// <summary>Окно при входе в Windows (в трее). Null — не выбирали: по режиму работы (architecture.md «Автозапуск»).</summary>
    public bool? Autostart { get; set; }
    /// <summary>Последняя попытка проверить обновления (раз в день при запуске окна).</summary>
    public DateTime? UpdatesCheckedAt { get; set; }
    /// <summary>Последняя удачная проверка.</summary>
    public DateTime? UpdatesOkAt { get; set; }
    /// <summary>Версия данных у источника, если она новее установленной (показываем до установки).</summary>
    public string? UpdatesData { get; set; }
    /// <summary>Новая версия программы у источника.</summary>
    public string? UpdatesApp { get; set; }
    /// <summary>Свои варианты DNS — одни и те же для этого компьютера и для роутера (architecture.md «DNS этого компьютера»).</summary>
    public List<DnsCustomEntry> DnsCustom { get; set; } = [];
    /// <summary>Защищённый DNS не отвечает — разрешить обычный (по умолчанию нет: только защищённый).</summary>
    public bool DnsFallback { get; set; }

    /// <summary>Порядок списка стратегий: "result" (по числу открывшихся сайтов) или "name" (по имени). Один на оба списка и на ПК, и на роутере.</summary>
    public string StrategySort { get; set; } = StrategyOrder.ByResult;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool SortByName => StrategySort == StrategyOrder.ByName;
    /// <summary>Порядок сменили — списки перестраиваются.</summary>
    public event Action? StrategySortChanged;

    public void SetStrategySort(bool byName)
    {
        var v = byName ? StrategyOrder.ByName : StrategyOrder.ByResult;
        if (v == StrategySort) return;
        StrategySort = v;
        try { Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        StrategySortChanged?.Invoke();
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>Папка окна: %LocalAppData%\DpiShell (логи окна, выгруженные результаты, window.json).</summary>
    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DpiShell");

    /// <summary>Куда сохранять; задаётся при загрузке.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string File { get; private set; } = Path.Combine(DefaultDir, "window.json");

    /// <summary>
    /// Отпечатки ключей SSH роутеров (установка и удаление по SSH) — router-hostkeys.json рядом с window.json:
    /// первое подключение запоминает ключ, смена — вопрос «Доверять новому» (переустановили прошивку).
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IRouterHostKeyStore HostKeys => _hostKeys ??= new FileHostKeyStore(Path.Combine(Path.GetDirectoryName(File)!, "router-hostkeys.json"));
    IRouterHostKeyStore? _hostKeys;

    /// <summary>Кэш архива ядра для роутера (тот же zip, что у ПК; проверяется по хешу) — рядом с window.json.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string RouterCoreCache => Path.Combine(Path.GetDirectoryName(File)!, "router-core");

    /// <summary>
    /// Прочитать; файла нет — перенести тему, крестик и роутер из прежнего state.json, если он читается
    /// (до помощника программа работала с правами и хранила всё в %ProgramData%).
    /// </summary>
    public static WindowSettings Load(string? file = null, AppPaths? legacy = null)
    {
        file ??= Path.Combine(DefaultDir, "window.json");
        WindowSettings s;
        try
        {
            s = System.IO.File.Exists(file) ? Parse(System.IO.File.ReadAllText(file), file) : Migrate(legacy);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { s = new(); }
        s.File = file;
        return s;
    }

    /// <summary>
    /// Разобрать window.json. Одно поле не того вида (файл писала другая сборка) не стирает остальное — прежде так терялся
    /// и сохранённый пароль роутера: поля читаются по одному, непрочитанные остаются по умолчанию. Файл не JSON — копия в .bad.
    /// </summary>
    public static WindowSettings Parse(string text, string? file = null)
    {
        try { return JsonSerializer.Deserialize<WindowSettings>(text, Json) ?? new(); }
        catch (JsonException) { }
        var s = new WindowSettings();
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException("не объект");
            var props = typeof(WindowSettings).GetProperties().Where(p => p.CanWrite && p.GetSetMethod() is not null)
                .ToDictionary(p => Json.PropertyNamingPolicy!.ConvertName(p.Name));
            foreach (var x in doc.RootElement.EnumerateObject())
            {
                if (!props.TryGetValue(x.Name, out var p)) continue;
                try { p.SetValue(s, x.Value.Deserialize(p.PropertyType, Json)); }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { }
            }
        }
        catch (JsonException)
        {
            if (file is not null)
                try { System.IO.File.Copy(file, file + ".bad", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return s;
    }

    static WindowSettings Migrate(AppPaths? legacy)
    {
        var s = new WindowSettings();
        if (legacy is null) return s;
        try
        {
            if (!System.IO.File.Exists(legacy.StateFile)) return s;
            var old = AppState.Load(legacy);
            s.Theme = old.Theme;
            s.CloseAction = old.CloseAction;
            s.RouterAddress = old.RouterAddress;
            // DPAPI CurrentUser: пароль расшифруется, только если его сохранял этот же пользователь.
            s.RouterPassword = RouterSecrets.Unprotect(old.RouterPassword) is not null ? old.RouterPassword : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var tmp = File + ".tmp";
        System.IO.File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        System.IO.File.Move(tmp, File, overwrite: true);
    }
}

/// <summary>Свой вариант DNS: имя, 1–2 адреса и необязательный шаблон защищённого DNS (https).</summary>
public sealed record DnsCustomEntry(string Name, List<string> Ips, string? Doh = null);
