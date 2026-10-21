using System.IO;
using System.Net.Http;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Обход на этом ПК и на роутере одновременно не работают (router-api §6):
/// пакеты, изменённые обходом ПК, роутер изменяет второй раз. Решение — по факту «включён», а не по тому, открываются ли сайты.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Выбор из нескольких действий: индекс кнопки, -1 — «Отмена» (в тестах подменяется).</summary>
    public Func<string, IReadOnlyList<string>, int> Choose { get; set; } = ChoiceDialog.Show;

    /// <summary>Вопрос с пояснениями под кнопками: индекс действия, -1 — «Отмена» (в тестах подменяется).</summary>
    public Func<ChoiceRequest, int> ChooseDetailed { get; set; } = ChoiceDialog.Show;

    /// <summary>Действия вопроса «Оптимизация уже работает на роутере» по порядку кнопок.</summary>
    public enum RouterChoice { Joint = 0, SelectOnRouter = 1, OnlyPc = 2 }

    /// <summary>Роутер выключили на время подбора на ПК — после подбора спросить, включить ли обратно.</summary>
    bool _routerPausedForSelect;
    bool _switchingToRouter;

    void WireRouterPc()
    {
        Router.PcRunning = () => IsRunning;
        Router.StopPc = async () => !IsRunning || (await DisableCoreAsync() && !IsRunning);
        Router.EnablePcTimestamps = EnableTimestampsAsync;
        Router.VerifyFromPc = VerifyRouterFromPcAsync;
        Router.Changed += OnRouterChanged;
    }

    Task<bool> DisableCoreAsync() => WithHelper(L.T("router.disable.busy_short"), async c =>
    {
        await c.DisableAsync();
        await RefreshStatusAsync();
    });

    /// <summary>Метки TCP через помощника. Без вопроса — только если помощник уже на связи (без запроса прав).</summary>
    async Task<bool> EnableTimestampsAsync(bool ask)
    {
        var c = ask ? await NeedHelperAsync() : Helper.Client;
        if (c is null) return false;
        try { return await c.TcpTimestampsOnAsync(); }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException or TimeoutException) { return false; }
    }

    /// <summary>
    /// Перед «Включить» и подбором на ПК: обход уже работает на роутере — не включаем второй, а предлагаем
    /// «Работать вместе» (совместная работа: роутер — YouTube и Discord, ПК — свои сайты и игры), «Подобрать на роутере»
    /// или выключить роутер (на время подбора — с вопросом после него). true — можно продолжать на ПК.
    /// «Работать вместе» запоминается переключателем «Совместная работа» — дальше включение идёт без вопроса (<see cref="JointBeforeEnableAsync"/>).
    /// </summary>
    public async Task<bool> RouterGateAsync(bool select)
    {
        if (!(Router.Found && Router.RouterEnabled)) return true;
        // Совместная работа с роутером (mixed-strategies.md §7): ПК включается вместе с роутером — он ведёт только свои сайты, двойной обработки нет.
        if (!select && await JointBeforeEnableAsync()) return true;
        if (!Router.Connected)
        {
            var login = Choose(L.T("router.login_choice.text"),
                [L.T("router.login_choice.button")]);
            if (login == 0) Router.LoginCmd.Execute(null);
            return false;
        }
        if (!select) return await BothChoiceAsync();
        var choice = Choose(L.T("router.select_choice.text"), [L.T("router.choice.select_router"), L.T("router.choice.pause_router")]);
        if (choice == 0)
        {
            Router.SelectOnRouter = true;
            Page = Pages.Select;
            return false;
        }
        if (choice != 1) return false;
        if (!await Router.DisableAsync()) return false;
        _routerPausedForSelect = select;
        return true;
    }

    /// <summary>«Включить» на ПК, когда работает роутер (ответ владельца к п. 1): первой — «Работать вместе»; у ПК нечего вести — она серая с подсказкой.</summary>
    async Task<bool> BothChoiceAsync()
    {
        bool? hasWork = await Router.JointHasWorkAsync();
        var choice = (RouterChoice)ChooseDetailed(new(L.T("router.both.title"), L.T("router.both_choice.text"),
        [
            new(L.T("router.choice.joint"), L.T("router.choice.joint.detail"), "HomeIcon.Router", Enabled: hasWork != false, DisabledHint: L.T("router.choice.joint.nothing")),
            new(L.T("router.choice.select_router"), L.T("router.choice.select_router.detail"), "HomeIcon.Wand"),
            new(L.T("router.choice.only_pc"), L.T("router.choice.only_pc.detail"), "HomeIcon.Home"),
        ], L.T("router.both.footer"), "HomeIcon.Router"));
        switch (choice)
        {
            case RouterChoice.Joint:
                // Совместная работа включает и оптимизацию на ПК (только своё); выбор остаётся включённым переключателем на «Роутере».
                await Router.StartJointAsync();
                await RefreshStatusAsync();
                return false;
            case RouterChoice.SelectOnRouter:
                Router.SelectOnRouter = true;
                Page = Pages.Select;
                return false;
            case RouterChoice.OnlyPc:
                return await Router.DisableAsync();
            default:
                return false;
        }
    }

    /// <summary>После подбора на ПК, если роутер выключали на его время: «Включить на роутере обратно?» — тогда обход ПК выключается.</summary>
    public async Task RouterResumeAsync()
    {
        if (!_routerPausedForSelect) return;
        _routerPausedForSelect = false;
        if (!Router.Connected || Router.RouterEnabled) return;
        if (!Confirm(L.T(IsRunning ? "router.resume.confirm_pc" : "router.resume.confirm"))) return;
        if (IsRunning && !await DisableCoreAsync()) return;
        if (await Router.EnableAsync()) Message = L.T("router.resume.done");
    }

    /// <summary>
    /// Роутер сообщил «включён», а обход на ПК тоже работает (включили со страницы роутера или с другого ПК) —
    /// выключаем ПК сами и сообщаем (§6). Во время своих действий не трогаем: там решает пользователь.
    /// </summary>
    void OnRouterChanged()
    {
        // Совместная работа включена: ПК не выключается из-за роутера — он ведёт свои сайты (а роутер пропал или не принял связку — работает по полной настройке).
        if (_switchingToRouter || Busy || Selecting || IsLoading || !IsRunning || !Router.RouterEnabled || !Router.Found || Router.JointOn) return;
        _ = SwitchToRouterAsync();
    }

    /// <summary>Отложенное при старте включение обхода ПК (режим без службы): решится, когда станет ясно, что с роутером.</summary>
    string? _resumeDeferred;

    /// <summary>Сколько ждать, пока окно освободится, чтобы сверить ПК и роутер при старте.</summary>
    public TimeSpan ReconcileWait { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// При старте окна, когда известно и состояние ПК, и ответ роутера (§6): роутер включён — обход ПК выключается;
    /// роутер не отвечает, но это тот же роутер, где «живёт» обход (обновление, перезагрузка), — обход ПК тоже выключается
    /// (служба могла подняться с Windows от старой настройки) и не включается сам; другая сеть — всё как было.
    /// </summary>
    internal async Task ReconcileRouterPcAsync(Task routerRefresh)
    {
        try { await routerRefresh; } catch (Exception e) when (e is Core.Router.RouterException or HttpRequestException or TaskCanceledException) { }
        var until = DateTime.UtcNow + ReconcileWait;
        while ((Busy || Selecting) && DateTime.UtcNow < until) await Task.Delay(100);
        var deferred = _resumeDeferred;
        _resumeDeferred = null;
        if (Router.JointOn)
        {
            // Совместная работа: ПК не выключается из-за роутера. Связка (или возврат к полной настройке) — первой проверкой, оптимизация — после неё.
            if (deferred is not null && Helper.Client is { } jc)
            {
                await JointBeforeEnableAsync();
                try { await jc.EnableAsync(deferred); }
                catch (Exception e) when (e is HelperException or HelperUnavailableException) { Fail(Texts.Friendly(e)); }
                await RefreshStatusAsync();
            }
            return;
        }
        if (Router.Found && Router.RouterEnabled)
        {
            if (IsRunning) await SwitchToRouterAsync();
            else if (deferred is not null) await DisableCoreQuietAsync();   // режим без службы: «включён» у помощника — снять, работает роутер
            return;
        }
        if ((IsRunning || deferred is not null) && await Router.BypassExpectedButAbsentAsync())
        {
            if (Helper.Client is not null && await DisableCoreAsync())
                Message = L.T("router.pc_off_router_silent");
            return;
        }
        if (deferred is not null && Helper.Client is { } c)
        {
            try { await c.EnableAsync(deferred); }
            catch (Exception e) when (e is HelperException or HelperUnavailableException) { Fail(Texts.Friendly(e)); }
            await RefreshStatusAsync();
        }
    }

    async Task DisableCoreQuietAsync()
    {
        if (Helper.Client is not null) await DisableCoreAsync();
    }

    async Task SwitchToRouterAsync()
    {
        _switchingToRouter = true;
        try
        {
            // Помощника нет — выключить нечем: на главной плашка «включён и здесь, и на роутере» с кнопками.
            if (Helper.Client is null) return;
            if (await DisableCoreAsync()) Message = L.T("router.moved_to_router");
        }
        finally { _switchingToRouter = false; }
    }
}
