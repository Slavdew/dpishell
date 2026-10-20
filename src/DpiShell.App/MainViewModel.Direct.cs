using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// «Сайт не открывается?» (UX-аудит 7, п. 5): сайт, который сломался из-за оптимизации, открывается без неё в два шага — адрес и «Открыть без оптимизации».
/// На этом ПК сайт попадает в простой список «Открывать без оптимизации» на вкладке «Сайты» (user-exclude.txt у помощника, протокол 1.10;
/// в командную строку он идёт как <c>--hostlist-exclude</c> во всех ролях с именами сайтов), на роутере — в его «Исключения». Строки «Напрямую»
/// продвинутого режима не трогаются.
/// </summary>
public sealed partial class MainViewModel
{
    // ---------- Главная: «Сайт не открывается?» ----------

    /// <summary>Оптимизация работает на этом ПК или через роутер — только тогда есть что отключать для сайта.</summary>
    bool Covered => _active || (Router.Found && Router.RouterEnabled);

    /// <summary>Ссылка «Сайт не открывается?» на карточке — пока оптимизация работает.</summary>
    public bool ShowSiteFix => Covered;

    bool _directOpen;
    /// <summary>Строка «Адрес сайта → Открыть без оптимизации» раскрыта.</summary>
    public bool DirectOpen { get => _directOpen; set => Set(ref _directOpen, value); }

    string _directSite = "";
    public string DirectSite { get => _directSite; set => Set(ref _directSite, value); }

    string _directMessage = "";
    public string DirectMessage { get => _directMessage; private set { if (Set(ref _directMessage, value)) Raise(nameof(HasDirectMessage)); } }
    public bool HasDirectMessage => _directMessage.Length > 0;

    public ICommand ToggleDirectCmd => _toggleDirectCmd ??= new RelayCommand(() => { DirectOpen = !DirectOpen; if (DirectOpen) DirectMessage = ""; });
    ICommand? _toggleDirectCmd;

    public ICommand OpenDirectCmd => _openDirectCmd ??= RelayCommand.Async(OpenDirectAsync, () => !Busy && !IsLoading);
    ICommand? _openDirectCmd;

    /// <summary>Добавить сайт в исключения там, где сейчас работает оптимизация (на ПК и/или на роутере).</summary>
    public async Task OpenDirectAsync()
    {
        if (DataStore.NormalizeDomain(DirectSite) is not { } host)
        {
            DirectMessage = L.T("home.direct.empty");
            return;
        }
        bool pc = false, router = false;
        if (_active)
        {
            if (!await AddPcExcludeAsync(host)) return;
            pc = true;
        }
        if (Router.Found && Router.RouterEnabled)
        {
            if (!await Router.ExcludeHostAsync(host))
            {
                // Роутер не принял (нет входа, старая версия) — ПК уже сделан, об остальном скажет строка состояния роутера.
                DirectMessage = L.T(pc ? "home.direct.router_failed_pc" : "home.direct.router_failed", new { site = host });
                return;
            }
            router = true;
        }
        DirectSite = "";
        DirectMessage = L.T(pc && router ? "home.direct.done_both" : pc ? "home.direct.done_pc" : "home.direct.done_router", new { site = host });
        Message = DirectMessage;
    }

    async Task<bool> AddPcExcludeAsync(string host)
    {
        var lines = DraftExclude();
        if (!lines.Contains(host, StringComparer.OrdinalIgnoreCase)) lines.Add(host);
        return await SaveExcludeCoreAsync(lines);
    }

    // ---------- «Сайты» → «Открывать без оптимизации» (простой список исключений ПК) ----------

    string _excludeText = "";
    /// <summary>Черновик списка до «Сохранить»; сохраняет помощник (<c>user_exclude_set</c>).</summary>
    public string ExcludeText
    {
        get => _excludeText;
        set { if (Set(ref _excludeText, value)) { SetExcludeMessage(""); RaiseSitesDirty(); } }
    }

    IReadOnlyList<string> SavedExclude => Helper.Status?.UserExclude ?? [];

    /// <summary>В поле не то, что сохранено.</summary>
    public bool ExcludeDirty
    {
        get
        {
            var saved = SavedExclude.Select(DataStore.NormalizeDomain).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            return !saved.SetEquals(DraftExclude());
        }
    }

    List<string> DraftExclude() =>
        ExcludeText.Split('\n').Select(DataStore.NormalizeDomain).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    string _excludeMessage = "";
    bool _excludeError;
    /// <summary>«Сохранено…» — пока в поле то же, что сохранено (рядом с «Не сохранено» два статуса не нужны); отказ — виден всегда.</summary>
    public string ExcludeMessage => _excludeError || !ExcludeDirty ? _excludeMessage : "";

    void SetExcludeMessage(string text, bool error = false)
    {
        _excludeMessage = text; _excludeError = error;
        Raise(nameof(ExcludeMessage));
    }

    bool _excludeFromHelper;

    /// <summary>Первый ответ помощника — список в поле (дальше поле — черновик человека).</summary>
    void TakeExcludeFromHelper()
    {
        if (_excludeFromHelper || Helper.Status?.UserExclude is not { } list) return;
        _excludeFromHelper = true;
        if (list.Count > 0) ExcludeText = string.Join(Environment.NewLine, list);
    }

    /// <summary>Сохранить список из поля («Сохранить» на вкладке «Сайты» и уход с неё).</summary>
    async Task<bool> SaveExcludeAsync() => await SaveExcludeCoreAsync(DraftExclude());

    async Task<bool> SaveExcludeCoreAsync(IReadOnlyList<string> lines)
    {
        if (Helper.Reader is { } hc && !hc.Compat.Supports("user_exclude_set"))
        {
            SetExcludeMessage("Помощник старее окна и не знает этого списка — обновите его на вкладке «Информация и обновления».", error: true);
            Message = ExcludeMessage;
            return false;
        }
        if (lines.Count > AdvancedSites.MaxExclude)
        {
            SetExcludeMessage($"Не больше {AdvancedSites.MaxExclude} сайтов в списке «Открывать без оптимизации».", error: true);
            Message = ExcludeMessage;
            return false;
        }
        UserTargetsResult? r = null;
        if (!await WithHelper("Сохраняю исключения…", async c =>
            {
                r = await c.SetUserExcludeAsync([.. lines]);
                await FetchAsync(full: false);
            }) || r is null)
            return false;
        _excludeFromHelper = true;
        ExcludeText = string.Join(Environment.NewLine, r.Saved);
        SetExcludeMessage(r.Rejected.Count == 0
            ? $"Сохранено сайтов без оптимизации: {r.Saved.Count}."
            : $"Сохранено сайтов без оптимизации: {r.Saved.Count}. Не распознаны и убраны: {string.Join(", ", r.Rejected)}.");
        RaiseSitesDirty();
        return true;
    }
}
