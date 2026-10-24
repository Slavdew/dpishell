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

                // 1) Помощник стоит, ядро цело, папки нет: ничего не сломано — карточки на Главной нет, строка в «Диагностике» (аудит уведомлений, №2).
                if (vm.ShowAvCard || Visible(L.T("home.av.offer"))) throw new Exception("Карточка «Первый запуск» при целом ядре");
                if (Btn(L.T("home.av.add")) is not null) throw new Exception("«Добавить в исключения» на Главной при целом ядре");
                vm.GoTo(Pages.Diag);
                Pump();
                if (!Visible(L.T("diag.av.missing"))) throw new Exception("В «Диагностике» нет строки «папки нет в исключениях»");
                var diagAdd = Btn(L.T("home.av.add")) ?? throw new Exception("В «Диагностике» нет «Добавить в исключения»");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Shot("diag")); }
                diagAdd.Command.Execute(null);
                for (int i = 0; i < 600 && (vm.Busy || vm.AvDiagOffer); i++) { Pump(); Thread.Sleep(20); }
                Pump();
                if (!f.Backend.Calls.Contains("av_exclusion_add")) throw new Exception("Добавление не ушло помощнику: " + string.Join(" | ", f.Backend.Calls));
                if (Visible(L.T("diag.av.missing")) || Btn(L.T("home.av.add")) is not null) throw new Exception("После добавления строка в «Диагностике» осталась");
                vm.GoTo(Pages.Home);
                Pump();
                if (vm.ShowAvCard) throw new Exception("После добавления из «Диагностики» на Главной появилась карточка");

                // 1а) Помощника нет (первый запуск): предложение на Главной — текст, кнопка, (i) с пояснением, почему только эта папка.
                var vmFirst = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("first-" + theme), Repo.Data)));
                vmFirst.Defender = FakeDefender.Active();
                var wFirst = Open(vmFirst);
                wFirst.Height = 760;
                SetProp(vmFirst, "IsLoading", false);
                vmFirst.GoTo(Pages.Home);
                WaitUi(AvExclusionFlowTests.Probe(vmFirst));
                Pump();
                var tabsFirst = Find<TabControl>(wFirst)!;
                if (!vmFirst.ShowAvCard || !VisibleText(tabsFirst, t => t.Text == L.T("home.av.offer"))) throw new Exception("Нет карточки «Первый запуск» с предложением");
                if (Find<Button>(tabsFirst, b => b.IsVisible && b.Content as string == L.T("home.av.add")) is null) throw new Exception("Нет «Добавить в исключения»");
                var info = FindAll<InfoButton>(tabsFirst, b => b.IsVisible && b.Topic == InfoTexts.AvExclusion).SingleOrDefault() ?? throw new Exception("Нет (i) у карточки");
                if (!info.Text.Contains("%ProgramData%\\DpiShell") || !info.Text.Contains("не исключает")) throw new Exception("В (i) нет пути и причины: " + info.Text);
                int primary = FindAll<Button>(tabsFirst, b => b.IsVisible && b.Style == (Style)wFirst.FindResource("Primary")).Count();
                if (primary != 1) throw new Exception($"На Главной главных кнопок {primary}, ждали 1");
                if (snapDir is not null) Snap(wFirst, Shot("offer"));
                Close(wFirst);

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

    /// <summary>
    /// «Указать архив ядра…» (ТЗ av-exclusion, п. 9): ядро не найдено — кнопка на Главной рядом с «Добавить в исключения и восстановить» и на «Диагностике».
    /// Выбор файла подменён: окно читает файл само и отдаёт помощнику только байты (core_offline_part), затем core_offline_install;
    /// чужой файл — отказ помощника с понятной причиной в строке состояния. Без настоящего диалога выбора файла.
    /// </summary>
    [Fact]
    public void Core_Offline_Archive_Button_On_Home_And_Diag()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var f = new HelperFixture();
            f.Backend.Release.SetResult();
            f.Backend.Defender = FakeDefender.Active();
            f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, Service = SvcState.Stopped, Core = CoreState.Missing };
            f.Backend.DetailsValue = f.Backend.DetailsValue with { Core = new CoreCheck(CoreState.Missing, ["winws.exe"], []) };
            var good = new byte[3 * 1024 * 1024 + 5];
            new Random(7).NextBytes(good);
            f.Backend.OfflineSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(good));
            Directory.CreateDirectory(tmp.Sub("dl"));
            var goodFile = Path.Combine(tmp.Sub("dl"), "zapret-core-offline.zip");
            File.WriteAllBytes(goodFile, good);
            var badFile = Path.Combine(tmp.Sub("dl"), "other.zip");
            File.WriteAllBytes(badFile, new byte[100]);
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link);
            var picked = new List<string>();
            string? next = badFile;
            vm.ChooseCoreZip = (title, filter) => { picked.Add(title + "|" + filter); return next; };
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.GoTo(Pages.Home);
            WaitUi(vm.RefreshStatusAsync());
            WaitUi(RefreshAv(vm));
            Pump();
            var tabs = Find<TabControl>(w)!;
            Button? Btn(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);
            if (Btn(L.T("home.core.offline")) is null) throw new Exception("На Главной нет «Указать архив ядра…» при ненайденном ядре");
            if (Btn(L.T("home.av.add_restore")) is null) throw new Exception("Нет «Добавить в исключения и восстановить»");

            Task Offline() => (Task)typeof(MainViewModel).GetMethod("CoreOfflineAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;
            // Чужой файл: байты ушли, помощник отказал — причина в строке состояния, ядро не поставлено.
            WaitUi(Offline());
            Pump();
            if (!vm.Message.Contains("sha256")) throw new Exception("Нет причины отказа: " + vm.Message);
            if (picked.Count != 1 || !picked[0].Contains("*.zip")) throw new Exception("Выбор файла не с тем фильтром: " + string.Join(";", picked));
            // Архив выпуска: частями по 1 МБ, затем установка; ядро на месте — кнопки нет.
            next = goodFile;
            WaitUi(Offline());
            Pump();
            if (f.Backend.OfflineParts != 4) throw new Exception("Частей не 4: " + f.Backend.OfflineParts);
            if (!f.Backend.Calls.Contains("core_offline_install")) throw new Exception("Нет core_offline_install");
            WaitUi(vm.RefreshStatusAsync());
            Pump();
            if (Btn(L.T("home.core.offline")) is not null) throw new Exception("Кнопка осталась после установки ядра");
            // Отмена выбора файла — ничего не шлём.
            next = null;
            int calls = f.Backend.Calls.Count;
            WaitUi(Offline());
            if (f.Backend.Calls.Count != calls) throw new Exception("Без файла что-то ушло помощнику");

            // «Диагностика»: ядро сломано — рядом с «Восстановить».
            f.Backend.StatusValue = f.Backend.StatusValue with { Core = CoreState.Missing };
            vm.GoTo(Pages.Diag);
            WaitUi(vm.RefreshDiagAsync());
            Pump();
            if (Btn(L.T("diag.repair")) is null || Btn(L.T("home.core.offline")) is null) throw new Exception("На «Диагностике» нет «Восстановить» и «Указать архив ядра…»");
            Close(w);
            Stop(vm, f);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static Task RefreshAv(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("RefreshAvAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [])!;

    static void SetField(object o, string name, object? value) =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(o, value);
}
