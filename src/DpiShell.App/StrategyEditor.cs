using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Поле одной роли в окне правки: моноширинный текст, проверка белым списком на лету.</summary>
public sealed class RoleField : Observable
{
    string _text = "";
    string _error = "";

    public RoleField(string role, string text, Func<string, bool>? fakeExists)
    {
        Role = role;
        FakeExists = fakeExists;
        _text = text;
        _error = Check(text);
    }

    Func<string, bool>? FakeExists { get; }
    public string Role { get; }
    public string Label => Role switch
    {
        "quic" => "QUIC (по спискам)",
        "discord_udp" => "Голос Discord",
        "discord_media" => "Медиа Discord",
        "google" => "YouTube и Google",
        "general" => "Остальные сайты",
        "ipset_udp" => "UDP по IP-списку",
        "ipset_tcp" => "TCP по IP-списку",
        "game_tcp" => "Игры, TCP",
        "game_udp" => "Игры, UDP",
        _ => Role,
    };
    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;
            Error = Check(value);
            Changed?.Invoke();
        }
    }
    /// <summary>Что не так, одной фразой; пусто — в порядке.</summary>
    public string Error { get => _error; private set { if (Set(ref _error, value)) Raise(nameof(HasError)); } }
    public bool HasError => Error.Length > 0;
    public event Action? Changed;

    /// <summary>Переводы строк и повторные пробелы — в один пробел: поле многострочное, а параметры разделяются пробелом.</summary>
    public static string Normalize(string text) => Regex.Replace(text.Trim(), @"\s+", " ");
    public string Normalized => Normalize(Text);

    string Check(string text) => Normalize(text) is { Length: > 0 } t ? WinwsArgs.Problem(Role, t, FakeExists) ?? "" : "";
}

/// <summary>
/// Простое окно правки стратегии: название и поля по ролям. «Сохранить» — новая своя стратегия (прежняя не меняется);
/// каталожная правится только копией. Проверка — тем же белым списком, что у каталога и вставки (<see cref="StrategyExchange.CheckRoles"/>).
/// </summary>
public sealed class StrategyEditor : Observable
{
    readonly string _original;
    readonly Func<string, bool>? _fakeExists;
    string _title;

    /// <param name="original">Название исходной стратегии: новое имя должно отличаться.</param>
    /// <param name="catalog">Копия готовой стратегии (заголовок окна и подсказка другие).</param>
    public StrategyEditor(string original, string title, IReadOnlyDictionary<string, string> roles, bool catalog, Func<string, bool>? fakeExists = null)
    {
        _original = original;
        _title = title;
        Catalog = catalog;
        _fakeExists = fakeExists;
        foreach (var role in WinwsArgs.RoleOrder.Where(roles.ContainsKey))
            Add(role, roles[role]);
        RefreshMissing();
        SaveCmd = new RelayCommand(() => { Saved = true; Close?.Invoke(); }, () => CanSave);
        CancelCmd = new RelayCommand(() => Close?.Invoke());
        AddRoleCmd = new RelayCommand(p => { if (p is string r) AddRole(r); });
        ResetCommands();
    }

    void ResetCommands() => CommandManager.InvalidateRequerySuggested();

    public bool Catalog { get; }
    public string Header => Catalog ? "Копия стратегии" : "Изменить стратегию";
    public string Note => Catalog
        ? "Готовые стратегии не меняются — сохранится ваша копия под новым именем."
        : "Сохранится новая своя стратегия, прежняя останется.";
    public ObservableCollection<RoleField> Fields { get; } = [];
    /// <summary>Роли, которых в стратегии нет: можно добавить.</summary>
    public ObservableCollection<string> MissingRoles { get; } = [];
    public bool HasMissing => MissingRoles.Count > 0;

    public string Title
    {
        get => _title;
        set { if (Set(ref _title, value)) Revalidate(); }
    }

    string _titleError = "";
    public string TitleError { get => _titleError; private set { if (Set(ref _titleError, value)) Raise(nameof(HasTitleError)); } }
    public bool HasTitleError => TitleError.Length > 0;

    string _summary = "";
    /// <summary>Итог проверки: что мешает сохранению (первая причина); пусто — можно сохранять.</summary>
    public string Summary { get => _summary; private set { if (Set(ref _summary, value)) Raise(nameof(HasSummary)); } }
    public bool HasSummary => Summary.Length > 0;
    /// <summary>Внизу показываем причину, только если под полем нет своей красной строки: одна ошибка за раз.</summary>
    public bool ShowSummary => HasSummary && !Fields.Any(f => f.HasError);

    public bool CanSave => !HasSummary;
    public bool Saved { get; private set; }
    public event Action? Close;

    public ICommand SaveCmd { get; }
    public ICommand CancelCmd { get; }
    public ICommand AddRoleCmd { get; }

    /// <summary>Что сохранять: очищенное название и непустые роли.</summary>
    public (string Title, Dictionary<string, string> Roles) Result =>
        (StrategyExchange.CleanTitle(Title), Fields.Where(f => f.Normalized.Length > 0).ToDictionary(f => f.Role, f => f.Normalized));

    public void AddRole(string role)
    {
        if (!MissingRoles.Contains(role)) return;
        Add(role, "");
        RefreshMissing();
        Revalidate();
    }

    void Add(string role, string text)
    {
        var f = new RoleField(role, text, _fakeExists);
        f.Changed += Revalidate;
        Fields.Add(f);
    }

    void RefreshMissing()
    {
        MissingRoles.Clear();
        foreach (var r in WinwsArgs.RoleOrder.Where(r => Fields.All(f => f.Role != r))) MissingRoles.Add(r);
        Raise(nameof(HasMissing));
    }

    void Revalidate()
    {
        var clean = StrategyExchange.CleanTitle(Title);
        TitleError = string.IsNullOrWhiteSpace(Title) ? "Введите название"
            : string.Equals(clean, StrategyExchange.CleanTitle(_original), StringComparison.Ordinal) ? "Нужно новое название — прежняя стратегия останется"
            : "";
        var bad = Fields.FirstOrDefault(f => f.HasError);
        var roles = Fields.Where(f => f.Normalized.Length > 0).ToDictionary(f => f.Role, f => f.Normalized);
        Summary = HasTitleError ? TitleError
            : bad is not null ? $"{bad.Label}: {bad.Error}"
            : roles.Count == 0 ? "Нужна хотя бы одна роль с параметрами"
            : StrategyExchange.CheckRoles(roles, _fakeExists) ?? "";
        Raise(nameof(CanSave));
        Raise(nameof(ShowSummary));
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>Первая проверка после создания (поля уже заполнены).</summary>
    public StrategyEditor Start() { Revalidate(); return this; }
}
