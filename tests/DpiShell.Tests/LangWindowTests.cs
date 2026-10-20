using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Язык в окне: значок с флагом внизу меню, список языков, «Настройки» → «Язык»; смена — сразу, без перезапуска.
/// Снимки (DPISHELL_SNAPSHOTS): lang-home-ru.png, lang-home-en.png, lang-picker.png, lang-settings-ru.png, lang-settings-en.png.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangWindowTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Fact]
    public void Switching_language_updates_window_at_once_and_is_saved()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        void Shot(Window w, string name)
        {
            if (snapDir is null) return;
            Directory.CreateDirectory(snapDir);
            Snap(w, Path.Combine(snapDir, name + ".png"));
        }
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(L.Ru);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            WindowSmokeTests.Pump();
            var tabs = Find<TabControl>(w)!;
            TabItem Tab(int i) => (TabItem)tabs.Items[i];

            // Значок внизу меню: флаг и код.
            var button = Find<ToggleButton>(w, b => b.Name == "LangButton") ?? throw new Exception("Нет значка языка внизу меню");
            if (Find<FlagIcon>(button) is not { Code: "ru" }) throw new Exception("На значке нет флага ru");
            if (Find<TextBlock>(button, t => t.Text == "RU") is null) throw new Exception("На значке нет кода RU");
            if (button.ToolTip as string != "Язык интерфейса: Русский") throw new Exception($"Подсказка значка «{button.ToolTip}»");
            if (Tab(0).Header as string != "Главная") throw new Exception($"Вкладка «{Tab(0).Header}»");
            // Служба на главной: без помощника режим считается службой — «Режим службы» (служба Windows ещё не создана) или «Служба установлена», рядом «(i)».
            if (Find<TextBlock>(tabs, t => t.Text is "Режим службы" or "Служба установлена" && t.IsVisible) is null) throw new Exception("На главной нет строки службы");
            if (Find<InfoButton>(tabs, b => b.Topic == InfoTexts.Service && b.IsVisible) is null) throw new Exception("Нет «(i)» у службы");
            Shot(w, "lang-home-ru");

            // Список языков: оба встроенных, текущий отмечен.
            vm.Lang.PickerOpen = true;
            WindowSmokeTests.Pump();
            var popup = Find<Popup>(w) ?? throw new Exception("Нет списка языков");
            if (!popup.IsOpen) throw new Exception("Список языков не раскрылся");
            var rows = FindAll<Button>((FrameworkElement)popup.Child).Where(b => b.DataContext is LangItem).ToList();
            if (rows.Count != 2) throw new Exception($"В списке {rows.Count} языков, ждали 2");
            if (!((LangItem)rows[0].DataContext).IsCurrent || ((LangItem)rows[0].DataContext).Name != "Русский") throw new Exception("Первым должен быть отмеченный русский");
            if (snapDir is not null) Save((FrameworkElement)popup.Child, Path.Combine(snapDir, "lang-picker.png"));

            // Выбор — сразу: меню, (i), подсказки; сохранён в window.json.
            rows[1].Command.Execute(null);
            WindowSmokeTests.Pump();
            if (popup.IsOpen) throw new Exception("Список не закрылся после выбора");
            if (L.Current != "en" || vm.Settings.Language != "en") throw new Exception("Язык не выбран или не сохранён");
            if (WindowSettings.Load(vm.Settings.File).Language != "en") throw new Exception("Язык не записан в window.json");
            if (Tab(0).Header as string != "Home") throw new Exception($"Вкладка после смены языка «{Tab(0).Header}»");
            if (Find<TextBlock>(button, t => t.Text == "EN") is null || Find<FlagIcon>(button) is not { Code: "gb" }) throw new Exception("Значок не сменился на EN");
            if (Find<InfoButton>(tabs, b => b.Topic == InfoTexts.Home)!.Text.StartsWith("Нажмите")) throw new Exception("(i) осталась русской");
            if (Find<TextBlock>(tabs, t => t.Text is "Service mode" or "Service installed" && t.IsVisible) is null) throw new Exception("Строка службы на главной не перевелась");
            Shot(w, "lang-home-en");

            // «Настройки» → «Язык»: тот же выбор, переключатели.
            vm.Page = Pages.Settings;
            WindowSmokeTests.Pump();
            var radios = FindAll<RadioButton>(tabs, r => r.DataContext is LangItem && r.IsVisible).ToList();
            if (radios.Count != 2 || radios[1].IsChecked != true) throw new Exception("В «Настройках» нет выбора языка или отмечен не тот");
            if (Find<CheckBox>(tabs, c => c.Content as string == "Service mode") is null) throw new Exception("Галочка режима службы не перевелась");
            radios[0].BringIntoView();
            WindowSmokeTests.Pump();
            Shot(w, "lang-settings-en");
            radios[0].IsChecked = true;
            WindowSmokeTests.Pump();
            if (L.Current != "ru" || Tab(0).Header as string != "Главная") throw new Exception("Выбор в «Настройках» не вернул русский");
            vm.ShowAdvancedSetting = true;
            WindowSmokeTests.Pump();
            if (Find<Button>(tabs, b => b.Content as string == "Выгрузить шаблон перевода" && b.IsVisible) is null) throw new Exception("Нет «Выгрузить шаблон перевода» у инструментов для опытных");
            Shot(w, "lang-settings-ru");

            // Шаблон — файл рядом с window.json, папка «открывается» (в тесте — подменено).
            string? opened = null;
            vm.OpenFolderAction = d => opened = d;
            vm.ExportLangTemplateCmd.Execute(null);
            if (opened is null || !File.Exists(Path.Combine(opened, "dpishell-translation.json"))) throw new Exception("Шаблон перевода не сохранён");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
