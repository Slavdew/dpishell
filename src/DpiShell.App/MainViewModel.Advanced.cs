using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Продвинутый режим на этом ПК (вкладка «Сайты» → «На этом ПК», docs/research/mixed-strategies.md §6): настройки и сборка командной строки у помощника
/// (<c>advanced_get</c>/<c>advanced_set</c>, протокол 1.8), окно только показывает и отправляет сайты и выбор стратегии. «Для всех» на ПК — только
/// когда оптимизация работает и после проверки обычных сайтов: они были открыты до — должны остаться открытыми после, иначе режим возвращается.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Карточка «Продвинутый режим» на «Сайтах» для этого ПК; у роутера своя — <see cref="RouterViewModel.Advanced"/>.</summary>
    public AdvancedViewModel AdvancedPc { get; private set; } = null!;

    public const string AllQuestion =
        "«Для всех сайтов»: оптимизация включится для любого сайта, кроме исключений. Часть обычных сайтов может перестать открываться — "
        + "я проверю их до и после, и если что-то сломается, верну «Как сейчас».\n\nВключить?";

    void InitAdvanced()
    {
        AdvancedPc = new AdvancedViewModel(router: false)
        {
            Load = LoadAdvancedPcAsync,
            Save = SaveAdvancedPcAsync,
            Strategies = () => [.. StrategyChoices.Select(c => new AdvancedChoice(c.Id, c.Title))],
            Unavailable = () => Helper.Reader is { } c && !c.Compat.Supports("advanced_set")
                ? "Помощник старее окна и не знает этого режима — обновите его на вкладке «Информация и обновления»." : null,
            Revert = RevertAdvancedPcAsync,
            Builtin = new BuiltinLists(Path.Combine(_shell.Paths.Seed, "lists")),
            Say = s => Message = s,
        };
        AdvancedPc.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AdvancedViewModel.Dirty)) RaiseSitesDirty(); };
        SaveSitesCmd = RelayCommand.Async(SaveSitesAsync, () => !Busy);
    }

    /// <summary>Одна кнопка «Сохранить» на вкладку «Сайты» (на ПК): свои сайты и строки «Свои настройки для сайтов».</summary>
    public ICommand SaveSitesCmd { get; private set; } = null!;

    /// <summary>Есть несохранённое: свои сайты или строки продвинутого режима.</summary>
    public bool SitesDirty => TargetsDirty || ExcludeDirty || AdvancedPc.Dirty;
    public bool SitesSaved => !SitesDirty;

    void RaiseSitesDirty() { Raise(nameof(TargetsDirty)); Raise(nameof(ExcludeDirty)); Raise(nameof(SitesDirty)); Raise(nameof(SitesSaved)); Raise(nameof(TargetsMessage)); Raise(nameof(ExcludeMessage)); }

    async Task SaveSitesAsync()
    {
        if (!await SaveTargetsAsync()) return;
        if (ExcludeDirty && !await SaveExcludeAsync()) return;
        if (AdvancedPc.Dirty && AdvancedPc.Available) await AdvancedPc.ApplyAsync();
        RaiseSitesDirty();
    }

    static AdvancedState ToState(AdvancedInfo i) => new(i.Enabled, i.Others, [.. i.Rows.Select(r => new AdvancedRowState(r.Domains, r.Strategy, r.Resolved))]);

    async Task<AdvancedState?> LoadAdvancedPcAsync()
    {
        if (Helper.Reader is not { } c) return AdvancedState.Off;   // помощника нет: режим «выключен», включение предложит его установить
        if (!c.Compat.Supports("advanced_get")) return null;
        try { return ToState(await c.AdvancedGetAsync()); }
        catch (HelperUnavailableException e) { Helper.MarkLost(e); return AdvancedState.Off; }
    }

    async Task<AdvancedSaveResult> SaveAdvancedPcAsync(AdvancedState next, AdvancedState prev)
    {
        bool toAll = next.Enabled && next.Others == AdvancedSites.OthersAll;
        bool newAll = toAll && !(prev.Enabled && prev.Others == AdvancedSites.OthersAll);
        HashSet<string> before = [];
        if (toAll)
        {
            // «Для всех» проверяется на обычных сайтах — значит, оптимизация должна работать, а сайты — открываться сейчас.
            if (!IsRunning) return new(null, "«Для всех сайтов» проверяется на обычных сайтах, пока оптимизация работает: сначала включите её.");
            if (newAll && !Confirm(AllQuestion)) return new(null, "Не применено.");
            before = await OpenControlSitesAsync();
            if (before.Count == 0) return new(null, "Обычные сайты сейчас не открываются — проверить «Для всех сайтов» нельзя. Попробуйте позже.");
        }
        if (await NeedHelperAsync() is not { } c) return new(null, "Без помощника режим не применить.");
        if (!c.Compat.Supports("advanced_set")) return new(null, AdvancedPc.UnavailableText ?? "Помощник не знает этого режима.");

        AdvancedInfo? info = null;
        if (!await Guard("Применяю…", async () => info = await c.AdvancedSetAsync(next.Enabled, next.Others, Rows(next))))
            return new(null, _error ?? "Не удалось применить.");

        string? warn = null;
        if (toAll)
        {
            var now = await OpenControlSitesAsync();
            var broken = before.Where(u => !now.Contains(u)).Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u).ToList();
            if (broken.Count > 0)
            {
                // Сломалось то, что открывалось: возвращаем прежний вид (с сохранёнными строками), но не режим «Для всех».
                var back = next with { Others = prev.Enabled && prev.Others != AdvancedSites.OthersAll ? prev.Others : AdvancedSites.OthersLists };
                await Guard("Возвращаю как было…", async () => info = await c.AdvancedSetAsync(back.Enabled, back.Others, Rows(back)));
                warn = "Обычные сайты перестали открываться (" + string.Join(", ", broken.Take(3)) + (broken.Count > 3 ? " и ещё " + (broken.Count - 3) : "")
                    + ") — вернул «" + (info?.Others == AdvancedSites.OthersDirect ? "Напрямую" : "Только из списков") + "».";
            }
        }
        await RefreshStatusAsync();
        Router.JointRecheck();   // строки продвинутого режима — часть связки
        return new(info is null ? null : ToState(info), warn);
    }

    static IReadOnlyList<AdvancedRowInput> Rows(AdvancedState s) => [.. s.Rows.Select(r => new AdvancedRowInput(r.Domains, r.Strategy))];

    /// <summary>Какие из обычных сайтов («Обычные сайты») открываются сейчас — тем же способом, что «Проверить сейчас».</summary>
    async Task<HashSet<string>> OpenControlSitesAsync()
    {
        var groups = _shell.DefaultTargets.Groups.Where(g => g.Control).ToList();
        if (groups.Count == 0) return [];
        var verdicts = ProbeGroups is { } probe ? await probe(groups) : await Task.Run(() => _shell.CheckGroupsAsync(groups));
        return [.. verdicts.Where(v => v.Ok).Select(v => v.Target.Url)];
    }

    /// <summary>«Вернуть как было»: откат записи журнала помощника о продвинутом режиме.</summary>
    async Task<string?> RevertAdvancedPcAsync()
    {
        if (Helper.Reader is { } r && !r.Compat.Supports("changes_list")) return "Помощник старее окна и не ведёт журнал — обновите его.";
        if (await NeedHelperAsync() is not { } c) return "Без помощника вернуть нечего.";
        UndoReport? report = null;
        string? none = null;
        if (!await Guard("Возвращаю как было…", async () =>
            {
                var item = (await c.ChangesListAsync()).Items.FirstOrDefault(i => i.Kind == ChangeKinds.Advanced);
                if (item is null) { none = "Записи об изменении нет — вернуть нечего."; return; }
                report = await c.ChangesUndoAsync(item.Id);
            }))
            return _error;
        if (none is not null) return none;
        await RefreshStatusAsync();
        return AdvancedViewModel.UndoText(report?.Items.FirstOrDefault());
    }
}
