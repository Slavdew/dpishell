using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Группа на вкладке «Сайты» → «Свои сайты» (API 1.18): имя и сайты по строке. Пустое имя — «Прочее» (сайты без группы), оно всегда последнее,
/// не переименовывается и не удаляется. Группы сворачиваются; имя правится на месте.
/// </summary>
public sealed class SiteGroupRow : Observable
{
    readonly Action _changed;
    string _name, _text, _editName = "";
    bool _expanded = true, _editing;

    public SiteGroupRow(string name, string text, Action changed)
    {
        _name = name; _text = text; _changed = changed;
    }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) { Raise(nameof(Title)); _changed(); } }
    }

    public bool IsOther => _name.Length == 0;
    /// <summary>Имя на языке окна: «Прочее» — по ключу.</summary>
    public string Title => IsOther ? L.T("select.sub.other") : _name;
    /// <summary>Группу можно переименовать и удалить.</summary>
    public bool Named => !IsOther;

    /// <summary>Сайты по строке — то, что видит и правит человек.</summary>
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) { Raise(nameof(Count)); Raise(nameof(CountText)); _changed(); } }
    }

    /// <summary>Непустые строки, без пробелов по краям.</summary>
    public List<string> Lines() => [.. _text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    public int Count => Lines().Count;
    public string CountText => L.T("sites.group.count", new { n = Count });

    public bool Expanded { get => _expanded; set => Set(ref _expanded, value); }

    /// <summary>Имя правится на месте: пока Editing, в шапке поле ввода.</summary>
    public bool Editing { get => _editing; set { if (Set(ref _editing, value)) Raise(nameof(NotEditing)); } }
    public bool NotEditing => !_editing;
    public string EditName { get => _editName; set => Set(ref _editName, value); }

    /// <summary>Строки, которых касается выделение (или строка с курсором, если выделения нет): целиком, без пустых.</summary>
    public static List<string> LinesAt(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        int end = Math.Clamp(start + Math.Max(length, 0), start, text.Length);
        int from = start == 0 ? 0 : text.LastIndexOf('\n', start - 1) + 1;
        int to = end > start && text[end - 1] == '\n' ? end - 1 : end;
        int stop = text.IndexOf('\n', Math.Min(to, text.Length));
        var piece = text[from..(stop < 0 ? text.Length : stop)];
        return [.. piece.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
    }
}

public sealed partial class MainViewModel
{
    // ---------- «Сайты» → «Свои сайты»: группы ----------

    ObservableCollection<SiteGroupRow>? _siteGroups;
    /// <summary>Группы своих сайтов: именованные по порядку, «Прочее» последней (всегда есть).</summary>
    public ObservableCollection<SiteGroupRow> SiteGroupRows => _siteGroups ??= [new SiteGroupRow("", "", OnGroupsEdited)];

    SiteGroupRow OtherGroup => SiteGroupRows[^1];

    /// <summary>Шапки групп показываются, когда есть именованные: без них «Свои сайты» — один список, как раньше.</summary>
    public bool GroupHeaders => SiteGroupRows.Any(g => g.Named);

    /// <summary>Любая правка групп — прежний итог «Сохранено сайтов: N» уже не про то, что в окне.</summary>
    void OnGroupsEdited()
    {
        // Единственная группа — «Прочее» без шапки: свёрнутой её не оставляем, иначе список пропал бы без следа.
        if (!SiteGroupRows.Any(g => g.Named) && !OtherGroup.Expanded) OtherGroup.Expanded = true;
        Raise(nameof(GroupHeaders));
        TargetsMessage = "";
        Raise(nameof(TargetsText));
        RaiseSitesDirty();
    }

    /// <summary>
    /// Все сайты подряд (именованные группы, затем «Прочее»), по строке. Запись: сайт, который уже в какой-то группе, остаётся в ней;
    /// новый — в «Прочее»; пропавший из текста — убирается. Нужно тем, кто видит список целиком (добавление сайта с Главной, тесты).
    /// </summary>
    public string TargetsText
    {
        get => string.Join(Environment.NewLine, SiteGroupRows.SelectMany(g => g.Lines()));
        set
        {
            var lines = value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var want = new HashSet<string>(lines, StringComparer.OrdinalIgnoreCase);
            var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in SiteGroupRows)
            {
                var keep = g.Lines().Where(l => want.Contains(l) && placed.Add(l)).ToList();
                if (!keep.SequenceEqual(g.Lines())) g.Text = string.Join(Environment.NewLine, keep);
            }
            var rest = lines.Where(placed.Add).ToList();
            if (rest.Count > 0)
            {
                var other = OtherGroup;
                other.Text = string.Join(Environment.NewLine, other.Lines().Concat(rest));
            }
            OnGroupsEdited();
        }
    }

    /// <summary>Группы из окна для отправки помощнику: имена и сайты как введены; «Прочее» — с пустым именем.</summary>
    List<SiteGroup> DraftGroups() => [.. SiteGroupRows.Select(g => new SiteGroup(g.Name, g.Lines()))];

    /// <summary>Отпечаток черновика или сохранённого: сайт + группа, плюс сами группы (пустая группа — тоже правка). Порядок строк неважен.</summary>
    static HashSet<string> Signature(IEnumerable<SiteGroup> groups, bool withGroups)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in groups)
        {
            var name = withGroups ? SiteGroupRows_Name(g.Name) : "";
            if (name.Length > 0) set.Add("g:" + name);
            foreach (var s in g.Sites)
                if (DataStore.NormalizeDomain(s) is { } d) set.Add("s:" + d + "|" + name);
        }
        return set;

        static string SiteGroupRows_Name(string n) => SiteGroups.CleanName(n);
    }

    /// <summary>Помощник умеет группы (API 1.18); старый хранит плоский список — тогда группы окна в сравнение с сохранённым не входят.</summary>
    bool GroupsSupported => Helper.Reader is not { } c || c.Compat.Supports("user_groups_set");

    /// <summary>Сохранённое у помощника: сайты по группам.</summary>
    List<SiteGroup> SavedGroups() =>
        SiteGroups.Apply(Helper.Status?.UserTargets ?? [], Helper.Status?.UserGroups);

    /// <summary>Имена сохранённых групп по порядку — по ним окно расшифровывает «группа №N» из истории.</summary>
    IReadOnlyList<string> SavedGroupNames() => [.. SavedGroups().Where(g => g.Name.Length > 0).Select(g => g.Name)];

    void LoadGroups(IReadOnlyList<SiteGroup> groups)
    {
        var old = SiteGroupRows.ToDictionary(g => g.Name, g => g.Expanded, StringComparer.OrdinalIgnoreCase);
        SiteGroupRows.Clear();
        var list = groups.Where(g => g.Name.Length > 0).Concat(groups.Where(g => g.Name.Length == 0).DefaultIfEmpty(new SiteGroup())).ToList();
        // Групп много — все, кроме «Прочее», свёрнуты: список не превращается в простыню.
        bool fold = list.Count > 4;
        foreach (var g in list)
            SiteGroupRows.Add(new SiteGroupRow(g.Name, string.Join(Environment.NewLine, g.Sites), OnGroupsEdited)
            { Expanded = old.TryGetValue(g.Name, out var e) ? e : !(fold && g.Name.Length > 0) });
        OnGroupsEdited();
    }

    // Первый ответ помощника — группы и сайты в окно (дальше окно — черновик человека).
    void TakeTargetsFromHelper(HelperStatus s)
    {
        if (_targetsFromHelper) return;
        _targetsFromHelper = true;
        if (s.UserTargets.Count > 0 || s.UserGroups is { Count: > 0 }) LoadGroups(SiteGroups.Apply(s.UserTargets, s.UserGroups));
    }

    /// <summary>В окне не то, что сохранено: другие сайты или другие группы.</summary>
    public bool TargetsDirty
    {
        get
        {
            bool groups = GroupsSupported;
            return !Signature(SavedGroups(), groups).SetEquals(Signature(DraftGroups(), groups));
        }
    }

    // ---------- сводка подбора по группам ----------

    IReadOnlyList<TargetGroup> _reportTested = [];

    /// <summary>
    /// Сводка по выбранной строке подбора (лучшая выбрана сразу): строка на группу своих сайтов — «Telegram ✗ 0/12»,
    /// «Прочее ✓ 7/8 — не открыто: example.org», «проверено 10 из 120», «заблокировано по адресу…». Пусто — своих сайтов не проверяли.
    /// </summary>
    public IReadOnlyList<string> ResultGroupLines =>
        SelectedScore is { Score: { Error: null } score }
            ? [.. SiteGroupSummary.Build(score, _reportTested).Select(l => Texts.SubLineText(l, Names))]
            : [];
    public bool HasResultGroupLines => ResultGroupLines.Count > 0;

    void RaiseResultGroupLines() { Raise(nameof(ResultGroupLines)); Raise(nameof(HasResultGroupLines)); }

    // ---------- действия над группами ----------

    public ICommand AddGroupCmd => _addGroupCmd ??= new RelayCommand(() => NewGroup(), () => SiteGroupRows.Count <= SiteGroups.MaxGroups);
    ICommand? _addGroupCmd;

    public ICommand RenameGroupCmd => _renameGroupCmd ??= new RelayCommand(o =>
    {
        if (o is not SiteGroupRow g || g.IsOther) return;
        if (g.Editing) CommitRename(g);
        else { g.EditName = g.Name; g.Editing = true; }
    });
    ICommand? _renameGroupCmd;

    public ICommand CancelRenameCmd => _cancelRenameCmd ??= new RelayCommand(o => { if (o is SiteGroupRow g) g.Editing = false; });
    ICommand? _cancelRenameCmd;

    public ICommand DeleteGroupCmd => _deleteGroupCmd ??= new RelayCommand(o => { if (o is SiteGroupRow g) DeleteGroup(g); });
    ICommand? _deleteGroupCmd;

    string UniqueGroupName(string baseName)
    {
        var name = baseName;
        for (int i = 2; SiteGroupRows.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{baseName} {i}";
        return name;
    }

    /// <summary>Новая группа перед «Прочее», с именем по умолчанию; имя сразу правится.</summary>
    public SiteGroupRow NewGroup(string? name = null)
    {
        var row = new SiteGroupRow(UniqueGroupName(SiteGroups.CleanName(name) is { Length: > 0 } n ? n : L.T("sites.group.new_name")), "", OnGroupsEdited) { Expanded = true };
        SiteGroupRows.Insert(SiteGroupRows.Count - 1, row);
        row.EditName = row.Name;
        row.Editing = name is null;
        OnGroupsEdited();
        return row;
    }

    /// <summary>Имя занято или пусто — остаётся прежнее, об этом сказано в строке состояния.</summary>
    public bool CommitRename(SiteGroupRow g)
    {
        var name = SiteGroups.CleanName(g.EditName);
        g.Editing = false;
        if (name.Length == 0 || string.Equals(name, g.Name, StringComparison.Ordinal)) return name.Length > 0;
        if (SiteGroupRows.Any(x => x != g && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Message = L.T("sites.group.name_taken", new { name });
            return false;
        }
        g.Name = name;
        return true;
    }

    /// <summary>Удалить группу вместе с сайтами — после подтверждения (в «Сохранить» это и уйдёт к помощнику).</summary>
    public bool DeleteGroup(SiteGroupRow g)
    {
        if (g.IsOther) return false;
        if (!Confirm(L.T("sites.group.delete_confirm", new { name = g.Name, n = g.Count }))) return false;
        SiteGroupRows.Remove(g);
        OnGroupsEdited();
        return true;
    }

    /// <summary>Перенести строки из одной группы в другую (target — null: в новую группу).</summary>
    public void MoveSites(SiteGroupRow from, IReadOnlyCollection<string> lines, SiteGroupRow? target)
    {
        if (lines.Count == 0) return;
        target ??= NewGroup();
        if (target == from) return;
        var move = new HashSet<string>(lines.Select(l => l.Trim()), StringComparer.OrdinalIgnoreCase);
        var moved = from.Lines().Where(move.Contains).ToList();
        if (moved.Count == 0) return;
        from.Text = string.Join(Environment.NewLine, from.Lines().Where(l => !move.Contains(l)));
        target.Text = string.Join(Environment.NewLine, target.Lines().Concat(moved));
        target.Expanded = true;
    }
}
