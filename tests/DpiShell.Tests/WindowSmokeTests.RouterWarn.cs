using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// strategy_failing (роутер 0.2.2-r2): на вкладке «Роутер» — строка «Стратегия не проходит проверку — нужен подбор» и кнопка «Подобрать»; нажатие ведёт на экран «Подбор» на роутере, как кнопка вкладки.
    /// </summary>
    [Fact]
    public void Router_StrategyFailing_Warning_HasSelectAction()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter
            {
                WarningsJson = """[ { "code": "strategy_failing", "text": "Стратегия не проходит проверку — нужен подбор", "key": "web.warn.strategy_failing", "params": { "strategy": "alt9" } } ]""",
            }.As020();
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            vm.Router.UtcNow = () => fake.CheckedAt.AddMinutes(10);   // свежий итог проверки: предупреждение действует
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.GoTo(Pages.Router);
            Pump();
            var tabs = Find<TabControl>(w)!;
            var text = L.T("web.warn.strategy_failing");
            var page = ((TabItem)tabs.Items[Pages.Router]).Content as System.Windows.DependencyObject;
            var box = Find<TextBox>(page!, t => t.IsVisible && t.Text == text) ?? throw new Exception("Нет строки предупреждения на вкладке «Роутер»");
            var row = System.Windows.Media.VisualTreeHelper.GetParent(box) as System.Windows.DependencyObject;
            var btn = Find<Button>(row!, b => b.IsVisible && b.Content as string == L.T("router.select"))
                      ?? throw new Exception("Нет кнопки «Подобрать» у предупреждения");
            if (btn.Command is not { } cmd || !cmd.CanExecute(btn.CommandParameter)) throw new Exception("«Подобрать» недоступна");
            cmd.Execute(btn.CommandParameter);
            Pump();
            if (tabs.SelectedIndex != Pages.Select) throw new Exception($"«Подобрать» открыла вкладку {tabs.SelectedIndex}, а не «Подбор»");
            if (!vm.Router.SelectOnRouter) throw new Exception("Экран «Подбор» не переключён на роутер");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
