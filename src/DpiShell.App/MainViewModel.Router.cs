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

    Task<bool> DisableCoreAsync() => WithHelper("Выключаю…", async c =>
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
    /// «Подобрать на роутере» или выключить роутер (на время подбора — с вопросом после него). true — можно продолжать на ПК.
    /// </summary>
    public async Task<bool> RouterGateAsync(bool select)
    {
        if (!(Router.Found && Router.RouterEnabled)) return true;
        // Совместная работа с роутером (mixed-strategies.md §7): ПК включается вместе с роутером — он ведёт только свои сайты, двойной обработки нет.
        if (!select && await JointBeforeEnableAsync()) return true;
        if (!Router.Connected)
        {
            var login = Choose("Оптимизация уже работает на роутере — для всех устройств дома, и для этого ПК тоже. "
                               + "Вместе с роутером оптимизация на ПК не работает: сайты ломаются.\n\nЧтобы управлять оптимизацией на роутере, войдите на него.",
                ["Войти на роутер"]);
            if (login == 0) Router.LoginCmd.Execute(null);
            return false;
        }
        var choice = Choose(select
                ? "Оптимизация сейчас работает на роутере. Подбор на этом ПК при ней даст неверный результат: стратегии будут работать только «в паре» с роутером."
                : "Оптимизация уже работает на роутере — для всех устройств дома, и для этого ПК тоже. Вместе они не работают: трафик обрабатывается дважды.",
            select
                ? ["Подобрать на роутере", "Выключить на роутере на время подбора"]
                : ["Подобрать на роутере", "Выключить на роутере и включить на этом ПК"]);
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

    /// <summary>После подбора на ПК, если роутер выключали на его время: «Включить на роутере обратно?» — тогда обход ПК выключается.</summary>
    public async Task RouterResumeAsync()
    {
        if (!_routerPausedForSelect) return;
        _routerPausedForSelect = false;
        if (!Router.Connected || Router.RouterEnabled) return;
        if (!Confirm("Подбор закончен. Включить оптимизацию на роутере обратно?" + (IsRunning ? " Тогда на этом ПК она выключится." : ""))) return;
        if (IsRunning && !await DisableCoreAsync()) return;
        if (await Router.EnableAsync()) Message = "Оптимизация снова работает на роутере.";
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
                Message = "Оптимизация на этом ПК выключена: она работает на роутере, а роутер сейчас не отвечает.";
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
            if (await DisableCoreAsync()) Message = "Оптимизация перешла на роутер: на этом ПК выключена.";
        }
        finally { _switchingToRouter = false; }
    }
}
