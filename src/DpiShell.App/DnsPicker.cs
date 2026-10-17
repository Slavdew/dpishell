using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Вариант DNS в списке: готовый (с ПК или с роутера) или свой из window.json.</summary>
/// <param name="Ips">Адреса (для своего — то, что ввёл человек; для готовых — для подписи).</param>
/// <param name="Doh">Шаблон защищённого DNS; null — только обычный.</param>
public sealed record DnsOption(string Id, string Title, string Detail, bool Custom, IReadOnlyList<string> Ips, string? Doh)
{
    public override string ToString() => Title;
}

/// <summary>
/// Выбор DNS — общий для этого компьютера и роутера (одинаковые вид и поведение): список готовых и своих вариантов,
/// «Применить», «Вернуть как было», добавить/удалить свой (хранится в window.json и годится для обоих).
/// Что делать по «Применить» и «Вернуть», решает владелец (ПК — помощник, роутер — dns_set).
/// </summary>
public sealed class DnsPicker : Observable
{
    readonly WindowSettings _s;
    IReadOnlyList<DnsOption> _presets = [];

    public DnsPicker(WindowSettings settings)
    {
        _s = settings;
        ApplyCmd = RelayCommand.Async(() => Selected is { } o ? Apply(o) : Task.CompletedTask, () => !Working && Selected is not null && Enabled());
        RestoreCmd = RelayCommand.Async(() => Restore(), () => !Working && CanRestore && Enabled());
        AddCustomCmd = new RelayCommand(AddCustom);
        RemoveCustomCmd = new RelayCommand(p => RemoveCustom(p as DnsOption), p => p is DnsOption { Custom: true });
        Rebuild();
    }

    /// <summary>Включить выбранный вариант.</summary>
    public Func<DnsOption, Task> Apply { get; set; } = _ => Task.CompletedTask;
    public Func<Task> Restore { get; set; } = () => Task.CompletedTask;
    /// <summary>Можно ли сейчас действовать (роутер подключён, окно не занято).</summary>
    public Func<bool> Enabled { get; set; } = () => true;
    /// <summary>Щит на «Применить»: на ПК нужны права (помощник), на роутере — нет.</summary>
    public bool ShowShield { get; init; }
    /// <summary>Галочка «разрешить обычный DNS» (на ПК с Windows 11).</summary>
    bool _showFallback;
    public bool ShowFallback { get => _showFallback; set => Set(ref _showFallback, value); }

    public ObservableCollection<DnsOption> Options { get; } = [];

    DnsOption? _selected;
    public DnsOption? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) { Raise(nameof(SelectedDetail)); CommandManager.InvalidateRequerySuggested(); } }
    }
    public string SelectedDetail => Selected?.Detail ?? "";

    string _current = "";
    /// <summary>«Сейчас: Google, защищённый · Ethernet, Wi-Fi».</summary>
    public string CurrentText { get => _current; set => Set(ref _current, value); }

    string _note = "";
    /// <summary>Вторая строка: что не трогаем, Windows 10 без шифрования и т. п.</summary>
    public string Note { get => _note; set { if (Set(ref _note, value)) Raise(nameof(HasNote)); } }
    public bool HasNote => Note.Length > 0;

    string? _currentId;
    /// <summary>Включённый вариант: выбирается в списке, пока человек не выбрал другой.</summary>
    public string? CurrentId
    {
        get => _currentId;
        set
        {
            if (!Set(ref _currentId, value)) return;
            if (value is not null && Options.FirstOrDefault(o => o.Id == value) is { } o) Selected = o;
        }
    }

    bool _canRestore;
    public bool CanRestore { get => _canRestore; set { if (Set(ref _canRestore, value)) CommandManager.InvalidateRequerySuggested(); } }

    bool _working;
    public bool Working { get => _working; set { if (Set(ref _working, value)) CommandManager.InvalidateRequerySuggested(); } }

    public bool Fallback
    {
        get => _s.DnsFallback;
        set
        {
            if (_s.DnsFallback == value) return;
            _s.DnsFallback = value;
            Save();
            Raise();
        }
    }

    public ICommand ApplyCmd { get; }
    public ICommand RestoreCmd { get; }
    public ICommand AddCustomCmd { get; }
    public ICommand RemoveCustomCmd { get; }

    /// <summary>Готовые варианты (ПК — из <see cref="DnsPresets"/>, роутер — из dns_presets); свои добавляются следом.</summary>
    public void SetPresets(IReadOnlyList<DnsOption> presets)
    {
        _presets = presets;
        Rebuild();
    }

    /// <summary>Id своего варианта в списке.</summary>
    public static string CustomId(string name) => "my:" + name;

    void Rebuild()
    {
        var keep = Selected?.Id;
        Options.Clear();
        foreach (var p in _presets) Options.Add(p);
        foreach (var c in _s.DnsCustom)
            Options.Add(new DnsOption(CustomId(c.Name), c.Name, string.Join(", ", c.Ips) + (c.Doh is null ? " · без шифрования" : " · защищённый"), true, c.Ips, c.Doh));
        Selected = Options.FirstOrDefault(o => o.Id == (keep ?? CurrentId)) ?? Options.FirstOrDefault(o => o.Id == CurrentId);
        Raise(nameof(HasCustom));
    }

    public bool HasCustom => _s.DnsCustom.Count > 0;

    // ---------- свой вариант ----------

    string _newName = "", _newIps = "", _newDoh = "", _newError = "";
    public string NewName { get => _newName; set => Set(ref _newName, value); }
    /// <summary>1–2 адреса IPv4 через запятую или пробел.</summary>
    public string NewIps { get => _newIps; set => Set(ref _newIps, value); }
    public string NewDoh { get => _newDoh; set => Set(ref _newDoh, value); }
    public string NewError { get => _newError; private set { if (Set(ref _newError, value)) Raise(nameof(HasNewError)); } }
    public bool HasNewError => NewError.Length > 0;

    bool _adding;
    public bool Adding { get => _adding; set => Set(ref _adding, value); }

    /// <summary>Проверить и добавить свой вариант; ошибка — строкой под полями.</summary>
    public void AddCustom()
    {
        var name = NewName.Trim();
        if (!DnsValues.IsTitle(name)) { NewError = "Название — до 40 символов, без кавычек."; return; }
        if (_s.DnsCustom.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) || _presets.Any(p => string.Equals(p.Title, name, StringComparison.OrdinalIgnoreCase)))
        { NewError = "Такое название уже есть."; return; }
        var parts = NewIps.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        var ips = new List<string>();
        foreach (var p in parts)
        {
            if (DnsValues.NormalizeIp(p) is not { } ip || DnsValues.IsIpv6(ip)) { NewError = $"«{p}» — не адрес IPv4 (четыре числа через точку)."; return; }
            if (!ips.Contains(ip)) ips.Add(ip);
        }
        if (ips.Count is 0 or > 2) { NewError = "Нужен один или два адреса IPv4."; return; }
        var doh = NewDoh.Trim();
        if (doh.Length > 0 && !DnsValues.IsDohTemplate(doh)) { NewError = "Защищённый DNS — адрес вида https://сервер/путь."; return; }
        var entry = new DnsCustomEntry(name, ips, doh.Length > 0 ? doh : null);
        _s.DnsCustom.Add(entry);
        Save();
        NewName = NewIps = NewDoh = ""; NewError = ""; Adding = false;
        Rebuild();
        Selected = Options.First(o => o.Id == CustomId(name));
    }

    public void RemoveCustom(DnsOption? o)
    {
        if (o is not { Custom: true }) return;
        _s.DnsCustom.RemoveAll(c => CustomId(c.Name) == o.Id);
        Save();
        if (Selected?.Id == o.Id) Selected = null;
        Rebuild();
    }

    /// <summary>Свой вариант, совпадающий по адресам (узнать включённый у ПК/роутера).</summary>
    public DnsOption? FindCustom(Func<DnsOption, bool> match) => Options.FirstOrDefault(o => o.Custom && match(o));

    /// <summary>Свои варианты поменялись в другом выборе (ПК ↔ роутер) — перечитать.</summary>
    public void Reload() => Rebuild();

    void Save()
    {
        try { _s.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        CustomChanged?.Invoke();
    }

    /// <summary>Свои варианты изменились — второй выбор (ПК или роутер) перечитывает их.</summary>
    public event Action? CustomChanged;
}
