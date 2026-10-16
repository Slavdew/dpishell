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

namespace DpiShell.Tests;

/// <summary>
/// Окно открывается в памяти (за пределами экрана), все вкладки строятся, ошибок привязок нет.
/// Без прав администратора и сети: ядро не ставится (InitializeAsync не вызывается).
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

    static readonly (int Page, string Name)[] AllPages =
        [(Pages.Home, "home"), (Pages.Select, "select"), (Pages.Targets, "targets"), (Pages.Net, "net"), (Pages.Hosts, "hosts"), (Pages.Diag, "diag")];

    [Fact]
    public void MainWindow_OpensAndAllPagesBind_WithoutErrors()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = new MainViewModel(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            var w = Open(vm);
            var tabs = Find<TabControl>(w)!;
            foreach (var page in new[] { Pages.Select, Pages.Targets, Pages.Net, Pages.Hosts, Pages.Diag, Pages.Home })
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
                shell.State.Theme = theme;
                // Тёмная — в режиме без службы: на снимках видны обе подсказки у галочки и у настройки закрытия.
                if (theme == ThemeManager.Dark) shell.State.RunMode = "process";
                var vm = new MainViewModel(shell);
                Fill(vm, shell);
                var w = Open(vm);
                AssertPage(w, theme == ThemeManager.Dark);

                if (snapDir is not null)
                {
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-loading.png"));
                }
                foreach (var (page, name) in AllPages)
                {
                    // Пока «загрузка» — смена вкладки не запускает проверку службы (sc.exe); потом снять заставку для снимка.
                    SetProp(vm, "IsLoading", true);
                    vm.Page = page;
                    Pump();
                    if (page == Pages.Home) AssertHome(w, vm);
                    if (snapDir is null) continue;
                    SetProp(vm, "IsLoading", false);
                    typeof(MainViewModel).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                    Pump();
                    Snap(w, Path.Combine(snapDir, $"{theme}-{name}.png"));
                    // Главная: правая колонка (плашки и «Проверить сейчас») прокручена до конца.
                    if (page == Pages.Home && Find<ScrollViewer>(Find<TabControl>(w)!, s => s.TemplatedParent is not ListBox && s.ScrollableHeight > 0) is { } hv)
                    {
                        hv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-home-end.png"));
                        hv.ScrollToHome();
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
                if (AppState.Load(shell.Paths).Theme != ThemeManager.Auto) throw new Exception("Выбор темы не сохранился в состоянии");
                Close(w);
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
            SelectRowsJson = """
                [ { "id": "alt", "group_share": 1.0, "target_share": 0.88, "avg_s": 0.74, "error": null },
                  { "id": "alt9", "group_share": 1.0, "target_share": 0.80, "avg_s": 0.61, "error": null },
                  { "id": "alt10", "group_share": 0.5, "target_share": 0.5, "avg_s": 0.92, "error": null },
                  { "id": "d-17", "title": "fake + multisplit, ts — YouTube", "group_share": 0.5, "target_share": 0.62, "avg_s": 0.83, "error": null },
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
                shell.State.Theme = theme;
                shell.State.UserTargets = ["example.com", "my-site.ru"];
                var vm = new MainViewModel(shell);
                var router = vm.Router;
                router.AddressAuto = false;
                router.CustomAddress = fake.Address;
                router.PollInterval = TimeSpan.FromMilliseconds(10);
                router.Gateway = () => null;
                // Сеть — в фоне без контекста окна (поток окна здесь занят), потом окно строится по готовому состоянию.
                Task.Run(async () =>
                {
                    if (!await router.ConnectAsync("secret")) throw new Exception("Не подключились: " + router.ConnectionText);
                    await router.StartSelectAsync(deep: false);
                    await router.LoadTargetsAsync();
                    await router.RefreshDiagAsync();
                }).GetAwaiter().GetResult();
                router.TargetsOnRouter = true;
                router.SelectedScore = router.Scores.First(x => x.Id == "alt9");
                Fill(vm, shell);

                var w = Open(vm);
                foreach (var (page, name) in AllPages)
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
                        if (Find<TextBlock>(tabs, t => t.Text == "Обход делает роутер" && t.IsVisible) is null) throw new Exception("На главной нет блока роутера");
                        if (Find<Button>(tabs, b => b.Content as string == "Отправить на роутер" && b.IsVisible) is null) throw new Exception("У найденной стратегии нет «Отправить на роутер»");
                        if (Find<ComboBox>(tabs) is not null) throw new Exception("На главной появился выпадающий список");
                    }
                    // Роутер сам включил лучшую (редакция §9) — итог «Включена на роутере», другую можно выбрать в таблице.
                    if (page == Pages.Select && (Find<TextBlock>(tabs, t => t.Text.StartsWith("Включена на роутере") && t.IsVisible) is null
                                                 || Find<Button>(tabs, b => b.Content as string == "Применить выбранную" && b.IsVisible && b.IsEnabled) is null))
                        throw new Exception("На «Подборе» нет итога роутера или «Применить выбранную»");
                    if (page == Pages.Diag && Find<Button>(tabs, b => b.Content as string == "Остановить" && b.IsVisible) is null)
                        throw new Exception("В диагностике роутера нет «Остановить» у конфликта");
                    if (snapDir is null) continue;
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"{theme}-router-{name}.png"));
                    if (page == Pages.Diag && Find<ScrollViewer>(tabs, s => s.ScrollableHeight > 0) is { } sv)
                    {
                        // Карточки роутера — в середине и в конце страницы.
                        var card = Find<TextBlock>(tabs, t => t.Text == "Роутер")!;
                        sv.ScrollToVerticalOffset(card.TranslatePoint(new Point(0, 0), (UIElement)sv.Content).Y - 30);
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-card.png"));
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"{theme}-router-diag-end.png"));
                        sv.ScrollToHome();
                    }
                }
                SetProp(vm, "IsLoading", true);
                Close(w);
                router.Dispose();
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    /// <summary>Разделы docs/10-info-texts.md: заголовок «## …» → текст абзаца (обратные кавычки разметки убраны).</summary>
    static Dictionary<string, string> DocInfoTexts()
    {
        var root = Directory.GetParent(Repo.Data)!.FullName;
        var map = new Dictionary<string, string>();
        string? head = null;
        var body = new List<string>();
        void Flush()
        {
            if (head is not null) map[head] = string.Join(" ", body).Replace("`", "").Trim();
            body.Clear();
        }
        foreach (var line in File.ReadAllLines(Path.Combine(root, "docs", "10-info-texts.md")))
        {
            if (line.StartsWith("## ")) { Flush(); head = line[3..].Trim(); continue; }
            if (head is not null && line.Trim().Length > 0) body.Add(line.Trim());
        }
        Flush();
        return map;
    }

    /// <summary>У каждого раздела из 10-info-texts.md в окне есть «(i)», и её текст — дословно из документа.</summary>
    [Fact]
    public void InfoButtons_EveryDocSection_HasButton_WithDocText()
    {
        var doc = DocInfoTexts();
        Assert.Equal(9, doc.Count);
        using var tmp = new TempDir();
        var seen = new Dictionary<string, string>();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = new MainViewModel(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            var w = Open(vm);
            foreach (var (page, _) in AllPages)
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

    /// <summary>Главная: выпадающего списка нет, стратегии — списком с выбранной, галочка режима показывает режим ядра.</summary>
    static void AssertHome(Window w, MainViewModel vm)
    {
        var tabs = Find<TabControl>(w)!;
        if (Find<ComboBox>(tabs) is not null) throw new Exception("На главной остался выпадающий список");
        var list = Find<ListBox>(tabs) ?? throw new Exception("На главной нет списка стратегий");
        if (list.Items.Count != vm.StrategyChoices.Count || list.Items.Count == 0) throw new Exception($"В списке {list.Items.Count} строк, ждали {vm.StrategyChoices.Count}");
        if ((list.SelectedItem as StrategyChoice)?.Id != vm.ChosenStrategyId) throw new Exception("В списке не выделена выбранная стратегия");
        var mode = Find<CheckBox>(tabs, c => BindingOperations.GetBinding(c, ToggleButton.IsCheckedProperty)?.Path.Path == "ServiceMode")
            ?? throw new Exception("Нет галочки «Режим службы»");
        if (mode.IsChecked != vm.Shell.ServiceMode) throw new Exception("Галочка режима не совпадает с режимом");
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
        vm.StrategyChoices.Add(new("found-1", "Найденная: fake + split2", "открыла всё в 2 из 2 подборов (100 %)", true));
        foreach (var (s, i) in strategies.Select((s, i) => (s, i)))
            vm.StrategyChoices.Add(new(s.Id, s.Title, i < 4 ? $"открыла всё в {4 - i} из 4 подборов ({(4 - i) * 25} %)" : "в подборах ещё не участвовала", false));
        vm.ChosenStrategyId = strategies[0].Id;

        SetProp(vm, "VpnWarning", "Включён VPN (WireGuard Tunnel). С ним обход обычно не нужен, а подбор может показать неверный результат.");
        SetProp(vm, "DnsSpoofed", true);
        SetProp(vm, "CheckSummary", "Открывается 3 из 4");
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

        // «Игры и IP-списки»: список из поставки — во временную папку. Светлая — «Все адреса» с отсчётом (игры ждут),
        // тёмная — «По списку», игры работают.
        bool dark = ThemeManager.IsDark;
        if (!shell.IpLists.SeedFromPackage()) throw new Exception("IP-список из поставки не встал");
        shell.State.IpsetMode = dark ? "list" : "any";
        shell.State.IpsetModeBeforeAny = "list";
        shell.State.IpsetAnyUntil = dark ? null : DateTime.UtcNow.AddMinutes(7.5);
        shell.State.GameMode = "all";
        shell.State.Enabled = dark;
        vm.Net.UserText = "203.0.113.0/24\r\n198.51.100.7";
        SetProp(vm.Net, "UserMessage", "Сохранено: своих подсетей 2, исключений 0.");
        vm.Net.Refresh();

        // «Починить адреса» — только копия hosts во временной папке. Светлая: включено, таблица и предпросмотр;
        // тёмная: выключено, а блок от прошлой установки остался.
        var hostsFile = Path.Combine(shell.Paths.Root, "hosts-copy");
        Directory.CreateDirectory(shell.Paths.Root);
        File.WriteAllText(hostsFile, "127.0.0.1 localhost\r\n0.0.0.0 web.telegram.org\r\n# DpiShell begin v1 2026-10-16\r\n140.82.121.3 github.com\r\n# DpiShell end\r\n");
        vm.Hosts.Manager = new HostsManager(shell.Paths, hostsFile);
        shell.State.HostsFix = !dark;
        vm.Hosts.RefreshNow();
        if (!dark)
        {
            var live = new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200");
            vm.Hosts.ShowReport(new HostsCheckReport(
            [
                new("raw.githubusercontent.com", "146.75.22.132", HostsVerdict.Install, live, OwnerState.Match, "FASTLY", DnsState.Fails, "185.199.108.133: нет ответа"),
                new("github.com", "140.82.121.3", HostsVerdict.Install, live, OwnerState.Match, "GITHUB", DnsState.Fails, "140.82.112.3: нет ответа"),
                new("discord.com", "162.159.138.232", HostsVerdict.NotNeeded, live, OwnerState.Match, "CLOUDFLARENET", DnsState.Works, "162.159.136.232: HTTP 200"),
                new("web.telegram.org", "149.154.167.220", HostsVerdict.BadCertificate, new(EndpointOutcome.BadCertificate, 0.3, "сертификат выдан другому сайту"),
                    OwnerState.Unknown, null, DnsState.Fails, ""),
                new("updates.discord.com", "104.16.0.1", HostsVerdict.Unreachable, new(EndpointOutcome.Timeout, 5, "нет ответа за 5 с"),
                    OwnerState.Unknown, null, DnsState.Works, ""),
            ], DateTime.Now));
        }
        vm.Message = "Готово.";
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
