using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// «Остановить» похожие программы не остановило Flowseal (BUG-stop-flowseal): на «Диагностике» под кнопкой — выделяемый итог
    /// с причиной и как остановить вручную, «Остановлено» не пишется. Помощник — фейк, sc и taskkill не вызываются.
    /// </summary>
    [Fact]
    public void Diag_StopConflicts_Failure_ShowsReasonSelectable_NotStopped()
    {
        using var tmp = new TempDir();
        RunUi(() =>
        {
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var f = new HelperFixture();
            const string name = "zapret от Flowseal (служба zapret)";
            f.Backend.StatusValue = f.Backend.StatusValue with { Foreign = [new Conflict("service", name, "Запустите service.bat")] };
            const string failed = name + ": sc stop — код 1051: [SC] ControlService FAILED 1051; не остановлена за 10 с";
            const string howTo = "Запустите service.bat из папки Flowseal от администратора → «Remove Services».";
            f.Backend.StopForeignValue = new([], [], [failed], [], [howTo]);
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            string? asked = null;
            vm.Confirm = s => { asked = s; return true; };   // без настоящего диалога
            vm.GoTo(Pages.Diag);
            WaitUi(vm.RefreshStatusAsync());
            Pump();
            var tabs = Find<TabControl>(w)!;
            if (Find<TextBox>(tabs, t => t.IsVisible && t.Text.Contains("ControlService")) is not null) throw new Exception("Итог виден до нажатия");
            if (!vm.StopConflictsCmd.CanExecute(null)) throw new Exception("«Остановить» недоступна при найденной программе");
            vm.StopConflictsCmd.Execute(null);
            for (int i = 0; i < 400 && !vm.HasStopConflictsResult; i++) { Pump(); Thread.Sleep(25); }
            Pump();
            if (asked is null || !asked.Contains(name)) throw new Exception("Не спросили подтверждение: " + asked);
            var box = Find<TextBox>(tabs, t => t.IsVisible && t.Text.Contains("ControlService FAILED 1051"))
                      ?? throw new Exception("Нет итога под кнопкой: " + vm.StopConflictsResult);
            if (!box.IsReadOnly) throw new Exception("Итог должен быть только для чтения");
            if (box.Text != $"Не остановлено: {failed}. {howTo}") throw new Exception("Итог: " + box.Text);
            if (box.Text.Contains("Остановлено")) throw new Exception("«Остановлено» при неудаче");
            if (vm.Message != box.Text) throw new Exception("В строке состояния другое: " + vm.Message);
            Close(w);
            Stop(vm, f);
        });
    }

    /// <summary>
    /// Живая проверка 23.10: «Остановить» сработало, а карточка «Похожие программы» осталась с той же кнопкой.
    /// После остановки служба с автозапуском — спокойная строка без «Остановить» на «Диагностике» и на главной.
    /// </summary>
    [Fact]
    public void Diag_StopConflicts_Success_CardBecomesDormantLine_NoStopButton()
    {
        using var tmp = new TempDir();
        RunUi(() =>
        {
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var f = new HelperFixture();
            var running = new ZapretInstance("service", "zapret", ZapretOwner.Flowseal, @"C:\zapret-discord-youtube\bin\winws.exe", null, null, "Running") { AutoStart = true };
            f.Backend.StatusValue = f.Backend.StatusValue with { Foreign = Conflicts.From([running]) };
            f.Backend.StopForeignValue = new(["zapret от Flowseal (служба zapret)"], ["zapret"], [], [], []);
            // Остановлена, тип запуска «автоматически»: помощник присылает её уже не в foreign.
            f.Backend.OnStopForeign = () => f.Backend.StatusValue = f.Backend.StatusValue with
            {
                Foreign = [], ForeignDormant = Conflicts.From([running with { State = "Stopped" }]),
            };
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.Confirm = _ => true;   // без настоящего диалога
            vm.GoTo(Pages.Diag);
            WaitUi(vm.RefreshStatusAsync());
            Pump();
            var tabs = Find<TabControl>(w)!;
            Button? StopButton() => Find<Button>(tabs, b => b.IsVisible && b.Command == vm.StopConflictsCmd);
            if (StopButton() is null) throw new Exception("Нет «Остановить» при работающей службе");
            if (vm.PcItems.FirstOrDefault(i => i.Command == vm.StopConflictsCmd) is not { State: Health.Error }) throw new Exception("На главной нет помехи");

            vm.StopConflictsCmd.Execute(null);
            for (int i = 0; i < 400 && !vm.HasStopConflictsResult; i++) { Pump(); Thread.Sleep(25); }
            Pump();
            if (StopButton() is not null) throw new Exception("«Остановить» осталась после остановки");
            if (vm.StopConflictsCmd.CanExecute(null)) throw new Exception("«Остановить» доступна без помехи");
            const string line = "zapret от Flowseal (служба zapret) — не работает, запустится после перезагрузки.";
            var box = Find<TextBox>(tabs, t => t.IsVisible && t.Text.StartsWith(line))
                      ?? throw new Exception("Нет спокойной строки: " + string.Join(" | ", vm.DormantList));
            if (!box.Text.Contains("Remove Services")) throw new Exception("Нет, как убрать насовсем: " + box.Text);
            if (Find<TextBlock>(tabs, t => t.IsVisible && t.Text == "Не найдены.") is not null) throw new Exception("«Не найдены.» при спящей службе");
            var home = vm.PcItems.Single(i => i.Label == "Похожие программы");
            if (home.State != Health.Ok || home.HasAction) throw new Exception($"Главная: {home.State}, кнопка {home.Action}");
            if (home.Value != "zapret от Flowseal (служба zapret) — не работает, запустится после перезагрузки") throw new Exception("Главная: " + home.Value);
            if (vm.HasConflicts || vm.ConflictWarning != "") throw new Exception("Остановленная служба всё ещё помеха");
            Close(w);
            Stop(vm, f);
        });
    }
}
