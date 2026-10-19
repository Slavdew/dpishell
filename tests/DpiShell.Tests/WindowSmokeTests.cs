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
public sealed partial class WindowSmokeTests
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

    static IEnumerable<T> FindAll<T>(DependencyObject root, Func<T, bool>? match = null) where T : DependencyObject
    {
        if (root is T t && (match is null || match(t))) yield return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var f in FindAll(VisualTreeHelper.GetChild(root, i), match)) yield return f;
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
        [(Pages.Home, "home"), (Pages.Select, "select"), (Pages.Sites, "sites"), (Pages.Apps, "apps"), (Pages.Hosts, "hosts"), (Pages.Dns, "dns"), (Pages.Diag, "diag"), (Pages.Settings, "settings"), (Pages.About, "about")];

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
            foreach (var page in new[] { Pages.Select, Pages.Sites, Pages.Apps, Pages.Hosts, Pages.Dns, Pages.Diag, Pages.Router, Pages.Settings, Pages.About, Pages.Home })
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

    /// <summary>
    /// «Починить адреса» (ТЗ 12): вкладка длинная — разделы свёрнуты, кроме первого; якоря сверху раскрывают раздел; строки вердиктов
    /// отрисованы, галочки только там, где с ними можно что-то сделать; «Закомментировать» не активна, пока ничего не отмечено.
    /// </summary>
    [Fact]
    public void Hosts_Page_SectionsCollapsed_AnchorsOpenThem_RowsRender()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            var w = Open(vm);
            vm.Page = Pages.Hosts;
            Pump();
            var tabs = Find<TabControl>(w)!;
            string[] names = ["HostsListSection", "HostsTrySection", "HostsUserSection", "HostsAuditSection"];
            Expander Section(string n) => (Expander)(w.FindName(n) ?? throw new Exception("Нет раздела " + n));
            if (!Section(names[0]).IsExpanded || names.Skip(1).Any(n => Section(n).IsExpanded))
                throw new Exception("По умолчанию раскрыт только первый раздел вкладки «Починить адреса»");
            var ys = names.Select(n => Section(n).TranslatePoint(new Point(0, 0), w).Y).ToList();
            if (!ys.SequenceEqual(ys.Order())) throw new Exception("Разделы стоят не в порядке якорей: " + string.Join(", ", ys));

            foreach (var n in names.Skip(1))
            {
                var anchor = Find<Button>(tabs, b => b.Tag as string == n) ?? throw new Exception("Нет якоря " + n);
                ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(anchor)).Invoke();
                Pump();
                if (!Section(n).IsExpanded) throw new Exception("Якорь не раскрыл раздел " + n);
            }

            var live = new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200");
            vm.Hosts.ShowTry(new HostsTryReport(
            [
                new("github.com", "140.82.121.3", HostsTryVerdict.Helps, live, "GITHUB", DnsMatch.SameNet, DnsState.Fails, ""),
                new("discord.com", "162.159.138.232", HostsTryVerdict.NotNeeded, live, null, DnsMatch.Same, DnsState.Works, ""),
                new("gist.github.com", "0.0.0.0", HostsTryVerdict.Breaks, null, null, DnsMatch.Differs, DnsState.Works, ""),
            ], 0, false, 3, DateTime.Now));
            vm.Hosts.ShowAudit(new HostsTryReport(
            [
                new("github.com", "140.82.112.4", HostsTryVerdict.Breaks, null, null, DnsMatch.Differs, DnsState.Works, "", 3, "3:0123456789ab"),
                new("ads.example.com", "0.0.0.0", HostsTryVerdict.Blocks, null, null, DnsMatch.Unknown, DnsState.Fails, "", 4, "4:abcdefabcdef"),
            ], 0, false, 2, DateTime.Now));
            Pump();

            int Boxes(string section) => FindAll<CheckBox>(Section(section), c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == "Выбрать строку").Count();
            if (Boxes("HostsTrySection") != 1) throw new Exception($"Галочка нужна только у «Поможет»: {Boxes("HostsTrySection")}");
            if (Boxes("HostsAuditSection") != 1) throw new Exception($"Галочка нужна только у мешающей строки: {Boxes("HostsAuditSection")}");
            if (vm.Hosts.CommentCmd.CanExecute(null)) throw new Exception("«Закомментировать» активна, хотя ничего не отмечено");
            vm.Hosts.AuditRows.First(r => r.CanSelect).Selected = true;
            if (!vm.Hosts.CommentCmd.CanExecute(null)) throw new Exception("«Закомментировать» не активна после выбора строки");
            // Ничего не вылезает за правый край окна.
            foreach (var n in names)
                if (Section(n).TranslatePoint(new Point(Section(n).ActualWidth, 0), w).X > w.ActualWidth + 1)
                    throw new Exception("Раздел шире окна: " + n);
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// «Подбор»: после «Применить» крупная карточка итога сворачивается в строку «✓ Включена …» со ссылкой «Подробнее»;
    /// в окне высотой 600 точек видно 5–6 строк таблицы; «Подробнее» возвращает карточку, новый подбор — тоже.
    /// </summary>
    [Fact]
    public void Select_AfterApply_ResultCollapsesToOneLine_TableFitsIn600()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var vm = TestWindow.Model(shell);
                var groups = shell.DefaultTargets.Groups.Where(g => g.Id is "youtube" or "discord").ToList();
                var strategies = shell.Catalog.Strategies.Take(14).ToList();
                for (int i = 0; i < strategies.Count; i++)
                {
                    var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
                    {
                        var ps = new[] { SslProtocols.Tls13, SslProtocols.Tls12 }.Select(v => new ProbeResult(t, v, ProbeOutcome.Ok, 0.4, 0, "HTTP 200")).ToList();
                        return new TargetVerdict(g.Id, t, true, 0.4, ps);
                    })).ToList();
                    vm.Scores.Add(new ScoreRow(DpiShell.Core.Selector.Score(strategies[i], groups, verdicts), vm.Names) { IsBest = i == 0 });
                }
                SetProp(vm, "ResultText", "Подходит: " + strategies[0].Title);
                SetProp(vm, "ResultHint", "без оптимизации не открывалось: YouTube, Discord.");
                var w = Open(vm);
                w.Height = 600;
                SetProp(vm, "IsLoading", false);
                vm.Page = Pages.Select;
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => Find<TextBlock>(tabs, t => t.IsVisible && m(t)) is not null;
                int VisibleRows()
                {
                    var grid = Find<DataGrid>(tabs, g => g.IsVisible && g.Items.Contains(vm.Scores[0]))!;
                    return grid.Items.Cast<object>().Select(grid.ItemContainerGenerator.ContainerFromItem).OfType<DataGridRow>()
                        .Count(r => r.IsVisible && r.TranslatePoint(new Point(0, r.ActualHeight - 1), grid).Y <= grid.ActualHeight);
                }

                // Пока не применили — карточка целиком.
                if (!Visible(t => t.Text.StartsWith("Подходит:"))) throw new Exception("До применения нет карточки итога");
                if (Visible(t => t.Text.StartsWith("✓ Включена"))) throw new Exception("До применения уже показана свёрнутая строка");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-select-result-full.png")); }

                // Лучшая включена — одна строка.
                SetProp(vm, "ResultText", "Применена: " + strategies[0].Title + " — оптимизация работает.");
                SetProp(vm, "ResultSummary", $"✓ Включена {strategies[0].Title} · {vm.Scores[0].Groups} · {vm.Scores[0].Share}");
                Pump();
                if (!Visible(t => t.Text.StartsWith("✓ Включена " + strategies[0].Title) && t.Text.Contains(" · "))) throw new Exception("После применения нет строки «✓ Включена …»");
                if (Visible(t => t.Text.StartsWith("Применена:") || t.Text.StartsWith("без оптимизации не открывалось"))) throw new Exception("После применения осталась крупная карточка");
                if (Find<Button>(tabs, b => b.Content as string == "Подробнее" && b.IsVisible && b.IsEnabled) is not { } more) throw new Exception("Нет ссылки «Подробнее»");
                int rows = VisibleRows();
                if (rows < 5) throw new Exception($"В окне 600 точек видно {rows} строк таблицы (ждали 5–6 и больше)");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-select-result-collapsed.png"));

                // «Подробнее» — прежняя карточка.
                more.Command.Execute(null);
                Pump();
                if (!Visible(t => t.Text.StartsWith("Применена:"))) throw new Exception("«Подробнее» не раскрыло карточку");
                if (Visible(t => t.Text.StartsWith("✓ Включена"))) throw new Exception("После «Подробнее» осталась свёрнутая строка");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-select-result-expanded.png"));

                // Новый подбор сбрасывает итог: карточка снова полная.
                typeof(MainViewModel).GetMethod("ResetResults", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                SetProp(vm, "ResultText", "Подходит: " + strategies[1].Title);
                Pump();
                if (!Visible(t => t.Text.StartsWith("Подходит:")) || Visible(t => t.Text.StartsWith("✓ Включена")))
                    throw new Exception("Новый подбор не вернул полную карточку");
                Close(w);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>
    /// Свои стратегии: вставленная строка попадает на «Подбор» как «не проверена» с карточкой «Проверить» (без «Применить»),
    /// на «Главной» у неё метка, «Изменить» и «Удалить»; окно правки — поля по ролям и проверка на лету. Снимки в обеих темах.
    /// Помощник настоящий над временной папкой (strategy_add по каналу), winws не запускается.
    /// </summary>
    [Fact]
    public void OwnStrategies_PastedRowAndEditor_BindAndSnap()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                shell.Paths.EnsureDirectories();
                foreach (var f in Directory.GetFiles(Path.Combine(Repo.Data, "fake"), "*.bin")) File.Copy(f, Path.Combine(shell.Paths.Fake, Path.GetFileName(f)), true);
                var settings = TestWindow.Settings(shell);
                settings.Theme = theme;
                var (vm, helper) = WithPcHelper(shell, settings);
                try
                {
                    SetProp(vm, "IsLoading", false);
                    var groups = shell.DefaultTargets.Groups.Where(g => g.Id is "youtube" or "discord").ToList();
                    var strategies = shell.Catalog.Strategies.Take(5).ToList();
                    foreach (var s in strategies)
                    {
                        var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
                            new TargetVerdict(g.Id, t, true, 0.4, new[] { SslProtocols.Tls13, SslProtocols.Tls12 }.Select(v => new ProbeResult(t, v, ProbeOutcome.Ok, 0.4, 0, "HTTP 200")).ToList()))).ToList();
                        vm.Scores.Add(new ScoreRow(DpiShell.Core.Selector.Score(s, groups, verdicts), vm.Names));
                    }
                    // Чужая стратегия из буфера: строка DpiShell.
                    var roles = new Dictionary<string, string>(shell.Catalog.Strategies[7].Roles);
                    vm.ReadText = () => StrategyExchange.Encode("Стратегия от друга", roles);
                    vm.ToastTime = TimeSpan.FromMinutes(5);
                    WaitUi(vm.PasteStrategyAsync());
                    var id = vm.PendingCheckId ?? throw new Exception("Вставка не дошла до проверки: " + vm.Message);

                    var w = Open(vm);
                    vm.Page = Pages.Select;
                    Pump();
                    var tabs = Find<TabControl>(w)!;
                    bool Visible(Func<TextBlock, bool> m) => Find<TextBlock>(tabs, t => t.IsVisible && m(t)) is not null;
                    if (Find<Button>(tabs, b => b.Content as string == "Проверить" && b.IsVisible) is null) throw new Exception("Нет кнопки «Проверить»");
                    if (Find<Button>(tabs, b => b.Content as string == "Применить" && b.IsVisible) is not null) throw new Exception("«Применить» до проверки");
                    if (Find<Button>(tabs, b => b.Content as string == "Вставить стратегию" && b.IsVisible) is null) throw new Exception("Нет ссылки «Вставить стратегию»");
                    if (!Visible(t => t.Text == "вставлена")) throw new Exception("Нет метки «вставлена» в таблице");
                    if (!Visible(t => t.Text == "не проверена")) throw new Exception("Нет «не проверена» в таблице");
                    if (Find<Button>(tabs, b => b.Content as string == "Скопировать стратегию" && b.IsVisible) is null) throw new Exception("Нет «Скопировать стратегию» у выбранной строки");
                    if (Find<Button>(tabs, b => b.Content as string == "Изменить" && b.IsVisible) is null) throw new Exception("Нет «Изменить» у вставленной");
                    if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-select-pasted.png")); }

                    // Выбрана готовая — то же действие называется «Сделать копию и изменить».
                    vm.SelectedScore = vm.Scores.First(r => r.Id == strategies[0].Id);
                    Pump();
                    if (Find<Button>(tabs, b => b.Content as string == "Сделать копию и изменить" && b.IsVisible) is null) throw new Exception("У готовой нет «Сделать копию и изменить»");
                    vm.SelectedScore = vm.Scores.First(r => r.Id == id);

                    // Главная: список стратегий, у вставленной — метка и в «Параметрах» «Изменить» / «Удалить».
                    SetProp(vm, "ShowStrategies", true);
                    vm.Page = Pages.Home;
                    Pump();
                    var own = vm.StrategyChoices.First(c => c.Id == id);
                    vm.StrategyChoices.Move(vm.StrategyChoices.IndexOf(own), 0);
                    own.ShowParams = true;
                    Pump();
                    var home = Find<TabControl>(w)!;
                    if (Find<Button>(home, b => b.Content as string == "Скопировать стратегию" && b.IsVisible) is null) throw new Exception("Нет «Скопировать стратегию» на главной");
                    if (Find<Button>(home, b => b.Content as string == "Удалить" && b.IsVisible) is null) throw new Exception("Нет «Удалить» у вставленной");
                    if (Find<TextBlock>(home, t => t.Text == "вставлена" && t.IsVisible) is null) throw new Exception("Нет метки «вставлена» на главной");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-own-strategy.png"));
                    own.ShowParams = false;

                    // Окно правки: исходное состояние и с ошибкой.
                    var editor = new StrategyEditor("Стратегия от друга", "Моя правка", roles, catalog: false, n => File.Exists(Path.Combine(Repo.Data, "fake", n))).Start();
                    var host = new Window
                    {
                        Content = new StrategyEditView { DataContext = editor }, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                        FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
                    };
                    host.SetResourceReference(Control.BackgroundProperty, "Page");
                    host.SetResourceReference(Control.ForegroundProperty, "Text");
                    host.Show();
                    Pump();
                    if (!editor.CanSave) throw new Exception("Вставленная стратегия не проходит проверку правки: " + editor.Summary);
                    if (FindAll<TextBox>(host, t => t.FontFamily.Source == "Consolas").Count() != editor.Fields.Count) throw new Exception("Поля ролей не моноширинные или не все");
                    if (snapDir is not null) Snap(host, Path.Combine(snapDir, $"{theme}-edit-strategy.png"));
                    editor.Fields.First(f => f.Role == "general").Text += " --wf-raw=true --dpi-desync-fake-tls=C:\\Windows\\x.bin";
                    editor.Title = "Стратегия от друга";
                    Pump();
                    if (editor.CanSave) throw new Exception("Правка с чужим параметром разрешена");
                    if (Find<Button>(host, b => b.Content as string == "Сохранить")?.IsEnabled != false) throw new Exception("«Сохранить» активна при ошибке");
                    if (Find<TextBlock>(host, t => t.IsVisible && t.Text.Contains("Параметр --wf-raw не разрешён")) is null) throw new Exception("Нет причины в окне правки");
                    if (snapDir is not null) Snap(host, Path.Combine(snapDir, $"{theme}-edit-strategy-error.png"));
                    host.Close();
                    Close(w);
                }
                finally { Stop(vm, helper); }
            }
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
                    if (page == Pages.Settings || page == Pages.About)
                    {
                        // Обновления и версии — только на «Информация и обновления»; в «Настройках» их нет.
                        var tabsAbout = Find<TabControl>(w)!;
                        bool Has(string text) => Find<TextBlock>(tabsAbout, t => t.Text == text && t.IsVisible) is not null
                                                 || Find<Button>(tabsAbout, b => b.Content as string == text && b.IsVisible) is not null;
                        string[] aboutOnly = ["Версия", "Проверить обновления", "Обновить списки", "Ядро zapret (bol-van)", "DpiShell на роутере", "Программа", "Помощник", "Данные"];
                        foreach (var t in aboutOnly)
                            if (Has(t) != (page == Pages.About))
                                throw new Exception($"«{t}»: {(page == Pages.About ? "нет на «Информация и обновления»" : "осталось в «Настройках»")}");
                        if (page == Pages.Settings && Has("Обновления")) throw new Exception("В «Настройках» осталась карточка «Обновления»");
                        if (page == Pages.About)
                        {
                            if (!Has("Не установлено") && Find<TextBlock>(tabsAbout, t => t.Text == "Не установлено" && t.IsVisible) is null) throw new Exception("Нет строки «Не установлено» для роутера");
                            if (!Has("Установить на роутер")) throw new Exception("Нет кнопки «Установить на роутер» на «Информация и обновления»");
                            if (Find<TextBlock>(tabsAbout, t => t.Text.Contains("оптимизация", StringComparison.OrdinalIgnoreCase) && t.IsVisible && t.Text.Contains("zapret")) is not null) throw new Exception("На «Информация и обновления» описана цель");
                            if (Find<Button>(tabsAbout, b => (b.Content as string ?? "").StartsWith("zapret (bol-van)") && b.IsVisible) is null
                                || Find<Button>(tabsAbout, b => (b.Content as string ?? "").StartsWith("github.com/Slavdew/dpishell") && b.IsVisible) is null)
                                throw new Exception("Нет ссылок на zapret и репозиторий");
                        }
                    }
                    if (page == Pages.Settings)
                    {
                        // Неактивная ссылка — серая (Muted), а не бледно-синяя: на неё не жмут.
                        var dead = new Button { Content = "Ссылка", Style = (Style)w.FindResource("LinkButton"), IsEnabled = false };
                        ((Panel)((ScrollViewer)((TabItem)Find<TabControl>(w)!.Items[Pages.Settings]).Content).Content).Children.Add(dead);
                        dead.ApplyTemplate();
                        Pump();
                        var muted = (SolidColorBrush)w.FindResource("Muted");
                        if ((dead.Foreground as SolidColorBrush)?.Color != muted.Color) throw new Exception("Неактивная ссылка не серая");
                        ((Panel)dead.Parent).Children.Remove(dead);
                    }
                    // Роутера нет — вкладка всё равно есть: пустое состояние и адрес (UX-аудит п. 47). Ещё не искали — «не проверялся» (UX-аудит 2, п. 10),
                    // и как поставить — ссылка на инструкцию (п. 9).
                    if (page == Pages.Router && (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "Роутер не проверялся" && t.IsVisible) is null
                                                 || Find<Button>(Find<TabControl>(w)!, b => b.Content as string == "Как поставить на роутер ↗" && b.IsVisible) is null))
                        throw new Exception("На вкладке «Роутер» без роутера нет пустого состояния или ссылки «Как поставить»");
                    // Пока роутер не найден: одна «Установить на роутер», в «Подключении» — только «Найти»; «Найти» ещё и ссылкой в пустом состоянии.
                    if (page == Pages.Router)
                    {
                        var tabsNone = Find<TabControl>(w)!;
                        int installs = FindAll<Button>(tabsNone, b => (b.Content as string ?? "").StartsWith("Установить") && b.IsVisible).Count();
                        int finds = FindAll<Button>(tabsNone, b => b.Content as string == "Найти" && b.IsVisible).Count();
                        if (installs != 1 || finds != 2) throw new Exception($"Без роутера кнопок установки {installs} (ждали 1), «Найти» {finds} (ждали 2)");
                        if (Find<TextBlock>(tabsNone, t => t.Text.Contains("«Найти» ниже")) is not null) throw new Exception("Осталась фраза «Нажмите «Найти» ниже»");
                    }
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
                    // Подбор: строка раскрыта — результаты по всем адресам (обязательные провалы — первыми, отмечены).
                    if (page == Pages.Select)
                    {
                        AssertScoreDetails(w, vm.Scores[2], "не открылось: Discord");
                        Snap(w, Path.Combine(snapDir, $"{theme}-select-detail.png"));
                        vm.Scores[2].Expanded = false;
                        vm.Scores[3].Expanded = true;
                        Pump();
                        {
                            var g = Find<DataGrid>(Find<TabControl>(w)!, x => x.IsVisible && x.Items.Contains(vm.Scores[3]))!;
                            ScrollRowToTop(g, (DataGridRow)g.ItemContainerGenerator.ContainerFromItem(vm.Scores[3]));
                        }
                        Snap(w, Path.Combine(snapDir, $"{theme}-select-detail-control.png"));
                        vm.Scores[3].Expanded = false;
                        Pump();
                    }
                    // Главная раскрыта: все помехи и список стратегий; затем прокручена до конца.
                    if (page == Pages.Home)
                    {
                        // «Проверить сейчас» → «Подробнее»: те же строки по адресам.
                        var more = Find<Expander>(Find<TabControl>(w)!, e => e.Header as string == "Подробнее" && e.IsVisible)!;
                        more.IsExpanded = true;
                        Pump();
                        if (Find<TextBlock>(more, t => t.Text.Contains("TLS 1.3 и TLS 1.2: 301 → ") && t.IsVisible) is null) throw new Exception("В «Подробнее» проверки нет подробностей по TLS");
                        // Адреса разошлись — оба пути с адресами: программа (DoH) и путь через DNS Windows.
                        if (Find<TextBlock>(more, t => t.Text.Contains("Через Windows (DNS Windows: 195.208.4.1)") && t.IsVisible) is null) throw new Exception("В «Подробнее» нет пути через DNS Windows");
                        if (Find<TextBlock>(w, t => t.Text.StartsWith("⚠ В браузере не откроется: YouTube") && t.IsVisible) is null) throw new Exception("В итоге нет ⚠ про браузер");
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-check-detail.png"));
                        more.IsExpanded = false;
                        Pump();
                        vm.ShowAllIssues = true;
                        vm.ShowStrategies = true;
                        var detailed = vm.StrategyChoices.First(c => c.HasWarning);
                        // Только последний прогон (17.10): прошлые подборы о сегодняшней сети не говорят.
                        if (detailed.Stats != "YouTube ✓ · Discord ✓ · свои 7 из 9 · проверено 17.10 в 10:00" || detailed.Warning != "ломает обычные сайты" || !detailed.HasDetails)
                            throw new Exception("Подпись стратегии не по последнему прогону: " + detailed.Stats);
                        detailed.Expanded = true;
                        Pump();
                        // Щелчок по раскрытым адресам не выбирает стратегию (выбор её включает).
                        var chosen = vm.ChosenStrategyId;
                        var addr = Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "свой адрес №1" && t.IsVisible) ?? throw new Exception("Раскрытая стратегия без адресов");
                        addr.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                            { RoutedEvent = UIElement.MouseLeftButtonDownEvent });
                        Pump();
                        if (vm.ChosenStrategyId != chosen || detailed.Id == chosen) throw new Exception("Щелчок по подробностям сменил выбор стратегии");
                        if (Find<ScrollViewer>(Find<TabControl>(w)!, s => s.TemplatedParent is not ListBox && s.ScrollableHeight > 0) is { } top
                            && Find<ListBox>(Find<TabControl>(w)!) is { } lb)
                        {
                            top.ScrollToVerticalOffset(lb.TranslatePoint(new Point(0, 0), (UIElement)top.Content).Y - 40);
                            Pump();
                        }
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-open.png"));
                        // «Параметры»: роли и командная строка winws; «Скопировать параметры» — в буфер (подмена).
                        detailed.Expanded = false;
                        detailed.ShowParams = true;
                        Pump();
                        if (Find<TextBox>(Find<TabControl>(w)!, t => t.Text.Contains("general: --dpi-desync=") && t.Text.Contains("winws.exe --wf-tcp=") && t.IsVisible) is null)
                            throw new Exception("В «Параметры» нет ролей или командной строки");
                        string? copied = null;
                        vm.CopyText = t => copied = t;
                        vm.CopyParamsCmd.Execute(detailed);
                        if (copied is null || !copied.StartsWith(detailed.Title + "\n") || !copied.Contains("{fake}/")) throw new Exception("«Скопировать параметры» скопировал не то: " + copied);
                        if (Find<ScrollViewer>(Find<TabControl>(w)!, s => s.TemplatedParent is not ListBox && s.ScrollableHeight > 0) is { } pv
                            && Find<ListBox>(Find<TabControl>(w)!)?.ItemContainerGenerator.ContainerFromItem(detailed) is FrameworkElement item)
                        {
                            pv.ScrollToVerticalOffset(item.TranslatePoint(new Point(0, 0), (UIElement)pv.Content).Y - 20);
                            Pump();
                        }
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-params.png"));
                        detailed.ShowParams = false;
                        if (Find<ScrollViewer>(Find<TabControl>(w)!, s => s.TemplatedParent is not ListBox && s.ScrollableHeight > 0) is { } top2) top2.ScrollToHome();
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
                    if (page == Pages.Diag && Find<Button>(Find<TabControl>(w)!, b => b.Content as string == "Остановить" && b.IsVisible) is null)
                        throw new Exception("В диагностике ПК у «Похожих программ» нет «Остановить»");
                    if (page == Pages.Diag && w.FindName("PcDnsCard") is FrameworkElement { IsVisible: true }) throw new Exception("Карточка DNS осталась на «Диагностике»");
                    if (page == Pages.Diag)
                        foreach (var text in new[] { "Роутер: что мешает", "Сайт не открывается" })
                            if (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == text && t.IsVisible) is not null) throw new Exception($"«{text}» на «Диагностике» без роутера");
                    if (page == Pages.Sites)
                    {
                        // Роутера нет: только свои адреса ПК — без карточек роутера и заглушек про исключения.
                        foreach (var text in new[] { "Сайт не открывается", "Для каких сайтов оптимизация", "Исключения (оптимизация их не трогает)", "Роутер: что мешает" })
                            if (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == text && t.IsVisible) is not null) throw new Exception($"«{text}» на «Сайтах» без роутера");
                        if (Find<TextBox>(Find<TabControl>(w)!, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Свои адреса" && t.IsVisible) is null) throw new Exception("На «Сайтах» нет поля своих адресов ПК");
                    }
                    if (page == Pages.Dns)
                    {
                        // Карточка DNS этого компьютера лежит на вкладке «DNS», а не на «Диагностике».
                        var dcard = (FrameworkElement)w.FindName("PcDnsCard")!;
                        if (!dcard.IsVisible) throw new Exception("Нет карточки DNS на вкладке «DNS»");
                        DependencyObject? parent = dcard;
                        while (parent is not null and not TabItem) parent = LogicalTreeHelper.GetParent(parent);
                        if ((parent as TabItem)?.Header as string != "DNS") throw new Exception("Карточка DNS лежит не на вкладке «DNS»");
                        Snap(w, Path.Combine(snapDir, $"{theme}-dns.png"));
                        // «Другой DNS» с главной (UX-аудит 2, п. 4): к выбору DNS этого компьютера, список вариантов раскрыт.
                        w.ShowSpot(Spot.PcDns);
                        Pump();
                        var picker = Find<DnsPickerView>(Find<TabControl>(w)!, v => v.IsVisible && v.DataContext == vm.PcDns)!;
                        var combo = Find<ComboBox>(picker)!;
                        if (!combo.IsDropDownOpen) throw new Exception("«Другой DNS» не привёл к выбору DNS");
                        Snap(w, Path.Combine(snapDir, $"{theme}-dns-choose.png"));
                        combo.IsDropDownOpen = false;
                        // Недоступные ссылки (копирование, сравнение) прячутся, а не стоят серыми.
                        if (Find<Button>(picker, b => b.IsVisible && !b.IsEnabled && b.Style is not null && (b.Content as string ?? "").StartsWith("Скопировать")) is not null)
                            throw new Exception("В блоке DNS видна серая ссылка «Скопировать…»");
                        // DNS — одна карточка: вывод проверки, таблица в «Подробнее» (раскрыта) и «DNS этого компьютера» (свой вариант раскрыт).
                        if (Find<Expander>(Find<TabControl>(w)!, e => e.Header as string == "Свой вариант") is { } own) own.IsExpanded = true;
                        if (Find<Expander>(Find<TabControl>(w)!, e => e.Header as string == "Подробнее: ответы DNS") is { } answers) answers.IsExpanded = true;
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-dns-expanded.png"));
                        if (Find<ScrollViewer>(Find<TabControl>(w)!, s => s.IsVisible) is { } dsv) dsv.ScrollToHome();
                    }
                    if (page == Pages.Diag && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } sv)
                    {
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-end.png"));
                        // Середина: проверки окружения и кэш Discord.
                        var card = Find<TextBlock>(Find<TabControl>(w)!, t => t.Text == "Что ещё может мешать")!;
                        sv.ScrollToVerticalOffset(card.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-diag-checks.png"));
                        sv.ScrollToHome();
                    }
                    // Настройки: конец — «Информация и обновления» и «Очистка и удаление».
                    if (page == Pages.Settings && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } stv)
                    {
                        stv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-settings-end.png"));
                        stv.ScrollToHome();
                    }
                    if (page == Pages.Apps && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } nv)
                    {
                        nv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-apps-end.png"));
                        nv.ScrollToHome();
                    }
                    if (page == Pages.Hosts && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.ScrollableHeight > 0) is { } hostsScroll)
                    {
                        // Вкладка длинная: конец (проверка своего hosts, закомментированные) — отдельным снимком.
                        if (w.FindName("HostsTrySection") is Expander { IsExpanded: true } trySection && hostsScroll.Content is UIElement hostsContent)
                        {
                            hostsScroll.ScrollToVerticalOffset(trySection.TranslatePoint(new Point(0, 0), hostsContent).Y - 12);
                            Pump();
                            Snap(w, Path.Combine(snapDir, $"{theme}-hosts-try.png"));
                        }
                        hostsScroll.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-hosts-end.png"));
                        hostsScroll.ScrollToHome();
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
                  { "id": "alt9", "group_share": 1.0, "target_share": 0.80, "avg_s": 0.61, "error": null,
                    "verdicts": [ { "group": "youtube", "url": "https://www.youtube.com/", "ok": true, "detail": "TLS 1.3 и TLS 1.2: 200, 512 КБ", "seconds": 0.52 },
                                  { "group": "discord", "url": "https://discord.com/", "ok": true, "detail": "TLS 1.3 и TLS 1.2: 200, 160 КБ", "seconds": 0.61 },
                                  { "group": "discord", "url": "https://gateway.discord.gg/", "ok": false, "detail": "TLS 1.2: поток оборвался на 16384 байтах", "seconds": 0.12 },
                                  { "group": "control", "url": "https://vk.com/", "ok": true, "detail": "200, 96 КБ", "seconds": 0.2 } ] },
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
                        if (Find<TextBlock>(tabs, t => t.Text == "Оптимизация работает на роутере" && t.IsVisible) is null) throw new Exception("На главной нет строки роутера");
                        if (Find<Button>(tabs, b => b.Content as string == "Подробнее →" && b.IsVisible) is null) throw new Exception("У строки роутера нет ссылки на вкладку");
                        if (Find<Button>(tabs, b => b.Content as string == "Включить и на этом ПК" && b.IsVisible && b.Style != (Style)w.FindResource("Primary")) is null)
                            throw new Exception("При оптимизации на роутере кнопка ПК должна быть второстепенной «Включить и на этом ПК»");
                        if (Find<ComboBox>(tabs) is not null) throw new Exception("На главной появился выпадающий список");
                        vm.ShowStrategies = true;
                        Pump();
                        if (Find<Button>(tabs, b => b.Content as string == "Отправить на роутер" && b.IsVisible) is null) throw new Exception("У найденной стратегии нет «Отправить на роутер»");
                    }
                    // Роутер сам включил лучшую (редакция §9) — итог «Включена на роутере», другую можно выбрать в таблице.
                    // Итог свёрнут в строку «✓ Включена на роутере …»; «Подробнее» возвращает карточку.
                    if (page == Pages.Select)
                    {
                        if (Find<TextBlock>(tabs, t => t.Text.StartsWith("✓ Включена на роутере") && t.IsVisible) is null
                            || Find<TextBlock>(tabs, t => t.Text.StartsWith("Включена на роутере") && t.IsVisible) is not null
                            || Find<Button>(tabs, b => b.Content as string == "Применить выбранную" && b.IsVisible && b.IsEnabled) is null)
                            throw new Exception("На «Подборе» нет свёрнутого итога роутера или «Применить выбранную»");
                        if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-router-select-collapsed.png")); }
                        if (Find<Button>(tabs, b => b.Content as string == "Подробнее" && b.IsVisible && b.DataContext == router) is not { } rmore) throw new Exception("У свёрнутого итога роутера нет «Подробнее»");
                        rmore.Command.Execute(null);
                        Pump();
                        if (Find<TextBlock>(tabs, t => t.Text.StartsWith("Включена на роутере") && t.IsVisible) is null) throw new Exception("«Подробнее» не раскрыло итог роутера");
                    }
                    if (page == Pages.Dns && (Find<TextBlock>(tabs, t => t.Text.StartsWith("DNS для всей сети — на вкладке") && t.IsVisible) is null
                                              || Find<Button>(tabs, b => b.Content as string == "Открыть →" && b.IsVisible) is null))
                        throw new Exception("На вкладке «DNS» нет подсказки про роутер");
                    if (page == Pages.Diag)
                    {
                        // «Диагностика» — только этот ПК: карточки роутера (и его отчёт) переехали на вкладку «Роутер».
                        foreach (var text in new[] { "Роутер: что мешает", "Похожие программы на роутере" })
                            if (Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is not null) throw new Exception($"«{text}» осталось на «Диагностике»");
                        if (Find<Button>(tabs, b => b.Content as string == "Сохранить отчёт роутера" && b.IsVisible) is not null) throw new Exception("«Сохранить отчёт роутера» осталось на «Диагностике»");
                        if (Find<TextBlock>(tabs, t => t.Text == "Похожие программы" && t.IsVisible) is null) throw new Exception("На «Диагностике» нет проверок этого ПК");
                    }
                    if (page == Pages.Sites)
                    {
                        // «Сайты» с роутером: «Сайт не открывается» наверху, «Для каких сайтов обход», свои адреса и исключения (не в раскрывашке).
                        foreach (var text in new[] { "Сайт не открывается", "Для каких сайтов оптимизация", "Свои адреса", "Исключения (оптимизация их не трогает)", "Сайт открывается только без оптимизации" })
                            if (Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is null) throw new Exception($"На вкладке «Сайты» нет «{text}»");
                        if (Find<TextBox>(tabs, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Исключения" && t.IsVisible) is null) throw new Exception("На «Сайтах» не видно поля исключений");
                        if (Find<Button>(tabs, b => b.Content as string == "Добавить в исключения" && b.IsVisible) is null) throw new Exception("Нет «Добавить в исключения»");
                        if (Find<Expander>(tabs, e => (e.Header as string ?? "").StartsWith("Списки роутера") && e.IsVisible) is not null) throw new Exception("Исключения снова спрятаны в раскрывашке");
                        var siteTop = Find<TextBlock>(tabs, t => t.Text == "Сайт не открывается" && t.IsVisible)!.TranslatePoint(new Point(0, 0), w).Y;
                        var addrTop = Find<TextBlock>(tabs, t => t.Text == "Свои адреса" && t.IsVisible && t.Style == (Style)w.FindResource("H2"))!.TranslatePoint(new Point(0, 0), w).Y;
                        if (siteTop > addrTop) throw new Exception("«Сайт не открывается» должно стоять выше своих адресов");
                    }
                    if (page == Pages.Router && Find<Button>(tabs, b => b.Content as string == "Исправить" && b.IsVisible) is null)
                        throw new Exception("У предупреждения роутера нет «Исправить»");
                    if (page == Pages.Router)
                    {
                        // «Роутер: что мешает» (diag, чужие обходы, отчёт) — на «Роутере», рядом с плашками; «Сайты» и режим обхода переехали на «Сайты».
                        foreach (var text in new[] { "Роутер: что мешает", "Похожие программы на роутере", "DNS для всей сети", "Проверять сам" })
                            if (Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is null) throw new Exception($"На вкладке «Роутер» нет «{text}»");
                        foreach (var text in new[] { "Для каких сайтов оптимизация", "Сайт не открывается" })
                            if (Find<TextBlock>(tabs, t => t.Text == text && t.IsVisible) is not null) throw new Exception($"«{text}» осталось на «Роутере»");
                        if (Find<Button>(tabs, b => b.Content as string == "Остановить" && b.IsVisible) is null) throw new Exception("В «Роутер: что мешает» нет «Остановить» у конфликта");
                        if (Find<Button>(tabs, b => b.Content as string == "Сохранить отчёт роутера" && b.IsVisible) is null) throw new Exception("На «Роутере» нет «Сохранить отчёт роутера»");
                        // dns_presets есть — вместо одной кнопки выбор вариантов, как на ПК.
                        if (Find<Button>(tabs, b => b.Content as string == "Включить защищённый DNS" && b.IsVisible) is not null) throw new Exception("Осталась одна кнопка «Включить защищённый DNS»");
                        if (Find<DnsPickerView>(tabs, p => p.IsVisible) is not { } rp || Find<ComboBox>(rp, c => c.Items.Count >= 4) is null)
                            throw new Exception("На вкладке «Роутер» нет выбора DNS");
                    }
                    if (snapDir is null) continue;
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-router-{(page == Pages.Dns ? "pcdns" : name)}.png"));
                    // Подбор на роутере: строка с разбивкой по адресам раскрывается; без неё — подсказка про обновление роутера.
                    if (page == Pages.Select)
                    {
                        var withVerdicts = router.Scores.First(x => x.Id == "alt9");
                        var without = router.Scores.First(x => x.Id == "alt");
                        if (!withVerdicts.HasDetails || without.HasDetails) throw new Exception("Разбивка по адресам роутера не разобрана");
                        withVerdicts.Expanded = without.Expanded = true;
                        Pump();
                        if (Find<TextBlock>(tabs, t => t.Text == "https://gateway.discord.gg/" && t.IsVisible) is null
                            || Find<TextBlock>(tabs, t => t.Text == without.NoDetailsHint && t.IsVisible) is null)
                            throw new Exception("Раскрытые строки роутера: нет адресов или подсказки");
                        {
                            var g = Find<DataGrid>(tabs, x => x.IsVisible && x.Items.Contains(withVerdicts))!;
                            ScrollRowToTop(g, (DataGridRow)g.ItemContainerGenerator.ContainerFromItem(withVerdicts));
                        }
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-select-detail.png"));
                        withVerdicts.Expanded = without.Expanded = false;
                        Pump();
                    }
                    if (page == Pages.Diag && Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } sv)
                    {
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-end.png"));
                        sv.ScrollToHome();
                    }
                    if (page == Pages.Sites && Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } ssv)
                    {
                        ssv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-sites-end.png"));
                        ssv.ScrollToHome();
                    }
                    if (page == Pages.Router && Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } rsv)
                    {
                        var expanders = new List<Expander>();
                        Collect(tabs, expanders);
                        foreach (var e in expanders) e.IsExpanded = true;
                        Pump();
                        var diagCard = Find<TextBlock>(tabs, t => t.Text == "Роутер: что мешает")!;
                        rsv.ScrollToVerticalOffset(diagCard.TranslatePoint(new Point(0, 0), (UIElement)rsv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-card.png"));
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
                    if (Find<Button>(tabs, b => b.Content as string == "Обновить программу ↗") is not null) throw new Exception($"Осталась «Обновить программу ↗» ({name})");
                    if (Find<Button>(tabs, b => (b.Content as string ?? "").StartsWith("Обновить программу →") && b.IsVisible) is not { } toSettings) throw new Exception($"Нет плашки совместимости ({name})");
                    toSettings.Command.Execute(null);
                    if (vm.Page != Pages.About) throw new Exception($"Ссылка «Обновить программу» не привела в «Информация и обновления» ({name})");
                    vm.Page = Pages.Router;
                    Pump();
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
                    // Идёт обновление: шаги (status.update.job.step), прошедшее время, «Подробнее» — хвост журнала.
                    void Field(string name, object? value) => typeof(RouterViewModel).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(router, value);
                    Field("_updateStep", "install");
                    Field("_updateStarted", DateTime.UtcNow - TimeSpan.FromSeconds(83));
                    router.GetType().GetProperty("UpdateLog")!.SetValue(router, "# download: dpishell-0.2.1-r1.apk\n# install: apk add --upgrade dpishell luci-app-dpishell");
                    SetProp(router, "Updating", true);
                    Pump();
                    if (Find<TextBlock>(tabs, t => t.Text.StartsWith("Обновляю DpiShell на роутере… прошло 1:2") && t.IsVisible) is null) throw new Exception("Нет «Обновляю…» с прошедшим временем");
                    if (Find<Expander>(tabs, e => e.Header as string == "Подробнее" && e.IsVisible && e.DataContext == router) is not { } log) throw new Exception("Нет «Подробнее» с журналом обновления");
                    log.IsExpanded = true;
                    Pump();
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-updating.png"));
                    // Не завершилось (running висит, версия прежняя) — так и сказано, кнопка «Повторить».
                    Field("_updateFailed", true);
                    SetProp(router, "Updating", false);
                    Pump();
                    if (Find<Button>(tabs, b => b.Content as string == "Повторить" && b.IsVisible) is null) throw new Exception("Нет «Повторить» у незавершённого обновления");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-update-failed.png"));
                    Field("_updateFailed", false);
                    Field("_updateStep", null);
                    router.GetType().GetProperty("UpdateLog")!.SetValue(router, "");
                    vm.Page = Pages.Home;
                    Pump();
                    if (Find<TextBlock>(Find<TabControl>(w)!, t => t.Text.Contains("доступно обновление 0.2.1-r1") && t.IsVisible) is null && !router.HomeLine.Contains("предупрежд"))
                        throw new Exception("На главной нет «доступно обновление»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-update-home.png"));
                }
                // Аварийный выключатель (§5.1, §5.2): сторож выключил обход — фраза на главной и на вкладке, «Подробнее» с целями, «Включить снова»;
                // сторож в «Проверять сам»; включение на пробу, сайты с ПК не открылись — плашка «Всё работает?» внизу окна.
                fake.UpdateFieldOn = false;
                fake.KillSwitch = true;
                fake.MethodsJson = FakeRouter.Methods020;
                fake.WatchdogTrip();
                WaitUi(router.RefreshAsync());
                vm.Page = Pages.Home;
                Pump();
                {
                    var tabs = Find<TabControl>(w)!;
                    if (Find<TextBlock>(tabs, t => t.Text.StartsWith("Оптимизация на роутере выключена сторожем") && t.IsVisible) is null) throw new Exception("На главной нет фразы о стороже");
                    if (Find<Button>(tabs, b => b.Content as string == "Попробовать снова (на пробу)" && b.IsVisible && b.IsEnabled) is null) throw new Exception("На главной нет «Попробовать снова»");
                    if (Find<Button>(tabs, b => b.Content as string == "Подобрать заново" && b.IsVisible && b.IsEnabled) is null) throw new Exception("В плашке главной нет «Подобрать заново»");
                    if (Find<Button>(tabs, b => b.Content as string == "Включить снова") is not null) throw new Exception("Осталась «Включить снова»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-home.png"));
                    vm.Page = Pages.Router;
                    Pump();
                    tabs = Find<TabControl>(w)!;
                    if (Find<Expander>(tabs, e => e.Header as string == "Подробнее" && e.IsVisible) is not { } more) throw new Exception("На вкладке нет «Подробнее» у выключения");
                    more.IsExpanded = true;
                    if (Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } wsv) wsv.ScrollToHome();
                    Pump();
                    if (Find<TextBlock>(tabs, t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == "https://ya.ru/") && t.IsVisible) is null)
                        throw new Exception("В «Подробнее» нет целей сторожа");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-tab.png"));
                    // Предложение после срабатывания сторожа: вопрос, «Подобрать» и «Не сейчас»; «Не сейчас» прячет его, «Попробовать снова» остаётся.
                    if (Find<TextBlock>(tabs, t => t.Text == "Подобрать на роутере другую стратегию сейчас?" && t.IsVisible) is null) throw new Exception("Нет вопроса «Подобрать на роутере другую стратегию сейчас?»");
                    if (Find<Button>(tabs, b => b.Content as string == "Подобрать" && b.IsVisible && b.IsEnabled) is null) throw new Exception("В предложении нет «Подобрать»");
                    if (Find<Button>(tabs, b => b.Content as string == "Не сейчас" && b.IsVisible) is not { } later) throw new Exception("В предложении нет «Не сейчас»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-offer.png"));
                    later.Command.Execute(null);
                    Pump();
                    if (Find<Button>(tabs, b => b.Content as string == "Не сейчас" && b.IsVisible) is not null || Find<TextBlock>(tabs, t => t.Text.StartsWith("Подобрать на роутере другую") && t.IsVisible) is not null)
                        throw new Exception("«Не сейчас» не спрятало предложение");
                    if (Find<Button>(tabs, b => b.Content as string == "Попробовать снова (на пробу)" && b.IsVisible && b.IsEnabled) is null) throw new Exception("После «Не сейчас» пропало «Попробовать снова»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-dismissed.png"));
                    if (Find<CheckBox>(tabs, c => (c.Content as string ?? "").StartsWith("Сторож:") && c.IsVisible) is not { } wdBox) throw new Exception("В «Проверять сам» нет сторожа");
                    if (Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } wsv2)
                    {
                        wsv2.ScrollToVerticalOffset(wdBox.TranslatePoint(new Point(0, 0), (UIElement)wsv2.Content).Y - 200);
                        Pump();
                        if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-setting.png"));
                        wsv2.ScrollToHome();
                    }
                }
                // Подбор после сторожа (роутер r8): идёт → нашёл и включил / не нашёл. Старый роутер (без watchdog.select) — выше, прежнее предложение.
                {
                    var tabs = Find<TabControl>(w)!;
                    fake.SelectSteps = 100000;
                    fake.WatchdogSelectRunning();
                    WaitUi(router.RefreshAsync());
                    Pump();
                    if (Find<TextBlock>(tabs, t => t.Text == "Сторож выключил оптимизацию — роутер подбирает другую стратегию…" && t.IsVisible) is null) throw new Exception("Нет фразы о подборе после сторожа");
                    if (Find<Button>(tabs, b => (b.Content as string is "Подобрать" or "Не сейчас" or "Попробовать снова (на пробу)") && b.IsVisible) is not null) throw new Exception("Во время подбора после сторожа остались кнопки предложения");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-select-running.png"));
                    WaitUi(router.CancelSelectAsync());
                    fake.WatchdogSelectFound();
                    WaitUi(router.RefreshAsync());
                    Pump();
                    if (Find<TextBlock>(tabs, t => t.Text.StartsWith("Сторож выключил прежнюю стратегию, роутер подобрал и включил ") && t.IsVisible) is null) throw new Exception("Нет фразы «роутер подобрал и включил»");
                    if (Find<Button>(tabs, b => (b.Content as string is "Подобрать" or "Не сейчас" or "Попробовать снова (на пробу)" or "Попробовать прежнюю (на пробу)") && b.IsVisible) is { } extra) throw new Exception("После удачного подбора сторожа остались кнопки: " + extra.Content);
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-select-found.png"));
                    fake.WatchdogTrip();
                    fake.WatchdogSelectFailed();
                    WaitUi(router.RefreshAsync());
                    Pump();
                    if (Find<TextBlock>(tabs, t => t.Text.StartsWith("Сторож выключил оптимизацию, подходящей стратегии не нашлось.") && t.IsVisible) is null) throw new Exception("Нет фразы «не нашлось»");
                    if (Find<Button>(tabs, b => b.Content as string == "Подобрать на роутере" && b.IsVisible && b.IsEnabled) is null) throw new Exception("Нет «Подобрать на роутере» после неудачи");
                    if (Find<Button>(tabs, b => b.Content as string == "Попробовать прежнюю (на пробу)" && b.IsVisible && b.IsEnabled) is null) throw new Exception("Нет «Попробовать прежнюю»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-select-failed.png"));
                    vm.Page = Pages.Home;
                    Pump();
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-watchdog-select-failed-home.png"));
                    vm.Page = Pages.Router;
                    fake.WatchdogSelectJson = null;
                    fake.SelectSteps = 3;
                    fake.StartedBy = "app";
                    fake.WatchdogTrip();
                    WaitUi(router.RefreshAsync());
                    Pump();
                }
                router.VerifyFromPc = () => Task.FromResult<bool?>(false);
                WaitUi(router.ToggleAsync());
                Pump();
                {
                    if (!router.TrialVisible || Find<Button>(w, b => b.Content as string == "Да, оставить" && b.IsVisible && b.IsEnabled) is null)
                        throw new Exception("Нет плашки «Всё работает?» после включения на пробу");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-trial.png"));
                }
                WaitUi(router.RevertTrialAsync());
                fake.KillSwitch = false;
                fake.MethodsJson = null;
                fake.WatchdogOk = true;
                fake.Enabled = true;
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
                if (Find<Button>(d, b => b.Content as string == "Установить" && b.IsVisible && b.IsEnabled) is null) throw new Exception("В начале установки «Установить» неактивна");
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
                if (Find<Button>(d, b => b.Content as string == "Повторить" && b.IsVisible && b.IsEnabled) is null) throw new Exception("После сбоя установки нет «Повторить»");
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
        Assert.Equal(16, doc.Count);
        using var tmp = new TempDir();
        using var fake = new FakeRouter { KillSwitch = true };   // «Сторож» в «Проверять сам» — у роутера со сторожем
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
            vm.ShowAdvancedSetting = true;   // «Отладка подключений» видна только для опытных
            vm.Net.ListsOpen = true;         // IP-списки — в свёрнутом «Дополнительно»
            var w = Open(vm);
            foreach (var (page, _) in AllPages.Append((Pages.Router, "router")))
            {
                vm.Page = page;
                Pump();
                var buttons = new List<InfoButton>();
                Collect(Find<TabControl>(w)!, buttons);
                foreach (var b in buttons.Where(b => b.IsVisible)) seen[b.Topic ?? ""] = b.Text;
            }
            // «Сайты» роутера: «Сайт не открывается» и «Для каких сайтов обход» — с переключателем «На роутере».
            vm.Router.TargetsOnRouter = true;
            vm.Page = Pages.Sites;
            Pump();
            var routerSites = new List<InfoButton>();
            Collect(Find<TabControl>(w)!, routerSites);
            foreach (var b in routerSites.Where(b => b.IsVisible)) seen[b.Topic ?? ""] = b.Text;
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
        // Порядок списка: «по результату» выбран; «по имени» — числа как числа (ALT2 … ALT10), без номера — первым; выбор виден в окне.
        var rByResult = Find<RadioButton>(tabs, r => r.Content as string == "по результату" && r.IsVisible);
        var rByName = Find<RadioButton>(tabs, r => r.Content as string == "по имени" && r.IsVisible);
        if (rByResult is null || rByName is null) throw new Exception("На главной нет переключателя порядка «по результату» / «по имени»");
        if (rByResult.IsChecked != true) throw new Exception("По умолчанию порядок должен быть «по результату»");
        rByName.IsChecked = true;
        Pump();
        if (!vm.SortByName) throw new Exception("«по имени» не дошёл до настроек");
        var named = vm.StrategyChoices.Select(c => c.Title).ToList();
        for (int i = 1; i < named.Count; i++)
            if (StrategyOrder.NaturalCompare(named[i - 1], named[i]) > 0) throw new Exception($"«по имени»: «{named[i - 1]}» стоит раньше «{named[i]}»");
        rByResult.IsChecked = true;
        Pump();
        if (vm.SortByName) throw new Exception("«по результату» не вернулся");
        vm.ShowStrategies = false;

        // Наполнение: подмена DNS и VPN — видна только первая (DNS), заголовок и «Ещё 1» считают одно и то же (UX-аудит 2, п. 6).
        // Обновление списков — не помеха: видно всегда, не под ⚠.
        // Кнопка DNS уже в карточке состояния: плашки DNS и счёта помех нет, остаётся одна плашка VPN без заголовка «Нашлось».
        if (Find<TextBlock>(tabs, t => t.Text.StartsWith("⚠ Нашл") && t.IsVisible) is not null || vm.HasIssuesHeader)
            throw new Exception($"Заголовок «Нашлось…» при одной помехе: «{vm.IssuesTitle}»");
        Button? Visible(string text) => Find<Button>(tabs, b => b.Content as string == text && b.IsVisible);
        if (Visible("Ещё 1") is not null) throw new Exception("«Ещё 1» при одной помехе");
        if (Visible("Включить защищённый DNS") is null) throw new Exception("Нет кнопки DNS в карточке состояния");
        int dnsButtons = 0;
        foreach (var b in FindAll<Button>(tabs, b => b.Content as string == "Включить защищённый DNS" && b.IsVisible)) dnsButtons++;
        if (dnsButtons != 1) throw new Exception($"Кнопок «Включить защищённый DNS» на главной: {dnsButtons}, ждали 1");
        if (!vm.Updates.DataAvailable || Visible("Обновить списки") is null) throw new Exception("Обновление списков спрятано вместе с помехами");
        if (Visible("Понятно, не показывать") is null) throw new Exception("Плашка VPN не видна");
    }

    /// <summary>Строка таблицы подбора: отметка провала видна без раскрытия; стрелка раскрывает строки по всем адресам.</summary>
    static void AssertScoreDetails(Window w, ScoreRow row, string missed)
    {
        var tabs = Find<TabControl>(w)!;
        if (Find<TextBlock>(tabs, t => t.Text == missed && t.IsVisible) is null) throw new Exception($"В строке не видно «{missed}»");
        var grid = Find<DataGrid>(tabs, g => g.IsVisible && g.Items.Contains(row))!;
        grid.ScrollIntoView(row);
        Pump();
        var dgRow = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(row);
        var toggle = Find<ToggleButton>(dgRow, b => b.Style == (Style)w.FindResource("RowToggle"))
            ?? throw new Exception("У строки подбора нет стрелки «Подробнее»");
        toggle.IsChecked = true;
        Pump();
        if (!row.Expanded || dgRow.DetailsVisibility != Visibility.Visible) throw new Exception("Стрелка не раскрыла строку");
        if (Find<TextBlock>(dgRow, t => t.Text == row.Details[0].Url && t.IsVisible) is null) throw new Exception("В раскрытой строке нет адресов");
        ScrollRowToTop(grid, dgRow);
    }

    /// <summary>Для снимка: раскрытая строка — вверху таблицы (прокрутка по пикселям).</summary>
    static void ScrollRowToTop(DataGrid grid, DataGridRow row)
    {
        var sv = Find<ScrollViewer>(grid)!;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + row.TranslatePoint(new Point(0, 0), sv).Y - 30);
        Pump();
    }

    /// <summary>Галочка «Обход работает, даже когда программа закрыта» показывает режим помощника.</summary>
    static void AssertServiceMode(Window w, MainViewModel vm)
    {
        var mode = Find<CheckBox>(Find<TabControl>(w)!, c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "ServiceMode")
            ?? throw new Exception("Нет галочки «Оптимизация работает, даже когда программа закрыта»");
        if (mode.Content as string != "Оптимизация работает, даже когда программа закрыта") throw new Exception($"Галочка режима называется «{mode.Content}»");
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
        var strategies = shell.Catalog.Strategies.Take(6).ToList();
        // Статистика — настоящими файлами подборов: помощник отдаёт её окну (read_results), список строит само окно.
        SeedResults(shell, strategies);
        FillPc(vm, shell);
        vm.StrategyChoices.Insert(0, new("found-1", "Найденная: fake + split2", "YouTube ✓ · Discord ✓ · проверено 17.10 в 09:40", true));
        vm.ChosenStrategyId = strategies[0].Id;

        SetProp(vm, "VpnWarning", "Включён VPN (WireGuard Tunnel). С ним оптимизация обычно не нужна, а подбор может показать неверный результат.");
        SetProp(vm, "DnsSpoofed", true);
        // DNS этого компьютера: свой вариант в списке, включён Google (защищённый), есть что вернуть, VPN не трогаем.
        vm.PcDns.NewName = "AdGuard"; vm.PcDns.NewIps = "94.140.14.14, 94.140.15.15"; vm.PcDns.NewDoh = "https://dns.adguard-dns.com/dns-query";
        vm.PcDns.AddCustom();
        vm.ShowPcDns(new DnsStatusInfo("google", true, false, true, true,
            [new DnsAdapterInfo("Ethernet", "ethernet", false, ["8.8.8.8", "8.8.4.4"], true), new DnsAdapterInfo("Wi-Fi", "wifi", false, ["8.8.8.8", "8.8.4.4"], true)],
            ["Radmin VPN", "vEthernet (Default Switch)"]));
        // «Сравнить» без сети: время ответа рядом с названиями (и у роутера — тот же выбор).
        foreach (var picker in new[] { vm.PcDns, vm.Router.DnsPick })
        {
            picker.Measure = (doh, _) => Task.FromResult<double?>(doh.Contains("quad9") ? null : doh.Contains("google") ? 38 : 52);
            picker.CompareAsync().GetAwaiter().GetResult();
        }
        SetProp(vm, "CheckSummary", "Открывается 1 из 2");
        // «Проверить сейчас» и таблица подбора — те же строки по адресам, что строит окно из проверки (CheckRow.From).
        static TargetVerdict V(string group, string url, bool required, ProbeOutcome o13, string d13, ProbeOutcome o12, string d12, double s)
        {
            var t = new Target(url, Required: required);
            var p = new List<ProbeResult> { new(t, SslProtocols.Tls13, o13, s, 0, d13), new(t, SslProtocols.Tls12, o12, s * 1.3, 0, d12) };
            bool ok = p.All(x => x.Ok);
            return new(group, t, ok, ok ? s * 1.3 : 0, p);
        }
        var yt = V("youtube", "https://www.youtube.com/", true, ProbeOutcome.Ok, "301 → /?gl=RU → 200, 512 КБ", ProbeOutcome.Ok, "301 → /?gl=RU → 200, 512 КБ", 0.4);
        var img = V("youtube", "https://i.ytimg.com/vi/dQw4w9WgXcQ/maxresdefault.jpg", true, ProbeOutcome.Ok, "HTTP 200, 118 КБ", ProbeOutcome.Ok, "HTTP 200, 118 КБ", 0.3);
        var ggpht = V("youtube", "https://yt3.ggpht.com/", false, ProbeOutcome.Ok, "HTTP 404", ProbeOutcome.Timeout, "таймаут", 0.2);
        var dc = V("discord", "https://discord.com/", true, ProbeOutcome.Ok, "HTTP 200, 160 КБ", ProbeOutcome.Ok, "HTTP 200, 160 КБ", 0.5);
        var gw = V("discord", "https://gateway.discord.gg/", true, ProbeOutcome.Reset, "соединение сброшено удалённой стороной", ProbeOutcome.Reset, "соединение сброшено удалённой стороной", 0.1);
        var vk = V("control", "https://vk.com/", true, ProbeOutcome.Ok, "HTTP 200, 96 КБ", ProbeOutcome.Ok, "HTTP 200, 96 КБ", 0.2);
        var rbc = V("control", "https://www.rbc.ru/", true, ProbeOutcome.Truncated, "200, получено 16384 из 402113", ProbeOutcome.Truncated, "200, получено 16384 из 402113", 6);
        // Программа открывает по DoH, браузер по адресу DNS провайдера — нет: ⚠ и кнопка защищённого DNS.
        var stub = new ProbeResult(yt.Target, SslProtocols.Tls13, ProbeOutcome.ForeignRedirect, 0.1, 0, "302: редирект на warning.rt.ru");
        var ytBrowser = yt with { Browser = new BrowserPath([System.Net.IPAddress.Parse("195.208.4.1")], [System.Net.IPAddress.Parse("142.250.74.14")], false, null,
            [stub, stub with { Tls = SslProtocols.Tls12 }]) };
        foreach (var v in new[] { ytBrowser, img, ggpht, dc, gw }) vm.CheckRows.Add(CheckRow.From(v, vm.Names));

        var rows = new List<ScoreRow>
        {
            new(new StrategyScore(strategies[0], [yt, img, ggpht, dc, gw with { Ok = true, BestSeconds = 0.4, Probes = dc.Probes }, vk], 2, 2), vm.Names) { IsBest = true },
            new(new StrategyScore(new Strategy("found-1", "Найденная: fake + split2", "search", []), [yt, img, dc, gw, vk], 1, 2), vm.Names),
            new(new StrategyScore(strategies[1], [yt, img, ggpht, dc, gw, vk], 1, 2), vm.Names),
            new(new StrategyScore(strategies[2], [yt, img, dc, gw, vk, rbc], 0, 2, "ломает обычные сайты: www.rbc.ru"), vm.Names),
            new(new StrategyScore(strategies[3], [], 0, 2, "winws завершился: WinDivert: не удалось открыть драйвер (код 5)")),
        };
        foreach (var r in rows) vm.Scores.Add(r);
        vm.SelectedScore = rows[2];
        SetProp(vm, "SelectProgressMax", 10);
        SetProp(vm, "SelectProgress", 6);
        SetProp(vm, "SelectProgressText", "Проверено 6 из 10");
        SetProp(vm, "ResultText", "Подходит: " + strategies[0].Title);
        SetProp(vm, "ResultHint", "без оптимизации не открывалось: YouTube, Discord.");
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

    /// <summary>
    /// Три подбора в results: у второй стратегии свои сайты открываются не все и однажды сломались обычные сайты —
    /// в списке подпись по группам, предупреждение и адреса последнего подбора.
    /// </summary>
    static void SeedResults(Shell shell, IReadOnlyList<Strategy> strategies)
    {
        var groups = new List<TargetGroup>(shell.DefaultTargets.Groups.Where(g => g.Id is "youtube" or "discord" or "control"))
            { TargetSet.UserGroup(Enumerable.Range(1, 9).Select(i => $"site{i}.example")) };
        StrategyScore Run(Strategy st, double seconds, params string[] failed)
        {
            var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
            {
                bool ok = !failed.Contains(t.Url);
                var ps = new[] { SslProtocols.Tls13, SslProtocols.Tls12 }.Select(v => new ProbeResult(t, v, ok ? ProbeOutcome.Ok : ProbeOutcome.Truncated, seconds, 0,
                    ok ? (t.HandshakeOnly ? "HTTP 404" : t.Url.Contains("youtube") ? "301 → /?gl=RU → 200, 512 КБ" : "HTTP 200, 96 КБ") : "200, получено 16384 из 90000")).ToList();
                return new TargetVerdict(g.Id, t, ok, ok ? seconds : 0, ps);
            })).ToList();
            return DpiShell.Core.Selector.Score(st, groups, verdicts);
        }
        for (int run = 0; run < 3; run++)
        {
            var file = ResultStore.Save(shell.Paths, new SelectionReport([], groups,
            [
                Run(strategies[0], 0.4),
                Run(strategies[1], 0.5, run == 0 ? ["https://discord.com/"] : run == 1 ? ["https://site1.example/"] : ["https://www.rbc.ru/", "https://site2.example/", "https://site5.example/"]),
                Run(strategies[2], 0.6, "https://www.youtube.com/"),
            ], false));
            File.Move(file, Path.Combine(shell.Paths.Results, $"2026-10-1{5 + run}_10-00-00.json"), overwrite: true);
        }
    }

    static void PumpUntil(Func<bool> cond, string what)
    {
        for (int i = 0; i < 400 && !cond(); i++) { Pump(); Thread.Sleep(15); }
        if (!cond()) throw new Exception("Не дождались: " + what);
    }

    /// <summary>
    /// «Игры и программы» (ТЗ 13) в обеих темах, настоящий помощник над временной папкой и подменой источника записи:
    /// светлая — простой режим от «Начать» до вопроса «Заработало?»; тёмная — «Отладка подключений» (включена в «Настройках»):
    /// запись, список с галочками, сохранённый профиль. «Дополнительно» (IP-списки) свёрнуто. Снимки — DPISHELL_SNAPSHOTS.
    /// </summary>
    [Fact]
    public void AppsPage_SimpleAndAdvanced_Work_InBothThemes()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snapDir is not null) Directory.CreateDirectory(snapDir);
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                bool dark = theme == ThemeManager.Dark;
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                shell.Data.Seed();
                var flows = new FakeFlowSource();
                var procs = new FakeProcesses(new ProcInfo(4, 0, "System"), new ProcInfo(900, 4, "chrome.exe"));
                var f = new HelperFixture(backend: PcHelper.Over(shell, flows, procs));
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var settings = TestWindow.Settings(shell);
                settings.ShowAdvanced = dark;
                var vm = TestWindow.Model(shell, link, settings);
                vm.Apps.ListProcesses = () => [new(900, "chrome.exe", "Новая вкладка"), new(2000, "game.exe", "Моя игра")];
                vm.ApplyHelperData();
                var w = Open(vm);
                SetProp(vm, "IsLoading", false);
                vm.Page = Pages.Apps;
                PumpUntil(() => vm.Apps.Variants.Count > 0, "варианты параметров от помощника");
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => Find<TextBlock>(tabs, t => t.IsVisible && m(t)) is not null;
                Button? Btn(string content) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == content);

                // Раскладка: сверху «Игры и программы» и «Настроить под игру», ниже игровой режим, IP-списки — в свёрнутом «Дополнительно».
                if (!Visible(t => t.Text == "Игры и программы") || !Visible(t => t.Text == "Настроить под игру")) throw new Exception("Нет заголовков вкладки");
                if (!Visible(t => t.Text == "Игровой режим")) throw new Exception("Нет игрового режима");
                var more = Find<Expander>(tabs, e => e.Header as string == "Дополнительно: IP-списки и свои подсети")
                    ?? throw new Exception("Нет «Дополнительно»");
                if (more.IsExpanded) throw new Exception("«Дополнительно» должно быть свёрнуто");
                if (Visible(t => t.Text == "Свои подсети")) throw new Exception("Свои подсети видны в свёрнутом блоке");
                if (!Visible(t => t.Text.Contains("Vanguard"))) throw new Exception("Предупреждение про античиты не видно");
                if (Btn("Начать") is null) throw new Exception("Нет кнопки «Начать»");
                // «Отладка подключений» — только для опытных.
                bool advVisible = Visible(t => t.Text == "Отладка подключений");
                if (advVisible != dark) throw new Exception($"«Отладка подключений» видна = {advVisible}, ждали {dark}");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-idle.png"));

                if (!dark)
                {
                    // Простой режим: Начать → (игра запускается) → Готово → применено → «Заработало?».
                    Btn("Начать")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.IsRecording, "запись");
                    procs.List.Add(new ProcInfo(2000, 4, "game.exe"));
                    flows.Push("udp", "203.0.113.50", 27015, 2000);
                    flows.Push("udp", "203.0.113.51", 27016, 2000);
                    flows.Push("tcp", "198.51.100.7", 443, 2000, FlowHint.Established);
                    flows.Push("tcp", "198.51.100.60", 443, 900);   // браузер — шум
                    Task.Run(() => vm.Apps.TickAsync()).GetAwaiter().GetResult();
                    Pump();
                    if (!Visible(t => t.Text.StartsWith("Идёт запись"))) throw new Exception("Нет строки «Идёт запись»");
                    if (Btn("Готово") is null || Btn("Отмена") is null) throw new Exception("Нет «Готово» / «Отмена»");
                    if (Btn("Начать") is not null) throw new Exception("«Начать» не спрятана во время записи");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-recording.png"));
                    Btn("Готово")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.IsCheck, "применение и вопрос");
                    Pump();
                    if (!Visible(t => t.Text.StartsWith("Применено:") && t.Text.Contains("Заработало?"))) throw new Exception("Нет вопроса «Заработало?»");
                    foreach (var b in new[] { "Да, заработало", "Нет", "Стало хуже" })
                        if (Btn(b) is null) throw new Exception("Нет кнопки «" + b + "»");
                    if (!Visible(t => t.Text == "Профили программ")) throw new Exception("Профиль не в списке");
                    if (Btn("Вернуть как было") is null) throw new Exception("Нет «Вернуть как было»");
                    if (!Visible(t => t.Text.StartsWith("Оптимизация сейчас выключена"))) throw new Exception("Нет подсказки, что оптимизация выключена");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-simple-check.png"));
                    Btn("Да, заработало")!.Command.Execute(null);
                    Pump();
                    if (!Visible(t => t.Text.StartsWith("Готово: профиль включён"))) throw new Exception("Нет итога");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-simple-done.png"));
                }
                else
                {
                    // Продвинутый режим: запись → список с галочками → сохранение.
                    vm.Apps.RefreshProcesses();
                    Btn("Записать")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.AdvRecording, "запись");
                    procs.List.Add(new ProcInfo(2000, 4, "game.exe"));
                    flows.Push("udp", "203.0.113.50", 27015, 2000);
                    flows.Push("udp", "203.0.113.51", 27016, 2000);
                    flows.Push("tcp", "198.51.100.7", 443, 2000, FlowHint.Established);
                    flows.Push("udp", "192.168.1.1", 53, 2000);
                    flows.Push("tcp", "198.51.100.60", 443, 900);
                    Btn("Стоп")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.HasRows, "список подключений");
                    Pump();
                    var grid = Find<DataGrid>(tabs, g => g.IsVisible && g.Items.Count > 0 && g.Items[0] is FlowRow) ?? throw new Exception("Нет таблицы подключений");
                    if (grid.Items.Count != 5) throw new Exception("В таблице " + grid.Items.Count + " строк, ждали 5");
                    var boxes = FindAll<CheckBox>(grid, c => c.IsVisible && c.DataContext is FlowRow).ToList();
                    if (boxes.Count != 5 || boxes.Count(c => c.IsChecked == true) != 3) throw new Exception("Галочки по умолчанию: " + boxes.Count(c => c.IsChecked == true) + " из " + boxes.Count);
                    if (!Visible(t => t.Text.StartsWith("TCP 443 · UDP 27015-27016"))) throw new Exception("Нет предпросмотра профиля");
                    foreach (var b in new[] { "Отсечь шум", "Отметить все", "Снять все", "Сохранить профиль" })
                        if (Btn(b) is null) throw new Exception("Нет кнопки «" + b + "»");
                    if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-advanced.png"));
                    boxes.First(c => ((FlowRow)c.DataContext).Address == "203.0.113.51").IsChecked = false;   // человек снял одну
                    Pump();
                    if (!Visible(t => t.Text.StartsWith("TCP 443 · UDP 27015 ·"))) throw new Exception("Предпросмотр не пересчитался");
                    Btn("Сохранить профиль")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.Profiles.Count == 1, "профиль сохранён");
                    Pump();
                    if (!Visible(t => t.Text == "Профили программ")) throw new Exception("Нет списка профилей");
                    if (Btn("Другие параметры") is null) throw new Exception("Для опытных нет «Другие параметры»");
                    if (snapDir is not null)
                    {
                        Snap(w, Path.Combine(snapDir, $"{theme}-apps-advanced-saved.png"));
                        if (Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } sv)
                        {
                            sv.ScrollToEnd();
                            Pump();
                            Snap(w, Path.Combine(snapDir, $"{theme}-apps-advanced-end.png"));
                            sv.ScrollToHome();
                        }
                    }
                }
                // «Дополнительно» раскрывается и показывает IP-списки.
                more.IsExpanded = true;
                Pump();
                if (!Visible(t => t.Text == "Свои подсети")) throw new Exception("После раскрытия нет «Свои подсети»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-apps-more.png"));
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
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
        // Свои строки пользователя (одна с устаревшим адресом) и строка, закомментированная нами, — разделы «Мои строки» и «Проверить мой hosts».
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n0.0.0.0 web.telegram.org\r\n# DpiShell begin v1 2026-10-16\r\n140.82.121.3 github.com\r\n# DpiShell end\r\n" +
            "# DpiShell user begin 2026-10-17\r\n# Managed by DpiShell (own lines). Every line below is tagged dpishell-user; the app can remove them.\r\n" +
            "151.101.1.140 example.org  # dpishell-user 2026-10-17 manual\r\n104.16.0.1 updates.discord.com  # dpishell-user 2026-10-17 manual\r\n# DpiShell user end\r\n" +
            "# DpiShell off 2026-10-18: 1.2.3.4 old.example.com\r\n");
        Directory.CreateDirectory(Path.Combine(shell.Paths.Root, "hosts"));
        File.WriteAllText(Path.Combine(shell.Paths.Root, "hosts", "user-stale.json"), """[{"host":"updates.discord.com","ip":"104.16.0.1"}]""");
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
            // Примерка (все шесть вердиктов) и проверка своего hosts: разделы раскрыты, чтобы они попали на снимок.
            var okLive = new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200");
            vm.Hosts.ShowTry(new HostsTryReport(
            [
                new("github.com", "140.82.121.3", HostsTryVerdict.Helps, okLive, "GITHUB", DnsMatch.SameNet, DnsState.Fails, "140.82.112.3: нет ответа"),
                new("www.github.com", "140.82.121.4", HostsTryVerdict.Helps, okLive, "GITHUB", DnsMatch.SameNet, DnsState.Fails, "140.82.112.3: нет ответа"),
                new("discord.com", "162.159.138.232", HostsTryVerdict.NotNeeded, okLive, "CLOUDFLARENET", DnsMatch.Same, DnsState.Works, "162.159.138.232: HTTP 200"),
                new("gist.github.com", "0.0.0.0", HostsTryVerdict.Breaks, null, null, DnsMatch.Differs, DnsState.Works, "140.82.112.4: HTTP 200"),
                new("web.telegram.org", "149.154.167.220", HostsTryVerdict.Dangerous, new(EndpointOutcome.BadCertificate, 0.3, "чужой сертификат"), "EXAMPLE-NET", DnsMatch.Differs, DnsState.Fails, ""),
                new("updates.discord.com", "104.16.0.1", HostsTryVerdict.Useless, new(EndpointOutcome.Timeout, 5, "не отвечает"), null, DnsMatch.Unknown, DnsState.Unknown, "DNS не ответил"),
                new("ads.example.com", "0.0.0.0", HostsTryVerdict.Blocks, null, null, DnsMatch.Unknown, DnsState.Fails, "2.2.2.2: нет ответа"),
            ], 1, false, 7, DateTime.Now), new HostsPasteResult([], 1, false));
            vm.Hosts.PasteText = "140.82.121.3 github.com www.github.com\r\n140.82.121.4 raw.githubusercontent.com\r\n0.0.0.0 gist.github.com";
            vm.Hosts.ShowAudit(new HostsTryReport(
            [
                new("github.com", "140.82.112.4", HostsTryVerdict.Breaks, new(EndpointOutcome.Timeout, 5, "не отвечает"), null, DnsMatch.Differs, DnsState.Works, "140.82.121.3: HTTP 200", 3, "3:0123456789ab"),
                new("web.telegram.org", "0.0.0.0", HostsTryVerdict.Breaks, null, null, DnsMatch.Differs, DnsState.Works, "149.154.167.220: HTTP 200", 2, "2:abcdefabcdef"),
                new("phish.example.com", "9.9.9.9", HostsTryVerdict.Dangerous, new(EndpointOutcome.BadCertificate, 0.3, "чужой сертификат"), "EXAMPLE-NET", DnsMatch.Unknown, DnsState.Unknown, "DNS не ответил", 9, "9:fedcbafedcba"),
                new("ads.example.com", "0.0.0.0", HostsTryVerdict.Blocks, null, null, DnsMatch.Unknown, DnsState.Fails, "2.2.2.2: нет ответа", 12, "12:111111111111"),
            ], 0, false, 4, DateTime.Now));
            vm.Hosts.AuditRows[0].Selected = true;
        }
        vm.Hosts.UserOpen = true;
    }

    /// <summary>
    /// «Информация и обновления» → обновление программы: есть обновление с «Что нового», идёт обновление, ошибка (подменённый архив),
    /// после обновления (итог, «Вернуть прежнюю версию», версии пакетов и ядро роутера). Без ошибок привязок в обеих темах.
    /// Снимки: &lt;тема&gt;-about-update-ready|running|download|error|done.png.
    /// </summary>
    [Fact]
    public void About_AppUpdate_States_BindAndSnap()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                int n = 0;
                MainViewModel? last = null;

                (MainViewModel Vm, MainWindow W, AppVersionStore Store) NewWindow(MapHandler h, string current, Func<RestartPlan, Task> restart)
                {
                    var shell = new Shell(new AppPaths(tmp.Sub($"upd-{theme}-{++n}"), Repo.Data));
                    var settings = TestWindow.Settings(shell);
                    settings.Theme = theme;
                    var store = new AppVersionStore(Path.Combine(Path.GetDirectoryName(settings.File)!, "versions"));
                    var vm = last = TestWindow.Model(shell, settings: settings);
                    vm.Updates.Http = () => new HttpClient(h, disposeHandler: false);
                    vm.Updates.AppVersion = current + ".0";
                    vm.Updates.Confirm = _ => true;
                    vm.Updates.CreateUpdater = http => new AppUpdater(
                        new AppUpdateClient(http, [AppUpdateTests.A]) { PerSourceTimeout = TimeSpan.FromSeconds(5), SigningKey = AppUpdateTests.Key.PublicKey, OsVersion = new Version(10, 0, 26200) }, store)
                    { CurrentVersion = current, CurrentExe = Path.Combine(tmp.Sub("portable"), "DpiShell.exe"), CurrentPid = 4242, FreeSpace = _ => 5L << 30 };
                    var w = Open(vm);
                    // Окно подключает свой перезапуск (запуск другой копии и выход) — в тесте он подменён: настоящую копию не запускаем.
                    vm.Updates.Restart = restart;
                    SetProp(vm, "IsLoading", false);
                    vm.Page = Pages.About;
                    Pump();
                    return (vm, w, store);
                }
                void Until(Func<bool> done, string what)
                {
                    for (int i = 0; i < 500 && !done(); i++) { Pump(); Thread.Sleep(10); }
                    if (!done()) throw new Exception("Не дождались: " + what + " | " + last?.Updates.AppProgressText + " | " + last?.Updates.AppError + " | " + last?.Updates.AppErrorDetails);
                    Pump();
                }
                bool Has(MainWindow w, string text) =>
                    Find<TextBlock>(w, t => t.Text == text && t.IsVisible) is not null || Find<Button>(w, b => b.Content as string == text && b.IsVisible) is not null;
                bool HasPart(MainWindow w, string part) => Find<TextBlock>(w, t => t.Text.Contains(part) && t.IsVisible) is not null;
                void Shot(MainWindow w, string name)
                {
                    if (snapDir is null) return;
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-about-update-{name}.png"));
                }

                // 1. Есть обновление: «Что нового» и «Обновить до X» видны до нажатия, отката ещё нет.
                var (vm, w, _) = NewWindow(AppUpdateTests.Serve(new MapHandler(), AppUpdateTests.A, "1.2.0"), "1.0.0", _ => new TaskCompletionSource().Task);
                vm.Updates.CheckCmd.Execute(null);
                Until(() => vm.Updates.AppReleaseReady, "проверка выпуска");
                foreach (var t in new[] { "Что нового", "• Быстрее подбор", "• Исправлен трей", "Обновить до 1.2.0", "Проверить обновления" })
                    if (!Has(w, t)) throw new Exception($"Нет «{t}» при доступном обновлении");
                if (Has(w, "Скачать программу 1.2.0") || HasPart(w, "Вернуть прежнюю")) throw new Exception("Лишние кнопки при доступном обновлении");
                Shot(w, "ready");

                // 2. Идёт обновление: ход виден, кнопка неактивна. Перезапуск не завершается — как окно за миг до выхода.
                vm.Updates.UpdateAppCmd.Execute(null);
                Until(() => vm.Updates.AppProgressText.Contains("Перезапускаю"), "подготовка версии");
                if (!HasPart(w, "Перезапускаю программу")) throw new Exception("Ход обновления не виден");
                if (Find<ProgressBar>(w, p => p.IsVisible && p.IsIndeterminate) is null) throw new Exception("Нет полосы хода обновления");
                if (Find<Button>(w, b => b.Content as string == "Обновить до 1.2.0" && b.IsVisible && b.IsEnabled) is not null) throw new Exception("Кнопка обновления активна во время обновления");
                Shot(w, "running");
                typeof(UpdatesViewModel).GetField("_appProgress", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(vm.Updates, "Скачиваю версию 1.2.0: 34 МБ");
                vm.Updates.Refresh();
                Pump();
                if (!HasPart(w, "Скачиваю версию 1.2.0: 34 МБ")) throw new Exception("Ход скачивания не виден");
                Shot(w, "download");
                Close(w);

                // 3. Ошибка: архив на сервере подменён — понятная фраза и «Подробнее» мелко, повторить можно.
                var evil = AppUpdateTests.Serve(new MapHandler(), AppUpdateTests.A, "1.2.0").Ok(AppUpdateTests.A + AppRelease.PortableName("1.2.0"), AppUpdateTests.GoodZip("evil"));
                var (vm2, w2, store2) = NewWindow(evil, "1.0.0", _ => Task.CompletedTask);
                vm2.Updates.CheckCmd.Execute(null);
                Until(() => vm2.Updates.AppReleaseReady, "проверка выпуска (ошибка)");
                vm2.Updates.UpdateAppCmd.Execute(null);
                Until(() => vm2.Updates.HasAppError, "ошибка обновления");
                if (!HasPart(w2, "Скачанный файл не прошёл проверку") || !HasPart(w2, "программа не изменена")) throw new Exception("Нет фразы об ошибке");
                if (Find<Button>(w2, b => b.Content as string == "Обновить до 1.2.0" && b.IsVisible && b.IsEnabled) is null) throw new Exception("После ошибки нельзя повторить");
                if (store2.IsInstalled("1.2.0")) throw new Exception("Подменённый архив распакован");
                Shot(w2, "error");
                Close(w2);

                // 4. После обновления: итог, «Вернуть прежнюю версию»; роутер с пакетами r8 — версии пакета, страницы и ядра.
                using var fake = new FakeRouter().As020().AsR8(dpishell: "0.2.0-r8", luci: "0.2.0-r5", core: "v72.13");
                var (vm3, w3, store3) = NewWindow(new MapHandler(), "1.2.0", _ => Task.CompletedTask);
                var prev = Path.Combine(tmp.Sub("portable-old"), "DpiShell.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(prev)!);
                File.WriteAllText(prev, "exe 1.0.0");
                store3.Save(new("1.2.0", "1.0.0", prev));
                vm3.Updates.AnnounceStart("1.0.0", null);
                vm3.Router.AddressAuto = false;
                vm3.Router.CustomAddress = fake.Address;
                vm3.Router.PollInterval = TimeSpan.FromMilliseconds(10);
                vm3.Router.Gateway = () => "192.168.1.1";
                vm3.Router.PcTimestamps = () => true;
                var connect = vm3.Router.ConnectAsync("secret");
                Until(() => connect.IsCompleted, "вход на роутер");
                if (!connect.GetAwaiter().GetResult()) throw new Exception("Роутер не принял вход");
                foreach (var t in new[] { "Программа обновлена до версии 1.2.0.", "Вернуть прежнюю версию (1.0.0)", "0.2.0-r8 · страница LuCI 0.2.0-r5", "Ядро на роутере", "v72.13" })
                    if (!Has(w3, t)) throw new Exception($"Нет «{t}» после обновления");
                Shot(w3, "done");
                Close(w3);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
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
