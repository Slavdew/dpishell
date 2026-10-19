using System.Collections.ObjectModel;
using System.Windows.Input;
using DpiShell.Core.Router;

namespace DpiShell.App;

/// <summary>Что с пробой включения: проверяем с ПК, не открылось, проверить нельзя или проба не наша (со страницы роутера, до запуска окна).</summary>
public enum RouterTrialState { Ask, Checking, Failed, Unknown }

// Аварийный выключатель (router-api §5.1, §5.2; architecture.md «Аварийный выключатель»):
// включение из окна — на пробу (confirm_s), окно само проверяет сайты с этого ПК и подтверждает; не открылись — спрашивает.
// Роутер откатил сам, выключил сторож или кнопка — фраза с «Подробнее» и «Включить снова». Новых опросов нет: только отсчёт по left_s.
public sealed partial class RouterViewModel
{
    /// <summary>Срок пробы, с (договор — 60–600).</summary>
    public const int TrialSeconds = 180;

    /// <summary>
    /// Проверка сайтов с этого ПК после включения на роутере («Проверить сейчас», обязательные цели):
    /// true — открылись, false — нет, null — проверить нельзя. По умолчанию — нельзя (спросить человека).
    /// </summary>
    public Func<Task<bool?>> VerifyFromPc { get; set; } = () => Task.FromResult<bool?>(null);

    /// <summary>Шаг отсчёта на плашке; запас после срока, прежде чем спросить роутер, что вышло (часы расходятся).</summary>
    public TimeSpan TrialTick { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan TrialGrace { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Часы этого ПК (в тестах подменяются): отсчёт — от left_s и момента ответа, а не от until роутера.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    bool _disposed, _countdown;
    DateTime? _trialDeadline;
    RouterTrialState _trialState;

    /// <summary>confirm_s для enable/strategy_set/apply: только роутеру, который знает confirm (старый — как раньше, без пробы).</summary>
    int? TrialArg => _status?.Methods?.Contains("confirm") == true ? TrialSeconds : null;

    /// <summary>Включение ждёт подтверждения — плашка «Всё работает?» внизу окна (видна на любой вкладке).</summary>
    public bool TrialVisible => _trialDeadline is not null && Connected;
    public bool TrialChecking => _trialState == RouterTrialState.Checking;

    public string TrialText => _trialState switch
    {
        RouterTrialState.Checking => "Проверяю, что сайты открываются. Если не подтвердить — роутер сам вернёт прежнее "
            + (_trialDeadline is { } d ? "через " + RouterTexts.Countdown(d - UtcNow()) : "скоро") + ".",
        RouterTrialState.Failed => "Часть сайтов с этого ПК не открылась. Оставить оптимизацию?",
        RouterTrialState.Unknown => "Проверить сайты с этого ПК не удалось. Всё работает?",
        _ => "Оптимизация на роутере включена на пробу. Всё работает?",
    };

    /// <summary>Кнопки и отсчёт рядом с ними — только когда решает человек (проверка не прошла или её нет).</summary>
    public bool TrialAsk => !TrialChecking;

    /// <summary>«вернётся само через 2:45».</summary>
    public string TrialCountdown => _trialDeadline is { } d ? "вернётся само через " + RouterTexts.Countdown(d - UtcNow()) : "";

    public ICommand TrialKeepCmd => _trialKeepCmd ??= RelayCommand.Async(KeepTrialAsync, () => CanAct && TrialVisible);
    public ICommand TrialRevertCmd => _trialRevertCmd ??= RelayCommand.Async(RevertTrialAsync, () => CanAct && TrialVisible);
    ICommand? _trialKeepCmd, _trialRevertCmd;

    void RaiseTrial()
    {
        Raise(nameof(TrialVisible)); Raise(nameof(TrialChecking)); Raise(nameof(TrialAsk)); Raise(nameof(TrialText)); Raise(nameof(TrialCountdown));
        Requery();
    }

    /// <summary>Из status: ждёт ли включение подтверждения. Срок — от left_s по часам этого ПК.</summary>
    void SyncTrial(RouterPendingConfirm? p)
    {
        if (p is null)
        {
            _trialDeadline = null;
            _trialState = RouterTrialState.Ask;
        }
        else
        {
            _trialDeadline = UtcNow() + TimeSpan.FromSeconds(Math.Max(0, p.LeftS));
            if (!_countdown) _ = CountdownAsync();
        }
        RaiseTrial();
    }

    /// <summary>Отсчёт на плашке — локально; срок вышел — один запрос status (роутер вернул сам), не больше трёх раз.</summary>
    async Task CountdownAsync()
    {
        _countdown = true;
        int late = 0;
        try
        {
            while (!_disposed && Connected && _trialDeadline is { } d)
            {
                Raise(nameof(TrialCountdown));
                if (_trialState == RouterTrialState.Checking) Raise(nameof(TrialText));
                if (UtcNow() < d) { await Task.Delay(TrialTick); continue; }
                if (++late > 3) { _trialDeadline = null; break; }
                await Task.Delay(TrialGrace);
                if (!Busy && !_disposed) await RefreshStatusSafeAsync(resumeSelect: false);
            }
        }
        finally
        {
            _countdown = false;
            RaiseTrial();
        }
    }

    /// <summary>
    /// Действие, которое включает обход или меняет стратегию на роутере: на пробу, затем проверка с этого ПК.
    /// Открылось — confirm без вопросов; нет — плашка. Старый роутер (без confirm) — как раньше. done — что сказать, когда оставлено.
    /// </summary>
    async Task<bool> TrialAsync(string text, Func<RouterClient, int?, Task> act, string? done)
    {
        var confirmS = TrialArg;
        if (!await Guard(text, async () =>
            {
                await Call(c => act(c, confirmS));
                await RefreshStatusCoreAsync();
            }))
            return false;
        if (_status?.PendingConfirm is null)
        {
            if (done is not null) Say(done);
        }
        else await VerifyTrialAsync(done);
        return true;
    }

    /// <summary>Проверить сайты с этого ПК и подтвердить, если открылись; иначе — спросить (плашка с отсчётом).</summary>
    async Task VerifyTrialAsync(string? done = null)
    {
        _trialState = RouterTrialState.Checking;
        RaiseTrial();
        bool? opened;
        try { opened = await VerifyFromPc(); }
        catch (Exception e) when (e is not OutOfMemoryException) { opened = null; }   // проверка не удалась — решает человек
        if (_status?.PendingConfirm is null) { _trialState = RouterTrialState.Ask; RaiseTrial(); return; }   // уже вернули или подтвердили
        if (opened == true)
        {
            _trialState = RouterTrialState.Ask;
            if (await ConfirmCoreAsync()) { Say("Проверено — оставил. " + (done ?? "Оптимизация на роутере включена.")); return; }
        }
        else _trialState = opened == false ? RouterTrialState.Failed : RouterTrialState.Unknown;
        RaiseTrial();
        if (TrialVisible) Say(TrialText);
    }

    Task<bool> ConfirmCoreAsync() => Guard("Подтверждаю на роутере…", async () =>
    {
        await Call(c => c.ConfirmAsync());
        await RefreshStatusCoreAsync();
    }, changes: false);

    /// <summary>«Да, оставить» — confirm.</summary>
    public async Task KeepTrialAsync()
    {
        if (await ConfirmCoreAsync()) Say("Оставлено: оптимизация на роутере работает.");
    }

    /// <summary>«Вернуть как было» — не ждать срока: выключить или вернуть прежнюю стратегию (revert_to), затем снять ожидание.</summary>
    public async Task RevertTrialAsync()
    {
        if (_status?.PendingConfirm is not { } p) return;
        if (await Guard("Возвращаю как было на роутере…", async () =>
            {
                if (p.RevertsToOff) await Call(c => c.DisableAsync());
                else await Call(c => c.StrategySetAsync(p.RevertTo!));
                // Прежнее уже вернули — ждать нечего (без ожидания confirm тоже отвечает {}).
                await Call(c => c.ConfirmAsync());
                await RefreshStatusCoreAsync();
            }, changes: false))
            Say(p.RevertsToOff ? "Вернул как было: оптимизация на роутере выключена." : "Вернул как было: на роутере стратегия " + StrategyTitle(p.RevertTo) + ".");
    }

    // ---------- выключение без человека: сторож, кнопка, откат (last_off, warnings) ----------

    string _offText = "";
    /// <summary>«Обход на роутере выключил сторож в 14:32: с ним пропал интернет, без него есть»; пусто — не выключали.</summary>
    public string OffText { get => _offText; private set { if (Set(ref _offText, value)) { Raise(nameof(HasOff)); Raise(nameof(CanReEnable)); } } }
    public bool HasOff => OffText.Length > 0;
    /// <summary>Слова роутера (last_off.text) — в «Подробнее» над целями.</summary>
    public string OffDetail { get => _offDetail; private set { if (Set(ref _offDetail, value)) Raise(nameof(HasOffDetail)); } }
    string _offDetail = "";
    public bool HasOffDetail => OffDetail.Length > 0;
    /// <summary>Что не открылось у сторожа (last_off.targets) — адрес и что сломалось.</summary>
    public ObservableCollection<RouterOffTarget> OffTargets { get; } = [];
    public bool HasOffDetails => HasOffDetail || OffTargets.Count > 0;
    public bool CanReEnable => HasOff && !RouterEnabled && Connected && WdState != WatchdogSelectState.Running;

    /// <summary>Подбор после сторожа (status.watchdog.select, роутер 0.2.0-r8): нет поля — старый роутер или подбора ещё не было.</summary>
    public enum WatchdogSelectState { None, Running, Found, Failed }
    WatchdogSelectState _wdState;
    public WatchdogSelectState WdState => _wdState;
    /// <summary>Идёт подбор после сторожа.</summary>
    public bool WdRunning => _wdState == WatchdogSelectState.Running;
    /// <summary>Пока роутер сам подбирает после сторожа, «Включить», «Подобрать» и «Стратегия» не нужны — менять нечего, остаётся «Остановить подбор».</summary>
    public bool ShowRouterActions => !WdRunning;
    /// <summary>Фраза красная, кроме случая «роутер сам подобрал и включил».</summary>
    public bool OffAlert => _wdState != WatchdogSelectState.Found;
    /// <summary>Подпись возврата: после неудачного подбора роутера «прежняя» — она уже не подошла сторожу.</summary>
    public string ReEnableText => "Попробовать прежнюю (на пробу)";   // одно название везде (UX-аудит 5, п. 13)

    WatchdogSelectState ClassifyWatchdogSelect()
    {
        var failedWarn = _status?.Warnings?.Any(w => w.Code == "watchdog_select_failed") == true && !RouterEnabled;
        if (_status?.Watchdog?.Select is not { } sel) return failedWarn ? WatchdogSelectState.Failed : WatchdogSelectState.None;
        if (sel.Ok) return RouterEnabled ? WatchdogSelectState.Found : WatchdogSelectState.None;
        if (_status.Jobs?.Select == true) return WatchdogSelectState.Running;
        return RouterEnabled ? WatchdogSelectState.None : WatchdogSelectState.Failed;
    }

    /// <summary>Фраза о подборе после сторожа; пусто — подбора нет.</summary>
    public static string WatchdogSelectText(WatchdogSelectState state, string? strategy) => state switch
    {
        WatchdogSelectState.Running => "Сторож выключил оптимизацию — роутер подбирает другую стратегию…",
        WatchdogSelectState.Found => $"Сторож выключил прежнюю стратегию, роутер подобрал и включил {strategy}.",
        WatchdogSelectState.Failed => "Сторож выключил оптимизацию, подходящей стратегии не нашлось. Оптимизация выключена, пока вы её не включите.",
        _ => "",
    };

    bool _offByWatchdog;
    string _offKey = "", _dismissedOffKey = "";
    /// <summary>Выключил сторож (роутер без подбора после сторожа): окно предлагает подобрать другую стратегию («Подобрать» / «Не сейчас»).</summary>
    public bool OfferSelect => CanReEnable && _offByWatchdog && _wdState == WatchdogSelectState.None && _offKey != _dismissedOffKey;
    /// <summary>Выключили не сторож (кнопка, откат) или роутер не нашёл стратегию сам: кнопка «Подобрать на роутере».</summary>
    public bool OfferSelectButton => CanReEnable && (!_offByWatchdog || _wdState == WatchdogSelectState.Failed);

    /// <summary>«Не сейчас»: предложение скрыто до следующего срабатывания сторожа (по времени last_off), память — на сеанс.</summary>
    public ICommand DismissOfferCmd => _dismissOfferCmd ??= new RelayCommand(() => { _dismissedOffKey = _offKey; RaiseOffOffer(); });
    ICommand? _dismissOfferCmd;

    void RaiseOffOffer() { Raise(nameof(OfferSelect)); Raise(nameof(OfferSelectButton)); }

    /// <summary>«Включить снова» — как «Включить»: на пробу и с проверкой с этого ПК.</summary>
    public ICommand ReEnableCmd => _reEnableCmd ??= RelayCommand.Async(ToggleAsync, () => Can("enable") && !RouterEnabled);
    ICommand? _reEnableCmd;

    void UpdateOff()
    {
        _wdState = ClassifyWatchdogSelect();
        // Роутер сам подобрал и включил — last_off по договору null, но если отдаёт, «Подробнее» покажет, что не открылось у сторожа.
        var off = RouterEnabled && _wdState != WatchdogSelectState.Found ? null : _status?.LastOff;
        var warn = _status?.Warnings?.FirstOrDefault(w => RouterTexts.IsOffCode(w.Code));
        OffText = _wdState != WatchdogSelectState.None
            ? WatchdogSelectText(_wdState, StrategyTitle(_status?.Watchdog?.Select?.Best ?? _status?.Strategy))
            : off is not null ? RouterTexts.Off(off.By, off.At)
            : warn is not null ? RouterTexts.Off(warn.Code, null, RouterEnabled)
            : "";
        OffDetail = HasOff ? off?.Text ?? warn?.Text ?? "" : "";
        var targets = HasOff ? off?.Targets ?? [] : [];
        if (!targets.SequenceEqual(OffTargets))
        {
            OffTargets.Clear();
            foreach (var t in targets) OffTargets.Add(t);
        }
        _offByWatchdog = HasOff && (off is not null ? off.By == "watchdog" : warn?.Code is "watchdog_off" or "watchdog_select_failed" || _wdState != WatchdogSelectState.None);
        _offKey = !HasOff ? "" : off is not null ? off.By + "|" + off.At?.ToUniversalTime().Ticks : warn?.Code ?? "watchdog";
        Raise(nameof(HasOffDetails)); Raise(nameof(CanReEnable)); RaiseOffOffer();
        Raise(nameof(WdState)); Raise(nameof(WdRunning)); Raise(nameof(ShowRouterActions)); Raise(nameof(OffAlert)); Raise(nameof(ReEnableText));
    }

    // ---------- сторож (schedule.watchdog, status.watchdog) ----------

    bool _watchdog;
    /// <summary>Роутер знает сторожа: есть schedule.watchdog или status.watchdog.</summary>
    public bool WatchdogAvailable => _status?.Schedule?.Watchdog is not null || _status?.Watchdog is not null;

    /// <summary>«Сторож: выключать обход, если с ним пропал интернет» (schedule_set watchdog).</summary>
    public bool WatchdogEnabled
    {
        get => _watchdog;
        set { if (value != _watchdog) _ = SetScheduleAsync(_scheduleEnabled, _scheduleHours, _autoSelect, value); }
    }

    public const string WatchdogOnText = "Сторож включён: если с оптимизацией перестанут открываться проверочные сайты, роутер обычно сам выключит её. Поломки на других сайтах он может не заметить.";

    /// <summary>«проверено в 14:30 · всё в порядке»; сторож выключен — пусто.</summary>
    public string WatchdogLine
    {
        get
        {
            if (!_watchdog || _status?.Watchdog is not { Enabled: true } w) return "";
            if (w.CheckedAt is null) return "ещё не проверял";
            var verdict = w.Ok switch { true => " · всё в порядке", false => " · с оптимизацией пропадал интернет", null => "" };
            return "проверено в " + RouterTexts.Time(w.CheckedAt) + verdict;
        }
    }
    public bool HasWatchdogLine => WatchdogLine.Length > 0;

    void RaiseWatchdog()
    {
        Raise(nameof(WatchdogEnabled)); Raise(nameof(WatchdogAvailable)); Raise(nameof(WatchdogLine)); Raise(nameof(HasWatchdogLine));
    }
}
