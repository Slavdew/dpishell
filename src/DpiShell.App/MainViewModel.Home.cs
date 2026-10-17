using System.Collections.ObjectModel;
using System.Windows.Input;

namespace DpiShell.App;

/// <summary>Отметка группы в итоге «Проверить сейчас» на главной: «✓ YouTube».</summary>
public sealed record CheckGroupMark(string Title, bool Ok)
{
    public string Mark => Ok ? "✓" : "✗";
}

/// <summary>Место в окне, куда прокрутить по ссылке (<see cref="MainViewModel.ShowRequested"/>).</summary>
public enum Spot { Strategies, PcDns }

/// <summary>
/// Главная по UX-аудиту («Главная»): одно главное действие — выключатель; итог проверки — в карточке состояния;
/// стратегии — под «Сменить стратегию»; помехи — одной строкой «Нашлось N помех», раскрыта только самая важная.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Помехи на главной в порядке важности: показывается первая, остальные — по «Показать все».</summary>
    enum Issue { BothOn, Conflicts, Dns, Vpn, Update }

    /// <summary>Показать место на странице: окно прокручивает к нему (и раскрывает список DNS). Ссылка не должна «ничего не делать» (UX-аудит 2, п. 2, 4).</summary>
    public event Action<Spot>? ShowRequested;

    void InitHome()
    {
        CheckRows.CollectionChanged += (_, _) => RebuildCheckGroups();
        Updates.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UpdatesViewModel.DataAvailable)) RaiseIssues(); };
    }

    // ---------- выключатель ----------

    /// <summary>Обход делает роутер, на ПК выключен: кнопка ПК — второстепенная «Включить и на этом ПК» (п. 7 аудита).</summary>
    public bool PcSecondary => !IsRunning && Router.Found && Router.RouterEnabled;
    public bool PcPrimary => !PcSecondary;

    // ---------- стратегии ----------

    bool _showStrategies;
    /// <summary>Список стратегий на главной раскрыт («Сменить стратегию»). По умолчанию свёрнут: менять руками нужно редко.</summary>
    public bool ShowStrategies
    {
        get => _showStrategies;
        set
        {
            if (!Set(ref _showStrategies, value)) return;
            Raise(nameof(StrategiesLinkText));
            if (value) ShowRequested?.Invoke(Spot.Strategies);
        }
    }
    public string StrategiesLinkText => ShowStrategies ? "Скрыть стратегии" : "Сменить стратегию";
    public ICommand ToggleStrategiesCmd => _toggleStrategiesCmd ??= new RelayCommand(() => ShowStrategies = !ShowStrategies);
    ICommand? _toggleStrategiesCmd;

    public ICommand GoRouterCmd => _goRouterCmd ??= new RelayCommand(() => Page = Pages.Router);
    ICommand? _goRouterCmd;

    // ---------- итог «Проверить сейчас» ----------

    /// <summary>По группе: открылось всё — ✓. Строится из строк проверки (адреса — в «Подробнее»).</summary>
    public ObservableCollection<CheckGroupMark> CheckGroups { get; } = [];
    public bool HasCheck => CheckRows.Count > 0 || Checking;

    string _checkedAt = "";
    /// <summary>«в 14:05» — когда проверяли.</summary>
    public string CheckedAt { get => _checkedAt; private set => Set(ref _checkedAt, value); }

    void RebuildCheckGroups()
    {
        CheckGroups.Clear();
        foreach (var g in CheckRows.GroupBy(r => r.Group)) CheckGroups.Add(new(g.Key, g.All(r => r.Ok)));
        Raise(nameof(HasCheck));
    }

    // ---------- помехи ----------

    List<Issue> Issues()
    {
        var list = new List<Issue>();
        if (Router.BothOn) list.Add(Issue.BothOn);
        if (HasConflicts) list.Add(Issue.Conflicts);
        if (DnsSpoofed) list.Add(Issue.Dns);
        if (HasVpn) list.Add(Issue.Vpn);
        if (Updates.DataAvailable) list.Add(Issue.Update);
        return list;
    }

    bool _showAllIssues;
    public bool ShowAllIssues
    {
        get => _showAllIssues;
        set { if (Set(ref _showAllIssues, value)) RaiseIssues(); }
    }
    public ICommand ToggleIssuesCmd => _toggleIssuesCmd ??= new RelayCommand(() => ShowAllIssues = !ShowAllIssues);
    ICommand? _toggleIssuesCmd;

    bool Shown(Issue i)
    {
        var list = Issues();
        return list.Contains(i) && (ShowAllIssues || list[0] == i);
    }

    public bool ShowBothOn => Shown(Issue.BothOn);
    public bool ShowConflicts => Shown(Issue.Conflicts);
    public bool ShowDns => Shown(Issue.Dns);
    public bool ShowVpn => Shown(Issue.Vpn);
    public bool ShowUpdate => Shown(Issue.Update);

    /// <summary>Строка «Нашлось N помех» — когда плашек больше одной (одна — видна и так).</summary>
    public bool HasIssuesHeader => Issues().Count > 1;

    public string IssuesTitle
    {
        get
        {
            var list = Issues();
            int n = list.Count(i => i != Issue.Update);
            var update = list.Contains(Issue.Update) ? (n > 0 ? " · есть обновление списков" : "Есть обновление списков") : "";
            return n == 0 ? update : Plural(n) + update;
        }
    }

    public string IssuesToggleText => ShowAllIssues ? "Свернуть" : $"Показать все ({Issues().Count})";

    /// <summary>«Нашлась 1 помеха», «Нашлось 3 помехи», «Нашлось 5 помех».</summary>
    public static string Plural(int n)
    {
        int d = n % 10, h = n % 100;
        if (d == 1 && h != 11) return $"Нашлась {n} помеха";
        return d is >= 2 and <= 4 && h is < 12 or > 14 ? $"Нашлось {n} помехи" : $"Нашлось {n} помех";
    }

    void RaiseIssues()
    {
        foreach (var p in new[] { nameof(ShowBothOn), nameof(ShowConflicts), nameof(ShowDns), nameof(ShowVpn), nameof(ShowUpdate),
                     nameof(HasIssuesHeader), nameof(IssuesTitle), nameof(IssuesToggleText) })
            Raise(p);
    }
}
