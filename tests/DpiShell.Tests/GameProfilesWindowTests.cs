using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Вкладка «Игры и программы»: «Игровой режим» первым, список профилей (пустой и с профилями), окно «Новый профиль»
/// (подсказки формата в пустых полях, отказ остаётся в окне, успех закрывает окно, Esc — отмена), карточка «Записать игру…» с плашкой про античиты.
/// На русском и английском, в светлой и тёмной теме (снимки при DPISHELL_SNAPSHOTS: games-*-&lt;язык&gt;-&lt;тема&gt;.png). Ссылки не открываются,
/// настоящих диалогов нет: окно «Новый профиль» — подмена, показанная за краем экрана.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class GameProfilesWindowTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static void WaitUi(Task t)
    {
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        t.GetAwaiter().GetResult();
    }

    static void PumpUntil(Func<bool> cond, string what)
    {
        for (int i = 0; i < 400 && !cond(); i++) { Pump(); Thread.Sleep(15); }
        if (!cond()) throw new Exception("Не дождались: " + what);
    }

    const string GoodFilter = "outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d";
    const string GoodParams = "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8";

    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void Apps_tab_profiles_add_dialog_record_and_game_mode(string lang, bool dark)
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snapDir is not null) Directory.CreateDirectory(snapDir);
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(dark ? ThemeManager.Dark : ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var f = new HelperFixture(backend: PcHelper.Over(shell));
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link, TestWindow.Settings(shell));
            vm.ApplyHelperData();
            vm.Apps.OpenUrl = _ => { };
            vm.Net.OpenUrl = _ => { };
            var theme = dark ? "dark" : "light";

            var w = Open(vm);
            w.Height = 1000;
            SetProp(vm, "IsLoading", false);
            var tabs = Find<TabControl>(w)!;
            void Shot(Window win, string name)
            {
                CyrillicScan.CheckIfEn(win, name);
                if (snapDir is not null) Snap(win, Path.Combine(snapDir, $"{name}-{lang}-{theme}.png"));
            }
            void Show(FrameworkElement e) { e.BringIntoView(); Pump(); Pump(); }
            Button? Btn(DependencyObject root, string content) => Find<Button>(root, b => b.IsVisible && b.Content as string == content);
            double Y(FrameworkElement e) => e.TranslatePoint(new Point(0, 0), tabs).Y;

            vm.GoTo(Pages.Apps);
            PumpUntil(() => vm.Apps.Variants.Count > 0, "варианты параметров от помощника");

            // Порядок: «Игровой режим» — первым, ниже «Профили»; «Отладка» и «Дополнительно» свёрнуты; карточки записи и импорта на вкладке нет.
            var modeTitle = Find<TextBlock>(tabs, t => t.IsVisible && t.Text == L.T("apps.game.title"))!;
            var profilesTitle = Find<TextBlock>(tabs, t => t.IsVisible && t.Text == L.T("apps.profiles.title")) ?? throw new Exception("Нет блока «Профили»");
            if (Y(modeTitle) >= Y(profilesTitle)) throw new Exception("«Игровой режим» не выше списка профилей");
            Assert.False(Named<Expander>(w, "DebugSection").IsExpanded);
            Assert.False(Named<Expander>(w, "IpListsBlock").IsExpanded);
            Assert.False(Named<Border>(w, "RecordCard").IsVisible);
            Assert.False(VisibleText(tabs, t => t.Text == vm.Apps.Anti), "плашка про античиты висит на вкладке");
            Assert.Null(Btn(tabs, L.T("apps.start")));

            // Пустой список: короткое пустое состояние и две кнопки, «Добавить профиль…» — главная.
            Assert.True(VisibleText(tabs, t => t.Text == L.T("apps.profiles.empty")), "нет пустого состояния");
            var add = Btn(tabs, L.T("apps.profiles.add")) ?? throw new Exception("Нет «Добавить профиль…»");
            Assert.Same(w.FindResource("Primary"), add.Style);
            Assert.NotNull(Btn(tabs, L.T("apps.profiles.record")));
            // Профилей нет — «Свои профили» серая, с подсказкой почему.
            RadioButton ProfilesRadio() => Find<RadioButton>(tabs, r => r.IsVisible && r.Content as string == L.T("apps.game.mode.profiles"))!;
            Assert.False(ProfilesRadio().IsEnabled);
            Assert.Equal(L.T("apps.game.mode.profiles_none"), ProfilesRadio().ToolTip as string);
            Assert.True(ToolTipService.GetShowOnDisabled(ProfilesRadio()));
            Show(modeTitle);
            Shot(w, "games-empty");

            // Окно «Новый профиль» — подмена модального: то же окно за краем экрана, шаги теста идут, пока оно открыто.
            void InDialog(Action<Window, ProfileAddView> steps)
            {
                vm.Apps.AddProfileDialog = a =>
                {
                    var host = ProfileAddView.Host(a);
                    host.WindowStartupLocation = WindowStartupLocation.Manual;
                    host.Left = -20000; host.Top = -20000; host.ShowActivated = false; host.ShowInTaskbar = false;
                    bool closed = false;
                    host.Closed += (_, _) => closed = true;
                    void Close() => host.Close();
                    a.AddClosed += Close;
                    host.Show();
                    Pump(); Pump();
                    try { steps(host, (ProfileAddView)host.Content); }
                    finally { a.AddClosed -= Close; if (!closed) host.Close(); }
                };
                add.Command.Execute(null);
                Pump();
            }
            bool Closed(Window host) => !host.IsVisible;

            // 1. Пустое окно: четыре подсказки формата видны, в значения не попадают; «Добавить» недоступна; фокус — в «Название».
            InDialog((host, view) =>
            {
                var cues = new[] { "apps.add.cue.name", "apps.add.cue.filter", "apps.add.cue.params", "apps.add.cue.subnets" }.Select(k => L.T(k)).ToArray();
                foreach (var c in cues)
                    if (!VisibleText(host, t => t.Text == c)) throw new Exception("Нет подсказки «" + c + "»");
                Assert.All(new[] { vm.Apps.ImportTitle, vm.Apps.ImportFilter, vm.Apps.ImportParams, vm.Apps.ImportSubnets }, Assert.Empty);
                Assert.All(FindAll<TextBox>(host, t => t.IsVisible && t.IsEnabled && !t.IsReadOnly), t => Assert.Equal("", t.Text));
                Assert.False(Btn(host, L.T("apps.import.add"))!.IsEnabled);
                PumpUntil(() => ReferenceEquals(view.FindName("TitleBox"), FocusManager.GetFocusedElement(host)), "фокус в «Название»");
                // Подсказки — условные: только документационные сети, без рабочих параметров.
                Assert.Contains("198.51.100.0/24", L.T("apps.add.cue.subnets"));
                Assert.Contains("<", L.T("apps.add.cue.params"));
                // Ввод параметров — не «импорт»: команды «Импорт»/«Экспорт» будут отдельными.
                Assert.False(VisibleText(host, t => t.Text.Contains("мпорт", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("import", StringComparison.OrdinalIgnoreCase)), "в окне слово «импорт»");
                Assert.True(VisibleText(host, t => t.Text == L.T("apps.add.block")), "нет подписи блока «Параметры из сборки Flowseal»");
                Shot(host, "games-add-empty");

                // Ввод: подсказка поля пропадает, значение — ровно введённое.
                ((TextBox)view.FindName("TitleBox")).Text = "Mistfall Hunter";
                ((TextBox)view.FindName("FilterBox")).Text = GoodFilter;
                ((TextBox)view.FindName("ParamsBox")).Text = GoodParams;
                Pump();
                Assert.Equal("Mistfall Hunter", vm.Apps.ImportTitle);
                Assert.Equal(GoodFilter, vm.Apps.ImportFilter);
                Assert.False(VisibleText(host, t => t.Text == cues[0] || t.Text == cues[1] || t.Text == cues[2]), "подсказка видна над заполненным полем");
                Assert.True(VisibleText(host, t => t.Text == cues[3]), "у пустых «Подсетей» нет подсказки");
                PumpUntil(() => Btn(host, L.T("apps.import.add"))!.IsEnabled, "«Добавить» доступна при заполненных полях");
                Shot(host, "games-add-filled");

                // «Добавить»: окно закрывается само, профиль в списке.
                WaitUi(vm.Apps.ImportAsync());
                Pump();
                Assert.True(Closed(host), "окно не закрылось после добавления: " + vm.Apps.ImportMessage);
            });
            Assert.True(vm.Apps.HasProfiles, "msg=" + vm.Apps.ImportMessage + " main=" + vm.Message + " busy=" + vm.Busy);
            Assert.False(vm.Apps.ImportFailed);
            Assert.Equal("", vm.Apps.ImportFilter);
            PumpUntil(() => Find<TextBox>(tabs, t => t.IsVisible && t.Text.Contains(L.T("apps.profile.sig_mark"))) is not null, "профиль в списке");
            Assert.True(vm.Apps.ShowImportResult);
            Assert.True(Find<TextBox>(tabs, t => t.IsVisible && t.Text == vm.Apps.ImportMessage) is not null, "итог добавления не под списком");
            Assert.False(VisibleText(tabs, t => t.Text == L.T("apps.profiles.empty")), "пустое состояние при профиле");
            WaitUi(link.FetchAsync(full: false));
            vm.Net.Refresh();
            Assert.True(vm.Net.GameProfiles);
            Pump();
            Assert.True(ProfilesRadio().IsEnabled);
            Assert.Equal(L.T("apps.game.mode.profiles_tip"), ProfilesRadio().ToolTip as string);

            // 2. Отказ по лишнему: причина — выделяемым текстом в окне, окно открыто; Esc — отмена, введённое не хранится.
            InDialog((host, view) =>
            {
                vm.Apps.ImportTitle = "Broken";
                vm.Apps.ImportFilter = "outbound and tcp.DstPort = 1 and tcp.Syn = 1";
                vm.Apps.ImportParams = "--filter-tcp=1 --dpi-desync=fake";
                WaitUi(vm.Apps.ImportAsync());
                Pump();
                Assert.True(vm.Apps.ImportFailed);
                Assert.False(Closed(host), "окно закрылось при отказе");
                var error = (TextBox)view.FindName("Error");
                Assert.True(error.IsVisible && error.IsReadOnly && error.Text == vm.Apps.ImportMessage && error.Text.Length > 0, "причины нет в окне");
                Shot(host, "games-add-error");
                view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Assert.True(Closed(host), "Esc не закрыл окно");
            });
            Assert.Single(vm.Apps.Profiles);
            Assert.Equal("", vm.Apps.ImportFilter);
            Assert.Equal("", vm.Apps.ImportTitle);
            Assert.False(vm.Apps.HasImportMessage);

            // 3. Ещё профиль — только параметры и подсети (UDP): строки списка, действия справа одной высоты.
            vm.Apps.ImportTitle = "Voice Chat";
            vm.Apps.ImportParams = "--filter-udp=50000-50100 --dpi-desync=fake --dpi-desync-any-protocol=1";
            vm.Apps.ImportSubnets = "203.0.113.0/24";
            WaitUi(vm.Apps.ImportAsync());
            Assert.Equal(2, vm.Apps.Profiles.Count);
            Pump();
            var removes = FindAll<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("apps.profile.remove")).ToList();
            Assert.Equal(2, removes.Count);
            foreach (var r in removes)
            {
                var row = (StackPanel)VisualTreeHelper.GetParent(r);
                var heights = FindAll<Button>(row, b => b.IsVisible).Select(b => Math.Round(b.ActualHeight)).Distinct().ToList();
                Assert.True(heights.Count == 1, "кнопки строки разной высоты: " + string.Join(", ", heights));
            }
            Assert.Equal(2, FindAll<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("apps.profile.other_variant")).Count());
            Assert.Equal(2, FindAll<Button>(tabs, b => b.IsVisible && b.Content as string == L.T("apps.profile.copy")).Count());
            Show(modeTitle);
            Shot(w, "games-profiles");

            // 4. «Записать игру…» — карточка под списком: галка UDP, «Подробнее», плашка про античиты, «Начать»; «Закрыть» убирает её.
            Btn(tabs, L.T("apps.profiles.record"))!.Command.Execute(null);
            Pump(); Pump();
            var card = Named<Border>(w, "RecordCard");
            Assert.True(card.IsVisible);
            Assert.True(Y(card) > Y(profilesTitle));
            Assert.True(VisibleText(card, t => t.Text == vm.Apps.Anti), "нет плашки про античиты в карточке записи");
            Assert.NotNull(Find<CheckBox>(card, c => c.IsVisible && c.Content as string == L.T("apps.rec.udp")));
            Assert.NotNull(Btn(card, L.T("apps.start")));
            Show(card);
            Shot(w, "games-record");
            Btn(card, L.T("common.close"))!.Command.Execute(null);
            Pump();
            Assert.False(card.IsVisible);

            // Блок режима: «Свои профили» с раскрытыми подводными камнями.
            var pit = Find<Expander>(tabs, e => e.IsVisible && e.Header as string == L.T("apps.game.pit.header"))!;
            pit.IsExpanded = true;
            Show(pit);
            Shot(w, "games-mode-profiles");
            Assert.NotNull(Find<RadioButton>(tabs, r => r.IsVisible && r.Content as string == L.T("apps.game.mode.general")));

            // «Общий»: широкий режим, короткое предупреждение и четыре пункта.
            WaitUi(vm.Net.SetGameModeAsync(GameMode.TcpUdp));
            Pump();
            Show(pit);
            Shot(w, "games-mode-general");

            WaitUi(vm.Net.SetGameModeAsync(GameMode.Off));
            Pump();
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static T Named<T>(FrameworkElement w, string name) where T : class => w.FindName(name) as T ?? throw new Exception("Нет элемента " + name);
}
