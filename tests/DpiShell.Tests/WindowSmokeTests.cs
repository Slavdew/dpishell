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
        foreach (var name in new[] { "_timer", "_watchTimer", "_netSettle" })
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
        [(Pages.Home, "home"), (Pages.Select, "select"), (Pages.Targets, "targets"), (Pages.Diag, "diag")];

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
            foreach (var page in new[] { Pages.Select, Pages.Targets, Pages.Diag, Pages.Home })
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
                        sv.ScrollToHome();
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
        SetProp(vm, "WatchText", "Запасные: " + strategies[0].Title + " → " + strategies[1].Title + ". Последняя проверка: 16.10 12:30.");
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
