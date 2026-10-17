using System.Diagnostics;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Окно открывается в памяти (за пределами экрана), все вкладки строятся, ошибок привязок нет.
/// Без прав администратора и сети: ядро не ставится (InitializeAsync не вызывается). Окно без прав: данные экранов — от помощника
/// (настоящий над временной папкой, по каналу в процессе; sc и WMI подменены) или помощника нет — режим просмотра.
/// Поймал бы падение 2026-10-16: TwoWay-привязка ProgressBar.Value к свойству без публичного set.
/// DPISHELL_SNAPSHOTS=&lt;папка&gt; — тест тем сохраняет PNG каждой вкладки в светлой и тёмной теме: &lt;тема&gt;-&lt;вкладка&gt;.png.
/// </summary>
public sealed class WindowSmokeTests
{
    sealed class BindingErrors : TraceListener
    {
        public readonly List<string> Lines = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) lock (Lines) Lines.Add(message); }
    }

    // Один STA-поток с одним Application на все тесты окна: ресурсы Application (кисти, стили) привязаны к потоку,
    // где созданы, а второй Application в процессе создать нельзя.
    static readonly Lazy<Dispatcher> Ui = new(() =>
    {
        Dispatcher? d = null;
        using var ready = new ManualResetEventSlim();
        var t = new Thread(() =>
        {
            d = Dispatcher.CurrentDispatcher;
            // Только стили (Theme.xaml) на голом Application. Не DpiShell.App.App: его конструктор ставит в очередь
            // OnStartup, и тот показывал пользователю «нужны права администратора» прямо во время тестов.
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DpiShell;component/Theme.xaml") });
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "wpf-tests" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        ready.Wait();
        return d!;
    });

    static readonly object Gate = new();

    static void RunUi(Action body)
    {
        lock (Gate)
        {
            Exception? error = null;
            Ui.Value.Invoke(() =>
            {
                try { body(); }
                catch (Exception e) { error = e; }
            });
            if (error is not null) throw new Exception("Окно упало: " + error.Message, error);
        }
    }

    static List<string> WithBindingErrors(Action body)
    {
        var errors = new BindingErrors();
        RunUi(() =>
        {
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            try { body(); }
            finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); }
        });
        return errors.Lines;
    }

    static T? Find<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        if (root is T t && (match is null || match(t))) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), match) is { } found) return found;
        return null;
    }

    /// <summary>Дождаться задачи на потоке окна, прокачивая очередь (её продолжения идут через Dispatcher).</summary>
    static void WaitUi(Task t)
    {
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        t.GetAwaiter().GetResult();
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    /// <summary>Окно за экраном, без активации и панели задач. Таймеры окна остановлены: они ходят в sc.exe.</summary>
    static MainWindow Open(MainViewModel vm)
    {
        var w = new MainWindow(vm)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
        };
        foreach (var name in new[] { "_timer", "_netSettle", "_clock" })
            (Field(w, name) as DispatcherTimer)?.Stop();
        w.Show();
        Pump();
        return w;
    }

    /// <summary>Спрятать окно и убрать значок из трея (иначе висит до конца процесса).</summary>
    static void Close(MainWindow w)
    {
        w.Hide();
        (Field(w, "_tray") as IDisposable)?.Dispose();
    }

    static object? Field(object o, string name) =>
        o.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(o);

    static void SetProp(object o, string name, object? value) => o.GetType().GetProperty(name)!.SetValue(o, value);

    /// <summary>Окно с настоящим помощником над временной папкой этого Shell (подключён до открытия окна).</summary>
    static (MainViewModel, HelperFixture) WithPcHelper(Shell shell, WindowSettings settings)
    {
        var f = new HelperFixture(backend: PcHelper.Over(shell));
        // Канал — в фоне: поток окна здесь занят, а продолжения клиента иначе ждали бы его.
        var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
        return (TestWindow.Model(shell, link, settings), f);
    }

    static void Stop(MainViewModel vm, HelperFixture f) => Task.Run(async () =>
    {
        await vm.Helper.DisposeAsync();
        await f.DisposeAsync();
    }).GetAwaiter().GetResult();

    static readonly (int Page, string Name)[] AllPages =
        [(Pages.Home, "home"), (Pages.Select, "select"), (Pages.Targets, "targets"), (Pages.Net, "net"), (Pages.Hosts, "hosts"), (Pages.Diag, "diag"), (Pages.Settings, "settings")];

    [Fact]
    public void MainWindow_OpensAndAllPagesBind_WithoutErrors()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            var w = Open(vm);
            var tabs = Find<TabControl>(w)!;
            foreach (var page in new[] { Pages.Select, Pages.Targets, Pages.Net, Pages.Hosts, Pages.Diag, Pages.Router, Pages.Settings, Pages.Home })
            {
                vm.Page = page;
                Pump();
                // Пункты меню стоят на экране в порядке вкладок, какая бы ни была выбрана (TabPanel переставлял выбранную в конец).
                var ys = tabs.Items.Cast<TabItem>().Select(t => t.TranslatePoint(new Point(0, 0), w).Y).ToList();
                if (!ys.SequenceEqual(ys.Order())) throw new Exception($"Меню перемешано при вкладке {page}: {string.Join(", ", ys)}");
            }
            // Режимы подбора: «Полный» (по умолчанию) — первым.
            vm.Page = Pages.Select;
            Pump();
            var modes = new List<RadioButton>();
            Collect(w, modes);
            var mode = modes.Where(r => BindingOperations.GetBinding(r, ToggleButton.IsCheckedProperty)?.Path.Path is "Full" or "Quick").ToList();
            if (mode.Count != 2 || mode[0].IsChecked != true || !Text(mode[0]).StartsWith("Полный"))
                throw new Exception("Первым должен стоять и быть выбран «Полный»: " + string.Join(", ", mode.Select(Text)));
            // Цвет выбранного пункта меню не протекает в содержимое вкладки (заголовок страницы — обычным цветом текста).
            var h1 = Find<TextBlock>(w, t => t.Text == "Подбор стратегии")!;
            if (((SolidColorBrush)h1.Foreground).Color != ((SolidColorBrush)w.Foreground).Color)
                throw new Exception("Заголовок страницы унаследовал цвет пункта меню: " + h1.Foreground);
            if (Find<TextBlock>(w, t => t.Text == "Что должно открываться") is { } h2 && ((SolidColorBrush)h2.Foreground).Color != ((SolidColorBrush)w.Foreground).Color)
                throw new Exception("Подзаголовок унаследовал цвет пункта меню");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static void Collect<T>(DependencyObject root, List<T> into) where T : DependencyObject
    {
        if (root is T t) into.Add(t);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) Collect(VisualTreeHelper.GetChild(root, i), into);
    }

    static string Text(RadioButton r) => r.Content is TextBlock tb ? string.Concat(tb.Inlines.OfType<System.Windows.Documents.Run>().Select(x => x.Text)) : r.Content?.ToString() ?? "";

    /// <summary>
    /// Обе темы: окно строится без ошибок привязок, фон берётся из словаря темы,
    /// переключение на лету (из окна и «как в системе») не падает.
    /// </summary>
    [Fact]
    public void Themes_LightAndDark_BuildAndSwitchOnTheFly()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var settings = TestWindow.Settings(shell);
                settings.Theme = theme;
                // Обновления (architecture.md «Обновления данных»): у источника данные новее и вышла новая программа — плашка на главной и карточка в настройках.
                settings.UpdatesData = "2099-01-01";
                settings.UpdatesApp = "99.0";
                settings.UpdatesCheckedAt = settings.UpdatesOkAt = new DateTime(2026, 10, 17, 12, 0, 0);
                // Тёмная — в режиме без службы: на снимках видны обе подсказки у галочки и у настройки закрытия.
                if (theme == ThemeManager.Dark) shell.State.RunMode = "process";
                var (vm, helper) = WithPcHelper(shell, settings);
                Fill(vm, shell);
                var w = Open(vm);
                AssertPage(w, theme == ThemeManager.Dark);

                if (snapDir is not null)
                {
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-loading.png"));
                    // Помощник молчит дольше 10 с — выход с заставки (UX-аудит, п. 46).
                    var text = vm.LoadingText;
                    SetProp(vm, "LoadSlow", true);
                    SetProp(vm, "LoadingText", "Помощник отвечает дольше обычного. Можно подождать, повторить или открыть окно без него — только для просмотра.");
                    Pump();
                    Snap(w, Path.Combine(snapDir, $"{theme}-loading-slow.png"));
                    SetProp(vm, "LoadSlow", false);
                    SetProp(vm, "LoadingText", text);
                }
                foreach (var (page, name) in AllPages.Append((Pages.Router, "router-none")))
                {
                    // Пока «загрузка» — смена вкладки не запускает проверку службы (sc.exe); потом снять заставку для снимка.
                    SetProp(vm, "IsLoading", true);
                    vm.Page = page;
                    Pump();
                    if (page == Pages.Home)
                    {
                        AssertHome(w, vm);
                        if (vm.Autostart.Enabled != (theme == ThemeManager.Dark))
                            throw new Exception("Автозапуск по умолчанию: включён без службы, выключен в режиме службы");
                    }
                    if (page == Pages.Settings) AssertServiceMode(w, vm);
                    // Роутера нет — вкладка всё равно есть: пустое состояние и адрес (UX-аудит п. 47). Ещё не искали — «не проверялся» (UX-аудит 2, п. 10),
                    // и как поставить — ссылка на инструкцию (п. 9).
                    if (page == Pages.Router && (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "Роутер не проверялся" && t.IsVisible) is null
                                                 || Find<Button>(Find<TabControl>(w)!, b => b.Content as string == "Как поставить на роутер ↗" && b.IsVisible) is null))
                        throw new Exception("На вкладке «Роутер» без роутера нет пустого состояния или ссылки «Как поставить»");
                    // Вход на роутер — небольшое окно на месте (п. 14): Enter — «Войти», Esc — «Отмена».
                    if (page == Pages.Router)
                    {
                        var login = RouterLoginDialog.Create(vm.Router, null);
                        login.WindowStartupLocation = WindowStartupLocation.Manual;
                        login.Left = login.Top = -20000;
                        login.ShowActivated = false;
                        login.Show();
                        Pump();
                        if (Find<Button>(login, b => b.IsDefault && b.Content as string == "Войти") is null || Find<Button>(login, b => b.IsCancel) is null || Find<PasswordBox>(login) is null)
                            throw new Exception("В окне входа нет пароля, «Войти» по Enter или «Отмена» по Esc");
                        // Пароль не задан — явный выбор (п. 11); «пустое — с сохранённым» — только когда сохранённый есть (здесь его нет).
                        if (Find<CheckBox>(login, c => c.Content as string == "Пароль не задан (свежая OpenWrt)") is null)
                            throw new Exception("В окне входа нет «Пароль не задан»");
                        if (Find<TextBlock>(login, t => t.Text.Contains("вход с сохранённым")) is not null)
                            throw new Exception("Подсказка про сохранённый пароль — а сохранённого нет");
                        if (snapDir is not null) Snap(login, Path.Combine(snapDir, $"{theme}-router-login.png"));
                        login.Close();
                    }
                    if (snapDir is null) continue;
                    SetProp(vm, "IsLoading", false);
                    typeof(MainViewModel).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                    Pump();
                    Snap(w, Path.Combine(snapDir, $"{theme}-{name}.png"));
                    // Главная раскрыта: все помехи и список стратегий; затем прокручена до конца.
                    if (page == Pages.Home)
                    {
                        vm.ShowAllIssues = true;
                        vm.ShowStrategies = true;
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-open.png"));
                        if (Find<ScrollViewer>(Find<TabControl>(w)!, s => s.TemplatedParent is not ListBox && s.ScrollableHeight > 0) is { } hv)
                        {
                            hv.ScrollToEnd();
                            Pump();
                            Snap(w, Path.Combine(snapDir, $"{theme}-home-end.png"));
                            hv.ScrollToHome();
                        }
                        vm.ShowAllIssues = false;
                        vm.ShowStrategies = false;
                        Pump();
                        // Идёт «Проверить сейчас» (сама после «Включить», UX-аудит 2, п. 1): в карточке «Проверяю…», прежних отметок нет.
                        var rows = vm.CheckRows.ToList();
                        var summary = vm.CheckSummary;
                        vm.CheckRows.Clear();
                        SetProp(vm, "CheckSummary", "Проверяю…");
                        SetProp(vm, "Checking", true);
                        Pump();
                        if (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "Проверяю…" && t.IsVisible) is null) throw new Exception("Нет «Проверяю…» в карточке");
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-checking.png"));
                        SetProp(vm, "Checking", false);
                        SetProp(vm, "CheckSummary", summary);
                        foreach (var r in rows) vm.CheckRows.Add(r);
                        Pump();
                    }
                    if (page == Pages.Diag && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } sv)
                    {
                        // «Другой DNS» с главной (UX-аудит 2, п. 4): к выбору DNS этого компьютера, список вариантов раскрыт.
                        w.ShowSpot(Spot.PcDns);
                        Pump();
                        var picker = Find<DnsPickerView>(Find<TabControl>(w)!, v => v.IsVisible && v.DataContext == vm.PcDns)!;
                        var combo = Find<ComboBox>(picker)!;
                        if (!combo.IsDropDownOpen || sv.VerticalOffset == 0) throw new Exception("«Другой DNS» не привёл к выбору DNS");
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-choose-dns.png"));
                        combo.IsDropDownOpen = false;
                        sv.ScrollToHome();
                        Pump();
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-end.png"));
                        // Середина: проверки окружения и кэш Discord.
                        var card = Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "Что ещё может мешать")!;
                        sv.ScrollToVerticalOffset(card.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-checks.png"));
                        // DNS — одна карточка: вывод проверки, таблица в «Подробнее» (раскрыта) и «DNS этого компьютера» (свой вариант раскрыт).
                        var dnsCard = Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "DNS" && t.IsVisible)!;
                        if (Find<Expander>(Find<TabControl>(w)!, e => e.Header as string == "Свой вариант") is { } own) own.IsExpanded = true;
                        if (Find<Expander>(Find<TabControl>(w)!, e => e.Header as string == "Подробнее: ответы DNS") is { } answers) answers.IsExpanded = true;
                        Pump();
                        sv.ScrollToVerticalOffset(dnsCard.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-dns.png"));
                        sv.ScrollToHome();
                    }
                    // Настройки: конец — «О программе» и «Очистка и удаление».
                    if (page == Pages.Settings && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } stv)
                    {
                        stv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-settings-end.png"));
                        stv.ScrollToHome();
                    }
                    if (page == Pages.Net && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } nv)
                    {
                        nv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-net-end.png"));
                        nv.ScrollToHome();
                    }
                    if (page == Pages.Hosts)
                    {
                        // Карточка «(i)»: открыть как по нажатию и снять её содержимое.
                        var info = Find<InfoButton>(Find<TabControl>(w)!, b => b.Topic == InfoTexts.Hosts)!;
                        var popup = (System.Windows.Controls.Primitives.Popup)Field(info, "_popup")!;
                        ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(info)).Invoke();
                        Pump();
                        if (!info.IsOpen) throw new Exception("Карточка «(i)» не открылась");
                        Save((FrameworkElement)popup.Child, Path.Combine(snapDir, $"{theme}-info.png"));
                        popup.IsOpen = false;
                        Pump();
                    }
                }
                SetProp(vm, "IsLoading", true);

                // На лету: из окна — в противоположную, обратно, затем «как в системе».
                vm.ThemeDark = theme == ThemeManager.Light;
                vm.ThemeLight = theme == ThemeManager.Dark;
                Pump();
                AssertPage(w, theme == ThemeManager.Light);
                if (theme == ThemeManager.Light) vm.ThemeLight = true; else vm.ThemeDark = true;
                Pump();
                AssertPage(w, theme == ThemeManager.Dark);
                vm.ThemeAuto = true;
                Pump();
                AssertPage(w, ThemeManager.SystemPrefersDark());
                ThemeManager.Refresh();
                Pump();
                if (WindowSettings.Load(settings.File).Theme != ThemeManager.Auto) throw new Exception("Выбор темы не сохранился в настройках окна");
                Close(w);
                Stop(vm, helper);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Роутер подключён (FakeRouter на localhost): главная с блоком роутера, «Подбор» на роутере с итогом, «Свои адреса» роутера,
    /// «Диагностика» с карточками роутера — без ошибок привязок, в обеих темах. Снимки: &lt;тема&gt;-router-&lt;вкладка&gt;.png.
    /// </summary>
    [Fact]
    public void Router_Connected_PagesBind_AndSnap()
    {
        using var tmp = new TempDir();
        using var fake = new FakeRouter
        {
            SelectSteps = 0,
            ConflictsJson = """[ { "kind": "service", "name": "zapret2", "running": true }, { "kind": "package", "name": "zapret-manager-beam", "running": true } ]""",
            SelectRowsJson = """
                [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null },
                  { "id": "alt9", "group_share": 1.0, "target_share": 0.80, "avg_s": 0.61, "error": null },
                  { "id": "alt10", "group_share": 0.5, "target_share": 0.5, "avg_s": 0.92, "error": null },
                  { "id": "d-17", "title": "fake + multisplit, ts — YouTube", "group_share": 0.5, "target_share": 0.62, "avg_s": 0.83, "error": null, "candidate": true, "ok": 3, "total": 4 },
                  { "id": "alt2", "group_share": 0.0, "target_share": 0.0, "avg_s": 0, "error": "не открылось ни одной группы" } ]
                """,
        };
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("router-" + theme), Repo.Data));
                var settings = TestWindow.Settings(shell);
                settings.Theme = theme;
                shell.State.UserTargets = ["example.com", "my-site.ru"];
                var (vm, helper) = WithPcHelper(shell, settings);
                var router = vm.Router;
                router.AddressAuto = false;
                router.CustomAddress = fake.Address;
                router.PollInterval = TimeSpan.FromMilliseconds(10);
                router.Gateway = () => null;
                router.PcTimestamps = () => false;   // netsh не зовём; метки «выключены» — видна подсказка роутера
                router.EnablePcTimestamps = _ => Task.FromResult(false);
                router.Confirm = _ => true;
                // Сеть — в фоне без контекста окна (поток окна здесь занят), потом окно строится по готовому состоянию.
                Task.Run(async () =>
                {
                    if (!await router.ConnectAsync("secret")) throw new Exception("Не подключились: " + router.ConnectionText);
                    await router.StartSelectAsync(deep: false);
                    await router.LoadTargetsAsync();
                    await router.RefreshDiagAsync();
                    await router.RefreshRouterPageAsync();
                    router.SiteUrl = "example.org";
                    await router.SiteCheckAsync();
                }).GetAwaiter().GetResult();
                router.TargetsOnRouter = true;
                router.SelectedScore = router.Scores.First(x => x.Id == "alt9");
                Fill(vm, shell);

                var w = Open(vm);
                foreach (var (page, name) in AllPages.Append((Pages.Router, "tab")))
                {
                    SetProp(vm, "IsLoading", true);
                    vm.Page = page;
                    Pump();
                    SetProp(vm, "IsLoading", false);
                    typeof(MainViewModel).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                    Pump();
                    var tabs = Find<TabControl>(w)!;
                    if (page == Pages.Home)
                    {
                        // Роутер — одной строкой со ссылкой на вкладку; обход делает он — кнопка ПК второстепенная.
                        if (Find<TextBlock>(tabs, t => t.Text == "Обход делает роутер" && t.IsVisible) is null) throw new Exception("На главной нет строки роутера");
                        if (Find<Button>(tabs, b => b.Content as string == "Подробнее →" && b.IsVisible) is null) throw new Exception("У строки роутера нет ссылки на вкладку");
                        if (Find<Button>(tabs, b => b.Content as string == "Включить и на этом ПК" && b.IsVisible && b.Style != (Style)w.FindResource("Primary")) is null)
                            throw new Exception("При обходе на роутере кнопка ПК должна быть второстепенной «Включить и на этом ПК»");
                        if (Find<ComboBox>(tabs) is not null) throw new Exception("На главной появился выпадающий список");
                        vm.ShowStrategies = true;
                        Pump();
                        if (Find<Button>(tabs, b => b.Content as string == "Отправить на роутер" && b.IsVisible) is null) throw new Exception("У найденной стратегии нет «Отправить на роутер»");
                    }
                    // Роутер сам включил лучшую (редакция §9) — итог «Включена на роутере», другую можно выбрать в таблице.
                    if (page == Pages.Select && (Find<TextBlock>(tabs, t => t.Text.StartsWith("Включена на роутере") && t.IsVisible) is null
                                                 || Find<Button>(tabs, b => b.Content as string == "Применить выбранную" && b.IsVisible && b.IsEnabled) is null))
                        throw new Exception("На «Подборе» нет итога роутера или «Применить выбранную»");
                    if (page == Pages.Diag && Find<Button>(tabs, b => b.Content as string == "Остановить" && b.IsVisible) is null)
                        throw new Exception("В диагностике роутера нет «Остановить» у конфликта");
                    if (page == Pages.Router && Find<Button>(tabs, b => b.Content as string == "Исправить" && b.IsVisible) is null)
                        throw new Exception("У предупреждения роутера нет «Исправить»");
                    if (page == Pages.Router)
                    {
                        foreach (var text in new[] { "Для каких сайтов обход", "Сайт не открывается", "DNS для всей сети", "Проверять сам", "Сайт открывается только без обхода" })
                            if (Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is null) throw new Exception($"На вкладке «Роутер» нет «{text}»");
                        if (Find<Button>(tabs, b => b.Content as string == "Добавить в исключения" && b.IsVisible) is null) throw new Exception("Нет «Добавить в исключения»");
                        // dns_presets есть — вместо одной кнопки выбор вариантов, как на ПК.
                        if (Find<Button>(tabs, b => b.Content as string == "Включить защищённый DNS" && b.IsVisible) is not null) throw new Exception("Осталась одна кнопка «Включить защищённый DNS»");
                        if (Find<DnsPickerView>(tabs, p => p.IsVisible) is not { } rp || Find<ComboBox>(rp, c => c.Items.Count >= 4) is null)
                            throw new Exception("На вкладке «Роутер» нет выбора DNS");
                    }
                    if (snapDir is null) continue;
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-router-{name}.png"));
                    if (page == Pages.Diag && Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } sv)
                    {
                        // Карточки роутера — в середине и в конце страницы.
                        var card = Find<Button>(tabs, b => b.Content as string == "Сохранить отчёт роутера")!;
                        sv.ScrollToVerticalOffset(card.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-card.png"));
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-end.png"));
                        sv.ScrollToHome();
                    }
                    if (page == Pages.Router && Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } rsv)
                    {
                        var expanders = new List<Expander>();
                        Collect(tabs, expanders);
                        foreach (var e in expanders) e.IsExpanded = true;
                        Pump();
                        rsv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-tab-end.png"));
                        var dnsHead = Find<TextBlock>(tabs, t => t.Text == "DNS для всей сети")!;
                        rsv.ScrollToVerticalOffset(dnsHead.TranslatePoint(new Point(0, 0), (UIElement)rsv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-dns.png"));
                        rsv.ScrollToHome();
                    }
                }
                // Совместимость (§5.4): роутер новее программы — плашка и «Обновить программу»; требует программу новее — Blocked, команды неактивны.
                foreach (var (api, minApp, name) in new[] { ("\"1.3\"", (string?)null, "warning"), ("\"1.0\"", "99.0.0", "blocked") })
                {
                    fake.StatusApi = api;
                    fake.MinApp = minApp;
                    WaitUi(router.RefreshAsync());   // на потоке окна: меняет списки, привязанные к окну
                    vm.Page = Pages.Router;
                    Pump();
                    var tabs = Find<TabControl>(w)!;
                    if (Find<Button>(tabs, b => b.Content as string == "Обновить программу ↗" && b.IsVisible) is null) throw new Exception($"Нет плашки совместимости ({name})");
                    if (name == "blocked" && Find<Button>(tabs, b => b.Content as string == "Проверить" && b.IsVisible && b.IsEnabled) is not null)
                        throw new Exception("При несовместимости «Проверить» активна");
                    if (Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } csv) csv.ScrollToHome();
                    Pump();
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-compat-{name}.png"));
                }
                // Доступно обновление (status.update): на вкладке — строка, «Что нового», «Обновить»; на главной — коротко.
                fake.StatusApi = "\"1.0\"";
                fake.MinApp = null;
                fake.UpdateFieldOn = true;
                WaitUi(router.RefreshAsync());
                Pump();
                {
                    var tabs = Find<TabControl>(w)!;
                    if (Find<Button>(tabs, b => b.Content as string == "Обновить" && b.IsVisible && b.IsEnabled) is null) throw new Exception("Нет «Обновить» у доступного обновления");
                    if (Find<Expander>(tabs, e => e.Header as string == "Что нового" && e.IsVisible) is not { } news) throw new Exception("Нет «Что нового»");
                    news.IsExpanded = true;
                    if (Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } usv) usv.ScrollToHome();
                    Pump();
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-update.png"));
                    vm.Page = Pages.Home;
                    Pump();
                    if (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text.Contains("доступно обновление 0.2.1-r1") && t.IsVisible) is null && !router.HomeLine.Contains("предупрежд"))
                        throw new Exception("На главной нет «доступно обновление»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-update-home.png"));
                }
                fake.UpdateFieldOn = false;
                fake.StatusApi = "1";
                SetProp(vm, "IsLoading", true);
                Close(w);
                router.Dispose();
                Stop(vm, helper);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Установка и удаление на роутере (FakeRouter + FakeSsh): диалоги строятся без ошибок привязок в обеих темах; кнопки на месте.
    /// Снимки: &lt;тема&gt;-setup-&lt;состояние&gt;.png.
    /// </summary>
    [Fact]
    public void RouterSetup_Dialogs_BindAndSnap()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                using var fake = new FakeRouter().As020();
                var shell = new Shell(new AppPaths(tmp.Sub("setup-" + theme), Repo.Data));
                var settings = TestWindow.Settings(shell);
                var offline = RouterSshScenarioTests.PreflightOk.Replace("\"net\":{\"feed\":true,\"fw_feed\":true,\"github\":true}", "\"net\":{\"feed\":false,\"fw_feed\":true,\"github\":false}");
                var bad = RouterSshScenarioTests.PreflightOk.Replace("\"foreign\":[]", "\"foreign\":[{\"kind\":\"package\",\"name\":\"zapret\"}]")
                    .Replace("\"tmp_free_kb\":221000", "\"tmp_free_kb\":9000");
                var failed = "# Ключ витрины DpiShell\n# Витрина пакетов\n# Пакеты DpiShell и зависимости\n"
                             + "{\"schema\":1,\"ok\":false,\"step\":\"packages\",\"message\":\"ERROR: unable to select packages:\\n  dpishell (no such package):\\n    required by: world[dpishell]\",\"version\":null,\"api\":null,\"core\":null}\n";
                var router = new RouterViewModel(shell, settings)
                {
                    AddressAuto = false, CustomAddress = fake.Address, PollInterval = TimeSpan.FromMilliseconds(10), Gateway = () => "192.168.1.1",
                    PcTimestamps = () => true, Http = new HttpClient(new RouterSetupViewModelTests.NoNetwork()), CoreCandidates = [RouterSetupViewModelTests.Vendor],
                };

                Window Show(Window d)
                {
                    d.WindowStartupLocation = WindowStartupLocation.Manual;
                    d.Left = d.Top = -20000;
                    d.ShowActivated = false;
                    d.Show();
                    Pump();
                    return d;
                }
                void Shot(Window d, string name)
                {
                    Pump();
                    if (snapDir is null) return;
                    Directory.CreateDirectory(snapDir);
                    Snap(d, Path.Combine(snapDir, $"{theme}-setup-{name}.png"));
                }

                // Удаление: подтверждение (не в один клик, «Отмена» по умолчанию) → итог «Ничего не осталось» и «Установить заново».
                var uninstallSsh = RouterSetupViewModelTests.Ssh(fake);
                RouterSetupViewModelTests.UninstallWritesResult(fake, uninstallSsh);
                router.SshConnector = new FakeSshConnector(uninstallSsh);
                Task.Run(() => router.ConnectAsync("secret")).GetAwaiter().GetResult();
                var u = router.NewUninstall();
                var ud = Show(RouterUninstallDialog.Create(u, null));
                if (Find<Button>(ud, b => b.Content as string == "Удалить" && !b.IsEnabled) is null) throw new Exception("«Удалить» активна без подтверждения");
                if (Find<Button>(ud, b => b.Content as string == "Отмена" && b.IsDefault && b.IsCancel) is null) throw new Exception("«Отмена» не по умолчанию");
                Shot(ud, "uninstall-confirm");
                u.Understood = true;
                Task.Run(() => u.UninstallAsync()).GetAwaiter().GetResult();
                Pump();
                if (Find<Button>(ud, b => b.Content as string == "Установить заново" && b.IsVisible) is null) throw new Exception("После удаления нет «Установить заново»");
                Shot(ud, "uninstall-done");
                ud.Close();
                // Без входа — по SSH, с паролем.
                fake.Reinstall();
                settings.RouterPassword = null;
                Task.Run(() => router.ConnectAsync("")).GetAwaiter().GetResult();
                var u2 = router.NewUninstall();
                var ud2 = Show(RouterUninstallDialog.Create(u2, null));
                if (Find<PasswordBox>(ud2, p => p.IsVisible) is null) throw new Exception("Без входа удаление не спрашивает пароль");
                Shot(ud2, "uninstall-ssh");
                ud2.Close();
                fake.Remove();

                // Установка: начало → роутер не подходит → подходит, через этот ПК → сбой пакетов с «Подробнее» → готово.
                var i = router.NewInstall();
                var d = Show(RouterInstallDialog.Create(i, null));
                if (Find<Button>(d, b => b.Content as string == "Проверить роутер" && b.IsVisible) is null || Find<PasswordBox>(d) is null
                    || Find<CheckBox>(d, c => c.Content as string == "Пароль не задан (свежая OpenWrt)") is null)
                    throw new Exception("В диалоге установки нет пароля, «Пароль не задан» или «Проверить роутер»");
                Shot(d, "install-start");
                router.SshConnector = new FakeSshConnector(RouterSetupViewModelTests.Ssh(fake, bad));
                Task.Run(() => i.CheckAsync("secret")).GetAwaiter().GetResult();
                Shot(d, "install-unsuitable");
                router.SshConnector = new FakeSshConnector(RouterSetupViewModelTests.Ssh(fake, offline, failed));
                Task.Run(() => i.CheckAsync("secret")).GetAwaiter().GetResult();
                Pump();
                if (Find<Button>(d, b => b.Content as string == "Установить" && b.IsEnabled) is null) throw new Exception("Роутер подходит, а «Установить» неактивна");
                Shot(d, "install-checked");
                Task.Run(() => i.InstallAsync("secret")).GetAwaiter().GetResult();
                Pump();
                if (Find<Expander>(d, e => e.Header as string == "Подробнее" && e.IsVisible) is not { } more) throw new Exception("У ошибки установки нет «Подробнее»");
                more.IsExpanded = true;
                Shot(d, "install-failed");
                router.SshConnector = new FakeSshConnector(RouterSetupViewModelTests.Ssh(fake));
                Task.Run(async () => { await i.CheckAsync("secret"); await i.InstallAsync("secret"); }).GetAwaiter().GetResult();
                Pump();
                if (Find<Button>(d, b => b.Content as string == "Подобрать на роутере" && b.IsVisible) is null) throw new Exception("После установки нет «Подобрать на роутере»");
                Shot(d, "install-done");
                d.Close();

                // Ключ роутера изменился: «Отмена» — главная и по умолчанию, «Доверять новому» — только явно.
                var (hk, _) = HostKeyDialog.Create(RouterViewModel.HostKeyText, null);
                Show(hk);
                if (Find<Button>(hk, b => b.Content as string == "Отмена" && b.IsDefault && b.IsCancel) is null || Find<Button>(hk, b => b.Content as string == "Доверять новому") is null)
                    throw new Exception("В вопросе о ключе роутера «Отмена» не по умолчанию или нет «Доверять новому»");
                Shot(hk, "hostkey");
                hk.Close();
                router.Dispose();
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Разделы docs/info-texts.md: заголовок «## …» → текст (обратные кавычки разметки убраны). Абзацы — через пустую строку «\n\n»:
    /// первый — ответ «когда включать» (UX-аудит, п. 25), в карточке он жирный.
    /// </summary>
    static Dictionary<string, string> DocInfoTexts()
    {
        var root = Directory.GetParent(Repo.Data)!.FullName;
        var map = new Dictionary<string, string>();
        string? head = null;
        var paragraphs = new List<string>();
        var lines = new List<string>();
        void EndParagraph()
        {
            if (lines.Count > 0) paragraphs.Add(string.Join(" ", lines));
            lines.Clear();
        }
        void Flush()
        {
            EndParagraph();
            if (head is not null) map[head] = string.Join("\n\n", paragraphs).Replace("`", "").Trim();
            paragraphs.Clear();
        }
        foreach (var line in File.ReadAllLines(Path.Combine(root, "docs", "info-texts.md")))
        {
            if (line.StartsWith("## ")) { Flush(); head = line[3..].Trim(); continue; }
            if (head is null) continue;
            if (line.Trim().Length == 0) EndParagraph();
            else lines.Add(line.Trim());
        }
        Flush();
        return map;
    }

    /// <summary>У каждого раздела из info-texts.md в окне есть «(i)», и её текст — дословно из документа.</summary>
    [Fact]
    public void InfoButtons_EveryDocSection_HasButton_WithDocText()
    {
        var doc = DocInfoTexts();
        Assert.Equal(12, doc.Count);
        using var tmp = new TempDir();
        using var fake = new FakeRouter();
        var seen = new Dictionary<string, string>();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            // Вкладка «Роутер» со своими «(i)» — только при подключённом роутере.
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
            var w = Open(vm);
            foreach (var (page, _) in AllPages.Append((Pages.Router, "router")))
            {
                vm.Page = page;
                Pump();
                var buttons = new List<InfoButton>();
                Collect(Find<TabControl>(w)!, buttons);
                foreach (var b in buttons.Where(b => b.IsVisible)) seen[b.Topic ?? ""] = b.Text;
            }
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
        foreach (var (head, text) in doc)
        {
            Assert.True(seen.ContainsKey(head), $"Нет кнопки «(i)» у раздела «{head}»");
            Assert.Equal(text, seen[head]);
        }
        Assert.Empty(seen.Keys.Except(doc.Keys));
    }

    /// <summary>
    /// Главная (UX-аудит): одна главная кнопка, стратегии свёрнуты под «Сменить стратегию» (раскрываются — список с выбранной),
    /// помехи — строкой «Нашлось N помех», раскрыта одна самая важная, остальные — по «Ещё N».
    /// </summary>
    static void AssertHome(Window w, MainViewModel vm)
    {
        var tabs = Find<TabControl>(w)!;
        if (Find<ComboBox>(tabs) is not null) throw new Exception("На главной остался выпадающий список");
        var primary = new List<Button>();
        Collect(tabs, primary);
        if (primary.Count(b => b.IsVisible && b.Style == (Style)w.FindResource("Primary")) != 1) throw new Exception("На главной должна быть одна главная кнопка");
        var list = Find<ListBox>(tabs) ?? throw new Exception("На главной нет списка стратегий");
        if (list.IsVisible) throw new Exception("Список стратегий должен быть свёрнут до «Сменить стратегию»");
        vm.ShowStrategies = true;
        Pump();
        if (!list.IsVisible) throw new Exception("«Сменить стратегию» не раскрыл список");
        if (list.Items.Count != vm.StrategyChoices.Count || list.Items.Count == 0) throw new Exception($"В списке {list.Items.Count} строк, ждали {vm.StrategyChoices.Count}");
        if ((list.SelectedItem as StrategyChoice)?.Id != vm.ChosenStrategyId) throw new Exception("В списке не выделена выбранная стратегия");
        vm.ShowStrategies = false;

        // Наполнение: подмена DNS и VPN — видна только первая (DNS), заголовок и «Ещё 1» считают одно и то же (UX-аудит 2, п. 6).
        // Обновление списков — не помеха: видно всегда, не под ⚠.
        if (Find<TextBlock>(tabs, t => string.Concat(t.Inlines.OfType<System.Windows.Documents.Run>().Select(r => r.Text)) == "⚠ Нашлось 2 помехи" && t.IsVisible) is null)
            throw new Exception($"Нет строки «Нашлось 2 помехи»: «{vm.IssuesTitle}», видна: {vm.HasIssuesHeader}");
        Button? Visible(string text) => Find<Button>(tabs, b => b.Content as string == text && b.IsVisible);
        if (Visible("Ещё 1") is null) throw new Exception("Нет «Ещё 1»: " + vm.IssuesToggleText);
        if (Visible("Включить защищённый DNS") is null) throw new Exception("Не видна самая важная помеха (DNS)");
        if (!vm.Updates.DataAvailable || Visible("Обновить списки") is null) throw new Exception("Обновление списков спрятано вместе с помехами");
        if (Visible("Понятно, не показывать") is not null) throw new Exception("Свёрнутые помехи видны");
        vm.ShowAllIssues = true;
        Pump();
        if (Visible("Понятно, не показывать") is null)
            throw new Exception("«Ещё 1» не раскрыл помехи (VPN)");
        vm.ShowAllIssues = false;
        Pump();
    }

    /// <summary>Галочка «Работать, даже когда окно закрыто» показывает режим помощника.</summary>
    static void AssertServiceMode(Window w, MainViewModel vm)
    {
        var mode = Find<CheckBox>(Find<TabControl>(w)!, c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "ServiceMode")
            ?? throw new Exception("Нет галочки «Работать, даже когда окно закрыто»");
        if (mode.IsChecked != (vm.Helper.Status?.RunMode != "process")) throw new Exception("Галочка режима не совпадает с режимом помощника");
    }

    static void AssertPage(Window w, bool dark)
    {
        var expected = (Color)ColorConverter.ConvertFromString(dark ? "#18191B" : "#F4F5F7");
        var actual = (w.Background as SolidColorBrush)?.Color;
        if (actual != expected || ThemeManager.IsDark != dark)
            throw new Exception($"Фон окна {actual}, ждали {expected} ({(dark ? "тёмная" : "светлая")})");
    }

    /// <summary>Наполнение: строки таблиц и плашки — для проверки главной и для снимков. В памяти и во временной папке.</summary>
    static void Fill(MainViewModel vm, Shell shell)
    {
        FillPc(vm, shell);
        var strategies = shell.Catalog.Strategies.Take(6).ToList();
        vm.StrategyChoices.Add(new("found-1", "Найденная: fake + split2", "открыла всё в 2 из 2 подборов (100 %)", true));
        foreach (var (s, i) in strategies.Select((s, i) => (s, i)))
            vm.StrategyChoices.Add(new(s.Id, s.Title, i < 4 ? $"открыла всё в {4 - i} из 4 подборов ({(4 - i) * 25} %)" : "в подборах ещё не участвовала", false));
        vm.ChosenStrategyId = strategies[0].Id;

        SetProp(vm, "VpnWarning", "Включён VPN (WireGuard Tunnel). С ним обход обычно не нужен, а подбор может показать неверный результат.");
        SetProp(vm, "DnsSpoofed", true);
        // DNS этого компьютера: свой вариант в списке, включён Google (защищённый), есть что вернуть, VPN не трогаем.
        vm.PcDns.NewName = "AdGuard"; vm.PcDns.NewIps = "94.140.14.14, 94.140.15.15"; vm.PcDns.NewDoh = "https://dns.adguard-dns.com/dns-query";
        vm.PcDns.AddCustom();
        vm.ShowPcDns(new DnsStatusInfo("google", true, false, true, true,
            [new DnsAdapterInfo("Ethernet", "ethernet", false, ["8.8.8.8", "8.8.4.4"], true), new DnsAdapterInfo("Wi-Fi", "wifi", false, ["8.8.8.8", "8.8.4.4"], true)],
            ["Radmin VPN", "vEthernet (Default Switch)"]));
        SetProp(vm, "CheckSummary", "Открывается 1 из 2");
        vm.CheckRows.Add(new(true, "YouTube", "https://www.youtube.com/", "0.4 с"));
        vm.CheckRows.Add(new(true, "YouTube", "https://rr1---sn-4g5e6nsz.googlevideo.com/", "0.7 с"));
        vm.CheckRows.Add(new(true, "Discord", "https://discord.com/", "0.5 с"));
        vm.CheckRows.Add(new(false, "Discord", "https://gateway.discord.gg/", "соединение сброшено"));

        var t = new Target("https://www.youtube.com/");
        var ok = new TargetVerdict("youtube", t, true, 0.4, [new ProbeResult(t, SslProtocols.Tls13, ProbeOutcome.Ok, 0.4, 1000, "")]);
        var bad = new TargetVerdict("discord", t, false, 0, [new ProbeResult(t, SslProtocols.Tls13, ProbeOutcome.Reset, 0, 0, "")]);
        var rows = new List<ScoreRow>
        {
            new(new StrategyScore(strategies[0], [ok, ok], 2, 2)) { IsBest = true },
            new(new StrategyScore(new Strategy("found-1", "Найденная: fake + split2", "search", []), [ok, bad], 1, 2)),
            new(new StrategyScore(strategies[1], [ok, bad], 1, 2)),
            new(new StrategyScore(strategies[2], [bad, bad], 0, 2)),
            new(new StrategyScore(strategies[3], [], 0, 2, "winws завершился: WinDivert: не удалось открыть драйвер (код 5)")),
        };
        foreach (var r in rows) vm.Scores.Add(r);
        vm.SelectedScore = rows[2];
        SetProp(vm, "SelectProgressMax", 10);
        SetProp(vm, "SelectProgress", 6);
        SetProp(vm, "SelectProgressText", "Проверено 6 из 10");
        SetProp(vm, "ResultText", "Подходит: " + strategies[0].Title);
        SetProp(vm, "ResultHint", "без обхода не открывалось: YouTube, Discord.");
        SetProp(vm, "OfferDeep", true);   // «Искать глубже» — в итоге, не рядом с «Начать подбор» (п. 30)
        typeof(MainViewModel).GetMethod("SetBest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(vm, [strategies[0].Id]);

        vm.TargetsText = string.Join(Environment.NewLine, Enumerable.Range(1, 40).Select(i => i == 3
            ? "очень-длинный-поддомен-для-проверки-горизонтальной-прокрутки.example-with-a-very-long-name.com"
            : $"site{i}.example.com"));
        SetProp(vm, "TargetsMessage", "Сохранено адресов: 40.");

        SetProp(vm, "ServiceText", "работает (стратегия: " + strategies[0].Title + ")");
        SetProp(vm, "DriverText", "работает");
        SetProp(vm, "CoreText", "изменено, возможно антивирусом: winws.exe");
        SetProp(vm, "CoreBroken", true);
        vm.ConflictList.Add("Программа winws.exe (C:\\zapret\\bin\\winws.exe)");
        vm.VpnList.Add("WireGuard Tunnel");
        vm.DnsRows.Add(new("www.youtube.com", "195.208.4.1", "142.250.74.46, 142.250.74.78 …", true));
        vm.DnsRows.Add(new("discord.com", "162.159.135.232", "162.159.135.232", false));
        SetProp(vm, "DnsSummary", "DNS провайдера подменяет адреса сайтов.");
        SetProp(vm, "TimestampsText", MainViewModel.TimestampsLine(true));
        vm.Findings.Add(new(true, "Служба BFE", "работает"));
        vm.Findings.Add(new(false, "Системный прокси", SystemChecks.ProxyFinding((true, "127.0.0.1:8888")).Text));
        vm.Findings.Add(new(true, "Adguard", "не запущен"));
        vm.Findings.Add(new(true, "Папка программы", "в порядке"));

        vm.Message = "Готово.";
    }

    /// <summary>Состояние ПК (его хранит помощник): режимы IP-списка и игр, копия hosts — во временной папке.</summary>
    static void FillPc(MainViewModel vm, Shell shell)
    {
        // «Игры и IP-списки»: список из поставки — во временную папку. Светлая — «Все адреса» с отсчётом (игры ждут),
        // тёмная — «По списку», игры работают.
        bool dark = ThemeManager.IsDark;
        if (!shell.IpLists.SeedFromPackage()) throw new Exception("IP-список из поставки не встал");
        shell.State.IpsetMode = dark ? "list" : "any";
        shell.State.IpsetModeBeforeAny = "list";
        shell.State.IpsetAnyUntil = dark ? null : DateTime.UtcNow.AddMinutes(7.5);
        shell.State.GameMode = "all";
        shell.State.Enabled = dark;

        // «Починить адреса» — только копия hosts во временной папке. Светлая: включено, таблица и предпросмотр;
        // тёмная: выключено, а блок от прошлой установки остался.
        var hostsFile = Path.Combine(shell.Paths.Root, "hosts-copy");
        Directory.CreateDirectory(shell.Paths.Root);
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n0.0.0.0 web.telegram.org\r\n# DpiShell begin v1 2026-10-16\r\n140.82.121.3 github.com\r\n# DpiShell end\r\n");
        shell.Hosts = new HostsManager(shell.Paths, hostsFile);
        shell.State.HostsFix = !dark;

        // Помощник отдаёт это окну (status, details) — экраны строятся по его ответу, как в работе.
        if (Task.Run(() => vm.Helper.FetchAsync(full: true)).GetAwaiter().GetResult() is { } error) throw new Exception("Помощник: " + error);
        vm.ApplyHelperData();
        vm.Net.UserText = "203.0.113.0/24\r\n198.51.100.7";
        SetProp(vm.Net, "UserMessage", "Сохранено: своих подсетей 2, исключений 0.");
        if (!dark)
        {
            var live = new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200");
            var report = new HostsCheckReport(
            [
                new("raw.githubusercontent.com", "146.75.22.132", HostsVerdict.Install, live, OwnerState.Match, "FASTLY", DnsState.Fails, "185.199.108.133: нет ответа"),
                new("github.com", "140.82.121.3", HostsVerdict.Install, live, OwnerState.Match, "GITHUB", DnsState.Fails, "140.82.112.3: нет ответа"),
                new("discord.com", "162.159.138.232", HostsVerdict.NotNeeded, live, OwnerState.Match, "CLOUDFLARENET", DnsState.Works, "162.159.136.232: HTTP 200"),
                new("web.telegram.org", "149.154.167.220", HostsVerdict.BadCertificate, new(EndpointOutcome.BadCertificate, 0.3, "сертификат выдан другому сайту"),
                    OwnerState.Unknown, null, DnsState.Fails, ""),
                new("updates.discord.com", "104.16.0.1", HostsVerdict.Unreachable, new(EndpointOutcome.Timeout, 5, "нет ответа за 5 с"),
                    OwnerState.Unknown, null, DnsState.Works, ""),
            ], DateTime.Now);
            vm.Hosts.ShowReport(report, shell.Hosts.Preview(report.ToInstall));
        }
    }

    static void Snap(Window w, string file)
    {
        var root = (FrameworkElement)VisualTreeHelper.GetChild(w, 0);
        Save(root, file);
    }

    /// <summary>Через VisualBrush: RenderTargetBitmap.Render(e) сдвигает картинку на смещение элемента в родителе.</summary>
    static RenderTargetBitmap Render(FrameworkElement e)
    {
        int width = Math.Max(1, (int)Math.Ceiling(e.ActualWidth)), height = Math.Max(1, (int)Math.Ceiling(e.ActualHeight));
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(e) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, width, height));
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(dv);
        return bmp;
    }

    static void Save(FrameworkElement e, string file)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(Render(e)));
        using var f = File.Create(file);
        enc.Save(f);
    }
}
