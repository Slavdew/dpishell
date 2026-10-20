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

    public static string AllQuestion => L.T("sites.adv.all_question");

    void InitAdvanced()
    {
        AdvancedPc = new AdvancedViewModel(router: false)
        {
            Load = LoadAdvancedPcAsync,
            Save = SaveAdvancedPcAsync,
            Strategies = () => [.. StrategyChoices.Select(c => new AdvancedChoice(c.Id, c.Title))],
            Unavailable = () => Helper.Reader is { } c && !c.Compat.Supports("advanced_set")
                ? L.T("sites.helper.outdated_update") : null,
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
            if (!IsRunning) return new(null, L.T("sites.adv.err.all_needs_running"));
            if (newAll && !Confirm(AllQuestion)) return new(null, L.T("sites.adv.err.not_applied"));
            before = await OpenControlSitesAsync();
            if (before.Count == 0) return new(null, L.T("sites.adv.err.control_closed"));
        }
        if (await NeedHelperAsync() is not { } c) return new(null, L.T("sites.adv.err.no_helper"));
        if (!c.Compat.Supports("advanced_set")) return new(null, AdvancedPc.UnavailableText ?? L.T("sites.adv.err.helper_no_mode"));

        AdvancedInfo? info = null;
        if (!await Guard(L.T("sites.adv.busy.apply"), async () => info = await c.AdvancedSetAsync(next.Enabled, next.Others, Rows(next))))
            return new(null, _error ?? L.T("sites.adv.err.apply_failed"));

        string? warn = null;
        if (toAll)
        {
            var now = await OpenControlSitesAsync();
            var broken = before.Where(u => !now.Contains(u)).Select(u => Uri.TryCreate(u, UriKind.Absolute, out var x) ? x.Host : u).ToList();
            if (broken.Count > 0)
            {
                // Сломалось то, что открывалось: возвращаем прежний вид (с сохранёнными строками), но не режим «Для всех».
                var back = next with { Others = prev.Enabled && prev.Others != AdvancedSites.OthersAll ? prev.Others : AdvancedSites.OthersLists };
                await Guard(L.T("sites.adv.busy.restore_mode"), async () => info = await c.AdvancedSetAsync(back.Enabled, back.Others, Rows(back)));
                var shown = string.Join(", ", broken.Take(3));
                var mode = L.T(info?.Others == AdvancedSites.OthersDirect ? "sites.adv.choice.direct" : "sites.mode.lists");
                warn = broken.Count > 3
                    ? L.T("sites.adv.broken_more", new { sites = shown, n = broken.Count - 3, mode })
                    : L.T("sites.adv.broken", new { sites = shown, mode });
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
        if (Helper.Reader is { } r && !r.Compat.Supports("changes_list")) return L.T("sites.adv.err.old_helper_journal");
        if (await NeedHelperAsync() is not { } c) return L.T("sites.adv.err.no_helper_revert");
        UndoReport? report = null;
        string? none = null;
        if (!await Guard(L.T("sites.adv.busy.revert"), async () =>
            {
                var item = (await c.ChangesListAsync()).Items.FirstOrDefault(i => i.Kind == ChangeKinds.Advanced);
                if (item is null) { none = L.T("sites.adv.err.no_record"); return; }
                report = await c.ChangesUndoAsync(item.Id);
            }))
            return _error;
        if (none is not null) return none;
        await RefreshStatusAsync();
        return AdvancedViewModel.UndoText(report?.Items.FirstOrDefault());
    }
}
