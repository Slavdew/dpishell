using System.IO;
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
        if (!Router.Connected)
        {
            var login = Choose("Обход уже работает на роутере — для всех устройств дома, и для этого ПК тоже. "
                               + "Вместе с роутером обход на ПК не работает: сайты ломаются.\n\nЧтобы управлять обходом на роутере, войдите на него.",
                ["Войти на роутер"]);
            if (login == 0) Router.LoginCmd.Execute(null);
            return false;
        }
        var choice = Choose(select
                ? "Обход сейчас работает на роутере. Подбор на этом ПК при нём даст неверный результат: стратегии будут работать только «в паре» с роутером."
                : "Обход уже работает на роутере — для всех устройств дома, и для этого ПК тоже. Вместе они не работают: трафик обрабатывается дважды.",
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
        if (!Confirm("Подбор закончен. Включить обход на роутере обратно?" + (IsRunning ? " Тогда на этом ПК он выключится." : ""))) return;
        if (IsRunning && !await DisableCoreAsync()) return;
        if (await Router.EnableAsync()) Message = "Обход снова работает на роутере.";
    }

    /// <summary>
    /// Роутер сообщил «включён», а обход на ПК тоже работает (включили со страницы роутера или с другого ПК) —
    /// выключаем ПК сами и сообщаем (§6). Во время своих действий не трогаем: там решает пользователь.
    /// </summary>
    void OnRouterChanged()
    {
        if (_switchingToRouter || Busy || Selecting || IsLoading || !IsRunning || !Router.RouterEnabled || !Router.Found) return;
        _ = SwitchToRouterAsync();
    }

    async Task SwitchToRouterAsync()
    {
        _switchingToRouter = true;
        try
        {
            // Помощника нет — выключить нечем: на главной плашка «включён и здесь, и на роутере» с кнопками.
            if (Helper.Client is null) return;
            if (await DisableCoreAsync()) Message = "Обход перешёл на роутер.";
        }
        finally { _switchingToRouter = false; }
    }
}
