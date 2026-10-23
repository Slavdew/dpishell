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
}
