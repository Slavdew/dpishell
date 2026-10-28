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
/// Вкладка «Игры и программы»: список профилей первым (строка — название и описание, справа выбор стратегии, тумблер, «⋯»), «Разные стратегии» показывают
/// и прячут выбор, «Общий игровой режим» — отдельный переключатель под списком, профили при нём остаются включёнными; окно «Новый профиль» (подсказки формата,
/// отказ в окне, успех закрывает окно, Esc — отмена); «Импорт…» — строка профиля с предпросмотром и ошибкой в окне; карточка «Записать игру…».
/// На русском и английском, в светлой и тёмной теме (снимки при DPISHELL_SNAPSHOTS: games-*-&lt;язык&gt;-&lt;тема&gt;.png). Ссылки не открываются,
/// настоящих диалогов нет: окна «Новый профиль» и «Импорт профиля» — подмены, показанные за краем экрана; буфер обмена — подмена.
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

    /// <summary>Третий профиль — строкой обмена: без своих параметров (пойдёт стратегией ПК), четыре подсети (в предпросмотре — первые три).</summary>
    static string ArenaCode() => ProfileCode.Encode(new AppProfile
    {
        Title = "Arena Clash", TcpPorts = "27015-27030", UdpPorts = "27015-27030",
        Addresses = ["203.0.113.0/24", "198.51.100.0/24", "198.51.101.0/24", "203.0.114.0/24"],
    });

    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void Apps_tab_profiles_toggles_strategies_general_import_and_record(string lang, bool dark)
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
            string? clipboard = null;
            vm.Apps.CopyText = s => clipboard = s;
            vm.Apps.ReadText = () => clipboard;
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
            var title = Find<TextBlock>(tabs, t => t.IsVisible && t.Text == L.T("apps.title"))!;

            vm.GoTo(Pages.Apps);
            PumpUntil(() => vm.Apps.Variants.Count > 0, "варианты параметров от помощника");

            // Порядок: «Профили программ» — первым, ниже «Общий игровой режим»; «Отладка» и «Дополнительно» свёрнуты; карточки записи нет.
            var profilesCard = Named<Border>(w, "ProfilesCard");
            var generalCard = Named<Border>(w, "GeneralCard");
            Assert.True(Y(profilesCard) < Y(generalCard), "«Общий игровой режим» выше списка профилей");
            Assert.False(Named<Expander>(w, "DebugSection").IsExpanded);
            Assert.False(Named<Expander>(w, "IpListsBlock").IsExpanded);
            Assert.False(Named<Border>(w, "RecordCard").IsVisible);
            Assert.False(VisibleText(tabs, t => t.Text == vm.Apps.Anti), "плашка про античиты висит на вкладке");
            // Переключателя «Выкл / Свои профили / Общий» больше нет.
            Assert.Null(Find<RadioButton>(profilesCard, r => r.IsVisible));
            Assert.Null(Find<RadioButton>(generalCard, r => r.IsVisible));

            // Пустой список: короткое пустое состояние, одна главная кнопка «Добавить профиль…», рядом «Импорт…» и «Записать игру…»; «Разных стратегий» нет.
            Assert.True(VisibleText(tabs, t => t.Text == L.T("apps.profiles.empty")), "нет пустого состояния");
            var add = Btn(tabs, L.T("apps.profiles.add")) ?? throw new Exception("Нет «Добавить профиль…»");
            Assert.Same(w.FindResource("Primary"), add.Style);
            Assert.Single(FindAll<Button>(profilesCard, b => b.IsVisible && ReferenceEquals(b.Style, w.FindResource("Primary"))));
            Assert.NotNull(Btn(profilesCard, L.T("apps.profiles.import")));
            Assert.NotNull(Btn(profilesCard, L.T("apps.profiles.record")));
            Assert.False(Named<CheckBox>(w, "SeparateBox").IsVisible);
            var general = Named<CheckBox>(w, "GeneralSwitch");
            Assert.False(general.IsChecked);
            Assert.True(VisibleText(generalCard, t => t.Text == L.T("apps.game.status.off")));
            Assert.False(VisibleText(generalCard, t => t.Text == L.T("apps.game.warn")), "предупреждение о пинге при выключенном «Общем»");
            Show(title);
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
                Assert.Contains("198.51.100.0/24", L.T("apps.add.cue.subnets"));
                Assert.Contains("<", L.T("apps.add.cue.params"));
                // Ввод параметров — не «импорт»: «Импорт…» — отдельная кнопка на вкладке.
                Assert.False(VisibleText(host, t => t.Text.Contains("мпорт", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("import", StringComparison.OrdinalIgnoreCase)), "в окне слово «импорт»");
                Assert.True(VisibleText(host, t => t.Text == L.T("apps.add.block")), "нет подписи блока «Параметры из сборки Flowseal»");
                Shot(host, "games-add-empty");

                ((TextBox)view.FindName("TitleBox")).Text = "Mistfall Hunter";
                ((TextBox)view.FindName("FilterBox")).Text = GoodFilter;
                ((TextBox)view.FindName("ParamsBox")).Text = GoodParams;
                Pump();
                Assert.Equal("Mistfall Hunter", vm.Apps.ImportTitle);
                Assert.False(VisibleText(host, t => t.Text == cues[0] || t.Text == cues[1] || t.Text == cues[2]), "подсказка видна над заполненным полем");
                PumpUntil(() => Btn(host, L.T("apps.import.add"))!.IsEnabled, "«Добавить» доступна при заполненных полях");
                Shot(host, "games-add-filled");

                WaitUi(vm.Apps.ImportAsync());
                Pump();
                Assert.True(Closed(host), "окно не закрылось после добавления: " + vm.Apps.ImportMessage);
            });
            Assert.True(vm.Apps.HasProfiles, "msg=" + vm.Apps.ImportMessage + " main=" + vm.Message + " busy=" + vm.Busy);
            Assert.False(vm.Apps.ImportFailed);
            // Свои параметры: «Свои» выбрано, «Разные стратегии» включились сами, итог — одной строкой об этом.
            Assert.True(vm.Apps.Separate);
            Assert.True(shell.State.ProfilesSeparate);
            Assert.Equal(AppProfiles.Own, shell.State.AppProfiles.Single().Strategy);
            Assert.StartsWith(L.T("apps.profile.added_own", new { title = "Mistfall Hunter" }), vm.Apps.ImportMessage);
            Assert.DoesNotContain("\n", vm.Apps.ImportMessage);
            PumpUntil(() => Find<TextBlock>(tabs, t => t.IsVisible && t.Text.Contains(L.T("apps.profile.sig_mark"))) is not null, "профиль в списке");
            Assert.True(Find<TextBox>(tabs, t => t.IsVisible && t.Text == vm.Apps.ImportMessage) is not null, "итог добавления не под списком");
            Assert.False(VisibleText(tabs, t => t.Text == L.T("apps.profiles.empty")), "пустое состояние при профиле");

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
            Assert.Equal("", vm.Apps.ImportTitle);

            // 3. Ещё профиль — только параметры и подсети (UDP).
            vm.Apps.ImportTitle = "Voice Chat";
            vm.Apps.ImportParams = "--filter-udp=50000-50100 --dpi-desync=fake --dpi-desync-any-protocol=1";
            vm.Apps.ImportSubnets = "203.0.113.0/24";
            WaitUi(vm.Apps.ImportAsync());
            Assert.Equal(2, vm.Apps.Profiles.Count);

            // 4. «Импорт…»: строка профиля → предпросмотр (название, протоколы и порты, параметры, подсети — число и первые три) → «Добавить».
            void InImport(Action<Window, ProfileImportView> steps)
            {
                vm.Apps.ImportDialog = a =>
                {
                    var host = ProfileImportView.Host(a);
                    host.WindowStartupLocation = WindowStartupLocation.Manual;
                    host.Left = -20000; host.Top = -20000; host.ShowActivated = false; host.ShowInTaskbar = false;
                    bool closed = false;
                    host.Closed += (_, _) => closed = true;
                    void Close() => host.Close();
                    a.CodeClosed += Close;
                    host.Show();
                    Pump(); Pump();
                    try { steps(host, (ProfileImportView)host.Content); }
                    finally { a.CodeClosed -= Close; if (!closed) host.Close(); }
                };
                Btn(profilesCard, L.T("apps.profiles.import"))!.Command.Execute(null);
                Pump();
            }
            InImport((host, view) =>
            {
                var addBtn = Btn(host, L.T("apps.import.add"))!;
                Assert.False(addBtn.IsEnabled);   // пусто — добавлять нечего
                PumpUntil(() => ReferenceEquals(view.FindName("CodeBox"), FocusManager.GetFocusedElement(host)), "фокус в поле строки");
                clipboard = ArenaCode();
                Btn(host, L.T("apps.import.paste"))!.Command.Execute(null);
                Pump(); Pump();
                var preview = (TextBox)view.FindName("Preview");
                Assert.True(preview.IsVisible, "нет предпросмотра");
                Assert.False(((TextBox)view.FindName("Error")).IsVisible);
                Assert.Contains("Arena Clash", preview.Text);
                Assert.Contains("TCP 27015-27030", preview.Text);
                Assert.Contains("UDP 27015-27030", preview.Text);
                Assert.Contains(L.T("apps.code.params_pc"), preview.Text);
                Assert.Contains(L.T("apps.code.subnets", new { n = 4, first = "203.0.113.0/24, 198.51.100.0/24, 198.51.101.0/24, …" }), preview.Text);
                Assert.DoesNotContain("203.0.114.0/24", preview.Text);
                PumpUntil(() => addBtn.IsEnabled, "«Добавить» после разбора");
                Shot(host, "games-import-preview");
                WaitUi(vm.Apps.AddCodeAsync());
                Pump();
                Assert.True(Closed(host), "окно импорта не закрылось после добавления: " + vm.Apps.CodeError);
            });
            Assert.Equal(3, vm.Apps.Profiles.Count);
            var arena = shell.State.AppProfiles.Single(p => p.Title == "Arena Clash");
            Assert.Equal(AppProfiles.Default, arena.Strategy);   // своих параметров в строке нет — «По умолчанию»
            Assert.True(arena.Enabled);
            Assert.Equal(L.T("apps.profile.added", new { title = "Arena Clash" }), vm.Apps.ImportMessage);

            // 5. Ошибка импорта — в окне: битая строка, строка стратегии; «Добавить» недоступна, предпросмотра нет; Esc — отмена.
            InImport((host, view) =>
            {
                var code = (TextBox)view.FindName("CodeBox");
                var error = (TextBox)view.FindName("Error");
                code.Text = StrategyExchange.Encode("Alt", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake" });
                Pump();
                Assert.True(error.IsVisible && error.Text == L.T("core.game_code.is_strategy"), "нет причины для строки стратегии: " + error.Text);
                code.Text = ProfileCode.Prefix + "AAAA!!";
                Pump();
                Assert.True(error.IsVisible && error.IsReadOnly && error.Text == L.T("core.xchg.damaged"), "нет причины в окне: " + error.Text);
                Assert.False(((TextBox)view.FindName("Preview")).IsVisible);
                Assert.False(Btn(host, L.T("apps.import.add"))!.IsEnabled);
                Shot(host, "games-import-error");
                view.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(view)!, 0, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Pump();
                Assert.True(Closed(host), "Esc не закрыл окно импорта");
            });
            Assert.Equal(3, vm.Apps.Profiles.Count);
            Assert.Equal("", vm.Apps.CodeText);

            // 6. Строки: у каждой — тумблер и «⋯», высота строк одинаковая; тумблер выключает только свой профиль.
            Pump();
            var rows = FindAll<ContentPresenter>(Named<ItemsControl>(w, "ProfileList"), c => c.DataContext is ProfileRow && VisualTreeHelper.GetParent(c) is Panel).ToList();
            Assert.Equal(3, rows.Count);
            Assert.Single(rows.Select(r => Math.Round(r.ActualHeight)).Distinct());
            foreach (var r in rows)
            {
                Assert.NotNull(Find<CheckBox>(r, c => c.IsVisible && ReferenceEquals(c.Style, w.FindResource("Switch"))));
                Assert.NotNull(Find<Button>(r, b => b.IsVisible && b.Content as string == "⋯"));
            }
            // Старых кнопок у строки нет.
            Assert.Null(Btn(profilesCard, L.T("apps.profile.remove")));
            var voiceRow = rows.Single(r => ((ProfileRow)r.DataContext).Title == "Voice Chat");
            Find<CheckBox>(voiceRow, c => ReferenceEquals(c.Style, w.FindResource("Switch")))!.IsChecked = false;
            PumpUntil(() => !shell.State.AppProfiles.Single(p => p.Title == "Voice Chat").Enabled && vm.Apps.Profiles.Any(p => p.Title == "Voice Chat" && !p.IsOn), "профиль выключен");
            Assert.All(shell.State.AppProfiles.Where(p => p.Title != "Voice Chat"), p => Assert.True(p.Enabled));
            Assert.DoesNotContain(WinwsArgs.Build(shell.Catalog.Strategies[0], shell.Paths, shell.CurrentOptions), a => a == "--filter-udp=50000-50100");

            // 7. «⋯»: «Экспорт», «Скопировать для Flowseal», «Удалить». «Экспорт» — строка без выбранной стратегии и «вкл».
            var menu = vm.Apps.Profiles.Single(p => p.Title == "Voice Chat").Menu();
            Assert.Equal([L.T("apps.profile.export"), L.T("apps.profile.flowseal"), "", L.T("apps.profile.remove")], menu.Select(m => m.Header));
            menu[0].Run!();
            Assert.StartsWith(ProfileCode.Prefix, clipboard);
            var back = ProfileCode.Decode(clipboard, _ => true);
            Assert.True(back.Enabled);
            Assert.Equal("50000-50100", back.UdpPorts);
            menu[1].Run!();
            Assert.StartsWith(ProfileExchange.FilterMark, clipboard);

            // 8. «Разные стратегии» выкл — выбора у строк нет; вкл — у каждой строки выпадающий список, у Mistfall — «Свои».
            WaitUi(SetSeparate(vm, false));
            PumpUntil(() => !vm.Apps.Separate, "«Разные стратегии» выключены");
            Pump();
            Assert.Null(Find<ComboBox>(profilesCard, c => c.IsVisible));
            Assert.Equal(AppProfiles.Own, shell.State.AppProfiles.Single(p => p.Title == "Mistfall Hunter").Strategy);   // выбор хранится
            Show(title);
            Shot(w, "games-profiles");
            WaitUi(SetSeparate(vm, true));
            PumpUntil(() => vm.Apps.Separate, "«Разные стратегии» включены");
            Pump();
            var combos = FindAll<ComboBox>(profilesCard, c => c.IsVisible).ToList();
            Assert.Equal(3, combos.Count);
            Assert.Contains(combos, c => (c.SelectedItem as ChoiceItem)?.Key == AppProfiles.Own);
            Assert.Equal(L.T("apps.profile.choice.default"), ((ChoiceItem)combos.First(c => ((ProfileRow)c.DataContext).Title == "Arena Clash").SelectedItem).Title);
            Show(title);
            Shot(w, "games-profiles-separate");

            // 9. «Общий игровой режим» вместе с профилями: профили остаются включёнными, строка «срабатывают первыми», предупреждение о пинге.
            WaitUi(vm.Net.SetGameModeAsync(GameMode.TcpUdp));
            Pump();
            Assert.True(general.IsChecked);
            Assert.True(VisibleText(generalCard, t => t.Text == L.T("apps.game.profiles_first")), "нет «Свои профили срабатывают первыми»");
            Assert.True(VisibleText(generalCard, t => t.Text == L.T("apps.game.warn")), "нет предупреждения о пинге");
            Assert.Equal(2, shell.State.AppProfiles.Count(p => p.Enabled));
            var pit = Find<Expander>(generalCard, e => e.IsVisible && e.Header as string == L.T("apps.game.pit.header"))!;
            Show(generalCard);
            Shot(w, "games-general");
            pit.IsExpanded = true;
            Show(pit);
            Shot(w, "games-general-pitfalls");

            // 10. «Записать игру…» — карточка под списком: галка UDP, «Подробнее», плашка про античиты, «Начать»; «Закрыть» убирает её.
            Btn(tabs, L.T("apps.profiles.record"))!.Command.Execute(null);
            Pump(); Pump();
            var card = Named<Border>(w, "RecordCard");
            Assert.True(card.IsVisible);
            Assert.True(Y(card) > Y(profilesCard));
            Assert.True(VisibleText(card, t => t.Text == vm.Apps.Anti), "нет плашки про античиты в карточке записи");
            Assert.NotNull(Find<CheckBox>(card, c => c.IsVisible && c.Content as string == L.T("apps.rec.udp")));
            Assert.NotNull(Btn(card, L.T("apps.start")));
            Show(card);
            Shot(w, "games-record");
            Btn(card, L.T("common.close"))!.Command.Execute(null);
            Pump();
            Assert.False(card.IsVisible);

            WaitUi(vm.Net.SetGameModeAsync(GameMode.Off));
            Pump();
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>«Разные стратегии» — как галкой: свойство уходит помощнику, ждём перечитанный список.</summary>
    static async Task SetSeparate(MainViewModel vm, bool on)
    {
        vm.Apps.Separate = on;
        for (int i = 0; i < 200 && vm.Apps.Separate != on; i++) await Task.Delay(15);
    }

    static T Named<T>(FrameworkElement w, string name) where T : class => w.FindName(name) as T ?? throw new Exception("Нет элемента " + name);
}
