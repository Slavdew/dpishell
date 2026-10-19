using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Отметка группы в итоге «Проверить сейчас» на главной: «✓ YouTube».</summary>
public sealed record CheckGroupMark(string Title, bool Ok, bool Browser = false)
{
    /// <summary>⚠ — открывается программой, но не браузером (DNS системы ведёт не туда).</summary>
    public string Mark => !Ok ? "✗" : Browser ? "⚠" : "✓";
}

/// <summary>Место в окне, куда прокрутить по ссылке (<see cref="MainViewModel.ShowRequested"/>).</summary>
public enum Spot { Strategies, PcDns }

/// <summary>
/// Главная по UX-аудиту («Главная»): одно главное действие — выключатель; итог проверки — в карточке состояния;
/// стратегии — под «Сменить стратегию»; помехи — одной строкой «Нашлось N помех», раскрыта только самая важная.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Помехи на главной в порядке важности: показывается первая, остальные — по «Ещё N». Обновление списков — не помеха (UX-аудит 2, п. 6).</summary>
    enum Issue { BothOn, Conflicts, Dns, Vpn }

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
    public string StrategiesLinkText => ShowStrategies ? "Скрыть стратегии" : "Показать стратегии";
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
        // Как в подборе: группа открывается, если открылись её обязательные адреса (необязательный провал — только в «Подробнее»).
        foreach (var g in CheckRows.GroupBy(r => r.Group)) CheckGroups.Add(new(g.Key, g.All(r => r.Ok || !r.Alert), g.Any(r => r.HasBrowser)));
        Raise(nameof(HasCheck));
        Raise(nameof(BrowserWarning)); Raise(nameof(HasBrowserWarning)); Raise(nameof(ShowBrowserDnsFix));
        RaiseIssues();
    }

    /// <summary>«⚠ В браузере не откроется: YouTube — DNS провайдера подменяет адрес.» Пусто — браузер открывает то же, что программа.</summary>
    public string BrowserWarning
    {
        get
        {
            var bad = CheckRows.Where(r => r.HasBrowser).ToList();
            if (bad.Count == 0) return "";
            // Явная подмена и hosts — «в браузере не откроется»; просто «через DNS Windows не открылось» — про программы, берущие адрес у Windows.
            var parts = bad.GroupBy(r => r.Browser).Select(g =>
            {
                var groups = string.Join(", ", g.Select(r => r.Group).Distinct());
                return g.Key == BrowserPath.WindowsOnlyReason ? $"{g.Key}: {groups}." : $"В браузере не откроется: {groups} — {g.Key}.";
            });
            return "⚠ " + string.Join(" ", parts);
        }
    }
    public bool HasBrowserWarning => CheckRows.Any(r => r.HasBrowser);
    /// <summary>Кнопка «Включить защищённый DNS» — когда виноват DNS (подмена или просто не открылось через DNS Windows), не hosts.</summary>
    public bool ShowBrowserDnsFix => CheckRows.Any(r => r.Browser is BrowserPath.DnsReason or BrowserPath.WindowsOnlyReason);

    // ---------- помехи ----------

    List<Issue> Issues()
    {
        var list = new List<Issue>();
        if (Router.BothOn) list.Add(Issue.BothOn);
        if (HasConflicts) list.Add(Issue.Conflicts);
        // Кнопка DNS уже в карточке состояния — второй такой же плашкой и помехой в счёте она не нужна.
        if (DnsSpoofed && !ShowBrowserDnsFix) list.Add(Issue.Dns);
        if (HasVpn) list.Add(Issue.Vpn);
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
    /// <summary>Обновление списков — отдельной карточкой, всегда видно и не под ⚠.</summary>
    public bool ShowUpdate => Updates.DataAvailable;

    /// <summary>Строка «Нашлось N помех» — когда плашек больше одной (одна — видна и так).</summary>
    public bool HasIssuesHeader => Issues().Count > 1;

    public string IssuesTitle => Plural(Issues().Count);

    /// <summary>«Ещё 2» — сколько скрыто (одна уже видна): числа сходятся с заголовком.</summary>
    public string IssuesToggleText => ShowAllIssues ? "Свернуть" : $"Ещё {Issues().Count - 1}";

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
