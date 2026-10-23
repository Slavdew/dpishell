using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    static Task AvAdd(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("AvAddAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;

    /// <summary>
    /// Карточка «Первый запуск» на Главной (ТЗ av-exclusion) в обеих темах, Защитник — подставной у помощника:
    /// 1) Защитник работает, папки нет — «Добавить в исключения» (i); нажали — помощник добавил, строка «✓», кнопки нет;
    /// 2) ядро удалено, исключения нет — одна кнопка «Добавить в исключения и восстановить» (добавить, затем восстановить); ядро удалено при исключении — «✓» и «Восстановить»;
    /// 3) Защитник не дал — фраза, папка выделяемым текстом, «Скопировать путь», «Открыть Защитник»; сторонний антивирус — его имя, без «Открыть Защитник».
    /// Снимки: &lt;тема&gt;-home-av-offer, -home-av-core, -home-av-refused.
    /// </summary>
    [Fact]
    public void Home_AvExclusion_Card_ThreeStates_Binds_AndSnap()
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var f = new HelperFixture();
                f.Backend.Release.SetResult();   // восстановление ядра не висит
                f.Backend.Defender = FakeDefender.Active();
                f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, Service = SvcState.Stopped };
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                var w = Open(vm);
                w.Height = 760;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(Pages.Home);
                WaitUi(vm.RefreshStatusAsync());
                WaitUi(RefreshAv(vm));
                Pump();
                var tabs = Find<TabControl>(w)!;
                Button? Btn(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);
                bool Visible(string text) => VisibleText(tabs, t => t.Text == text);
                string Shot(string name) => Path.Combine(snapDir!, $"{theme}-home-av-{name}.png");

                // 1) Предложение: текст, кнопка, (i) с пояснением, почему только эта папка.
                if (!vm.ShowAvCard || !Visible(L.T("home.av.offer"))) throw new Exception("Нет карточки «Первый запуск» с предложением");
                if (Btn(L.T("home.av.add")) is null) throw new Exception("Нет «Добавить в исключения»");
                var info = FindAll<InfoButton>(tabs, b => b.IsVisible && b.Topic == InfoTexts.AvExclusion).SingleOrDefault() ?? throw new Exception("Нет (i) у карточки");
                if (!info.Text.Contains("%ProgramData%\\DpiShell") || !info.Text.Contains("не исключает")) throw new Exception("В (i) нет пути и причины: " + info.Text);
                int primary = FindAll<Button>(tabs, b => b.IsVisible && b.Style == (Style)w.FindResource("Primary")).Count();
                if (primary != 1) throw new Exception($"На Главной главных кнопок {primary}, ждали 1");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Shot("offer")); }
                WaitUi(AvAdd(vm));
                Pump();
                if (!f.Backend.Calls.Contains("av_exclusion_add")) throw new Exception("Добавление не ушло помощнику: " + string.Join(" | ", f.Backend.Calls));
                if (!Visible(L.T("home.av.ok"))) throw new Exception("После добавления нет «Папка ядра в исключениях Защитника ✓»");
                if (Btn(L.T("home.av.add")) is not null) throw new Exception("После добавления осталась кнопка");

                // 2) Ядро удалено, исключения нет — одна кнопка «…и восстановить».
                f.Backend.Defender = FakeDefender.Active();
                f.Backend.StatusValue = f.Backend.StatusValue with { Core = CoreState.Missing };
                SetField(vm, "_avJustAdded", false);
                WaitUi(vm.RefreshStatusAsync());
                WaitUi(RefreshAv(vm));
                Pump();
                if (!Visible(L.T("home.av.core_missing"))) throw new Exception("Нет «Файлы ядра не найдены»");
                if (Btn(L.T("home.av.add_restore")) is null) throw new Exception("Нет «Добавить в исключения и восстановить»");
                if (Btn(L.T("diag.repair")) is not null) throw new Exception("Отдельная «Восстановить» рядом с «…и восстановить»");
                var before = f.Backend.Calls.Count;
                f.Backend.StatusValue = f.Backend.StatusValue with { Core = CoreState.Missing };
                WaitUi(AvAdd(vm));
                Pump();
                var after = f.Backend.Calls.Skip(before).ToList();
                int add = after.IndexOf("av_exclusion_add"), repair = after.FindIndex(c => c.StartsWith("core_repair"));
                if (add < 0 || repair < add) throw new Exception("Не «добавить, затем восстановить»: " + string.Join(" | ", after));
                // Ядро всё ещё «удалено» (подмена), исключение стоит — «✓» и «Восстановить».
                if (!Visible(L.T("home.av.ok")) || Btn(L.T("diag.repair")) is null) throw new Exception("При исключении и сломанном ядре нет «✓» и «Восстановить»");
                if (snapDir is not null) Snap(w, Shot("core"));

                // 3) Защитник не дал: ручной путь — выделяемая папка, «Скопировать путь», «Открыть Защитник».
                f.Backend.Defender = new FakeDefender { Refuse = true };
                WaitUi(RefreshAv(vm));
                WaitUi(AvAdd(vm));
                Pump();
                if (!Visible(L.T("home.av.refused"))) throw new Exception("Нет фразы об отказе Защитника");
                var path = Find<TextBox>(tabs, t => t.IsVisible && t.Text == FakeHelperBackend.AvRoot) ?? throw new Exception("Нет папки выделяемым текстом");
                if (!path.IsReadOnly) throw new Exception("Путь можно править");
                if (Btn(L.T("diag.copy_path")) is null || Btn(L.T("diag.open_defender")) is null) throw new Exception("Нет «Скопировать путь» / «Открыть Защитник»");
                if (snapDir is not null) Snap(w, Shot("refused"));

                // Сторонний антивирус: его имя, в Защитник не пишем, «Открыть Защитник» не предлагаем.
                f.Backend.Defender = FakeDefender.ThirdParty("ESET Security");
                SetField(vm, "_avFail", null);
                WaitUi(RefreshAv(vm));
                Pump();
                if (!Visible(L.T("home.av.third_party", new { name = "ESET Security" }))) throw new Exception("Нет фразы про сторонний антивирус");
                if (Btn(L.T("diag.open_defender")) is not null) throw new Exception("«Открыть Защитник» при стороннем антивирусе");
                if (Btn(L.T("home.av.add_restore")) is not null) throw new Exception("Предложение добавить в Защитник при стороннем антивирусе");
                if (Btn(L.T("diag.repair")) is null) throw new Exception("Нет «Восстановить» при стороннем антивирусе");
                foreach (var t in FindAll<TextBlock>(tabs, t => t.IsVisible && t.Text.Contains("исключ")))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase)) throw new Exception("Запретное слово: " + t.Text);
                // Английское окно — не здесь: CheckIfEn меняет язык всем тестам (только коллекция lang); ключи ru/en сверяет LocalizationTests.
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static Task RefreshAv(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("RefreshAvAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;

    static void SetField(object o, string name, object? value) =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(o, value);
}
