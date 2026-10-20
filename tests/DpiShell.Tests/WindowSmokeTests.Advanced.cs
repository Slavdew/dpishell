using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// «Сайты» → «Продвинутый режим» на ПК и на роутере, в обеих темах: переключатель сверху (выключен по умолчанию), включён — строки «сайты → стратегия»,
    /// «Остальные сайты», «Применить»; ошибка в строке — красным; в карточке нет слова «обход». Снимки: &lt;тема&gt;-sites-advanced-*.png.
    /// </summary>
    [Fact]
    public void Sites_AdvancedMode_PcAndRouter_Bind_AndSnap()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        using var fake = new FakeRouter { WarningsJson = "[]", AdvancedField_On = true }.As020();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var vm = TestWindow.Model(shell);
                var a = vm.AdvancedPc;
                var ids = shell.Catalog.Strategies.Take(3).ToList();
                // Список «Стратегия» строки — полными именами каталога («general (ALT10)»), как в остальных местах окна.
                a.Strategies = () => [.. shell.Catalog.Strategies.Select(s => new AdvancedChoice(s.Id, s.Title))];
                vm.Router.Advanced.Strategies = a.Strategies;
                // Роутер подключается до открытия окна: его события трогают окно, а ConnectAsync здесь идёт с другого потока.
                vm.Router.AddressAuto = false;
                vm.Router.CustomAddress = fake.Address;
                vm.Router.PcTimestamps = () => true;
                Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
                var w = Open(vm);
                w.Height = 900;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(Pages.Sites);
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => VisibleText(tabs, m);

                // Выключен по умолчанию: переключатель сверху, редактора нет, «Свои сайты» на месте.
                var sw = Find<CheckBox>(tabs, c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == "Свои настройки для сайтов") ?? throw new Exception("Нет переключателя «Продвинутый режим»");
                if (sw.IsChecked == true) throw new Exception("Продвинутый режим включён по умолчанию");
                if (!Visible(t => t.Text == "Свои настройки для сайтов")) throw new Exception("Нет заголовка карточки");
                if (Find<Button>(tabs, b => b.Content as string == "Добавить строку" && b.IsVisible) is not null) throw new Exception("Редактор виден у выключенного режима");
                if (!Visible(t => t.Text == "Свои сайты")) throw new Exception("Пропали «Свои сайты»");
                if (Find<InfoButton>(tabs, b => b.Topic == InfoTexts.Advanced && b.IsVisible) is null) throw new Exception("Нет «(i)» у продвинутого режима");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-sites-advanced-off.png")); }

                // Включён, с тремя строками и «Для всех сайтов».
                a.Load = () => Task.FromResult<AdvancedState?>(new(true, "lists",
                [
                    new(["youtube.com", "example.org"], "auto", ids[1].Id),
                    new(["bank.example"], "direct", null),
                    new(["games.example", "cdn.games.example"], ids[0].Id, ids[0].Id),
                ]));
                WaitUi(a.RefreshAsync(force: true));
                Pump();
                if (Find<Button>(tabs, b => b.Content as string == "Добавить строку" && b.IsVisible) is null) throw new Exception("Нет «Добавить строку»");
                if (Find<TextBox>(tabs, t => t.IsVisible && t.Text.Contains("games.example")) is null) throw new Exception("Нет строки с сайтами");
                if (!Visible(t => t.Text.StartsWith(L.T("sites.adv.hint.auto_now", new { strategy = "" }).TrimEnd()))) throw new Exception("Нет подсказки «Авто — сейчас …»");
                if (!Visible(t => t.Text.Contains(L.T("sites.adv.hint.builtin", new { site = "youtube.com", list = "YouTube" })))) throw new Exception("Нет подсказки о списке YouTube");
                if (!Visible(t => t.Text.Contains(L.T("sites.adv.hint.direct")))) throw new Exception("Нет подсказки у «Напрямую»");
                if (Find<RadioButton>(tabs, r => r.IsVisible && Text(r) == "Только из списков" && r.IsChecked == true) is null) throw new Exception("«Только из списков» должен быть выбран");
                if (Find<RadioButton>(tabs, r => r.IsVisible && Text(r) == "Для всех сайтов") is null || Find<RadioButton>(tabs, r => r.IsVisible && Text(r) == "Напрямую — без оптимизации") is null)
                    throw new Exception("Нет вариантов «Остальных сайтов»");
                // Одна кнопка сохранения и один статус на вкладку (UX-аудит 6, п. 2): второй «Применить» и «Не применено» нет.
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == "Применить") is not null) throw new Exception("Осталась вторая кнопка сохранения «Применить»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == "Сохранить") is null) throw new Exception("Нет единой кнопки «Сохранить»");
                if (!Visible(t => t.Text == "Сохранено")) throw new Exception("Нет статуса «Сохранено» без правок");
                if (Visible(t => t.Text == "Не сохранено")) throw new Exception("«Не сохранено» без правок");
                if (a.ToggleText != "Включено" || !Visible(t => t.Text == "Включено")) throw new Exception("Подпись переключателя — не состояние «Включено»");
                if (FindAll<ComboBox>(tabs, c => c.IsVisible && c.Items.Count >= 3).Count() < 3) throw new Exception("У строк нет списка стратегий");
                if (Find<ComboBox>(tabs, c => c.IsVisible && c.SelectedValue as string == ids[0].Id) is null) throw new Exception("В строке не выбрана её стратегия");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-advanced-on.png"));

                // Правка и ошибка в строке — красным.
                a.Rows[1].DomainsText = "bank example";
                Pump();
                if (!Visible(t => t.Text == "Не сохранено") || Visible(t => t.Text == "Сохранено")) throw new Exception("После правки строки нет единого статуса «Не сохранено»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == "Отменить правки") is null) throw new Exception("При правках нет «Отменить правки»");
                WaitUi(a.ApplyAsync());
                Pump();
                if (!Visible(t => t.Text == L.T("sites.adv.err.row_not_domain", new { row = 2 }))) throw new Exception("Нет сообщения об ошибке в строке");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-advanced-error.png"));
                foreach (var t in FindAll<TextBlock>(tabs, t => t.IsVisible && t.DataContext == a))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("В карточке слово «обход»: " + t.Text);

                // Роутер: та же карточка над «Для каких сайтов оптимизация».
                vm.Router.TargetsOnRouter = true;
                WaitUi(vm.Router.Advanced.RefreshAsync(force: true));
                Pump();
                tabs = Find<TabControl>(w)!;
                if (Find<CheckBox>(tabs, c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == "Свои настройки для сайтов") is null) throw new Exception("На роутере нет переключателя");
                var router = vm.Router.Advanced;
                if (router.Enabled) throw new Exception("На роутере режим включён по умолчанию");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-advanced-router-off.png"));
                router.Load = () => Task.FromResult<AdvancedState?>(new(true, "all", [new(["example.com"], "auto", "alt10"), new(["bank.example"], "direct", null)]));
                WaitUi(router.RefreshAsync(force: true));
                Pump();
                if (Find<Button>(tabs, b => b.Content as string == "Добавить строку" && b.IsVisible) is null) throw new Exception("На роутере нет «Добавить строку»");
                if (Find<RadioButton>(tabs, r => r.IsVisible && Text(r) == "Для всех сайтов" && r.IsChecked == true) is null) throw new Exception("На роутере не выбрано «Для всех сайтов»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-advanced-router-on.png"));
                Close(w);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
