using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Windows;
using DpiShell.Core;
using System.Windows.Data;
using System.Windows.Threading;

namespace DpiShell.App;

public partial class MainWindow : Window
{
    readonly MainViewModel _vm;
    readonly Tray _tray;
    readonly DispatcherTimer _timer;
    readonly DispatcherTimer _clock;
    bool _exiting;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        Icon = Icons.ImageFor(vm.Kind);
        SourceInitialized += (_, _) => ThemeManager.SetTitleBar(this);

        _tray = new Tray(vm);
        _tray.OpenRequested += () => ShowFromTray();
        _tray.ExitRequested += () => _ = ExitAsync();
        vm.Updates.Restart = RestartIntoAsync;
        vm.StatusChanged += () => Icon = Icons.ImageFor(vm.Kind);

        // Состояние службы может поменяться и без нас (сбой, чужая программа) — тихо перечитываем.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => _ = vm.TickAsync();
        _timer.Start();

        // «Все адреса — 10 минут»: обратный отсчёт и возврат по сроку (и когда окно в трее).
        _clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => vm.Net.Tick();
        _clock.Start();
        vm.Notify += s => _tray.Hint(s);

        // Смена сети — с паузой, пока сеть поднимается.
        _netSettle = new DispatcherTimer { Interval = WatchdogPolicy.NetworkSettle };
        // Сеть сменилась — и роутер ищем заново: ушли из дома — блок роутера пропадёт (router-api §6).
        _netSettle.Tick += (_, _) => { _netSettle.Stop(); _ = vm.Router.RefreshAsync(); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        // «Войти» — небольшой диалог на месте, без перехода на другую вкладку (UX-аудит п. 14).
        vm.Router.LoginRequested += () => Dispatcher.BeginInvoke(() => RouterLoginDialog.Show(vm.Router, this), DispatcherPriority.Loaded);
        // Установка и удаление DpiShell на роутере — диалоги на месте (по SSH и API, architecture.md «Установка, обновление, удаление»).
        vm.Router.InstallRequested += () => Dispatcher.BeginInvoke(() => RouterInstallDialog.Show(vm.Router.NewInstall(), this), DispatcherPriority.Loaded);
        vm.Router.UninstallRequested += () => Dispatcher.BeginInvoke(() => RouterUninstallDialog.Show(vm.Router.NewUninstall(), this), DispatcherPriority.Loaded);

        // Ссылка ведёт к месту, а не в начало вкладки (UX-аудит 2, п. 2, 4): прокрутить, когда вкладка построится.
        vm.ShowRequested += spot => Dispatcher.BeginInvoke(() => ShowSpot(spot), DispatcherPriority.ContextIdle);
    }

    void Toast_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _vm.ToastHovered = true;
    void Toast_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) => _vm.ToastHovered = false;

    /// <summary>Прокрутить страницу к месту; к выбору DNS — ещё и раскрыть список вариантов.</summary>
    public void ShowSpot(Spot spot)
    {
        FrameworkElement target = spot == Spot.PcDns ? PcDnsPicker : StrategiesBlock;
        if (!target.IsVisible) return;
        UpdateLayout();
        DependencyObject? up = target;
        while (up is not null and not System.Windows.Controls.ScrollViewer) up = System.Windows.Media.VisualTreeHelper.GetParent(up);
        if (up is System.Windows.Controls.ScrollViewer { Content: UIElement content } sv)
            sv.ScrollToVerticalOffset(Math.Max(0, target.TranslatePoint(new Point(0, 0), content).Y - 12));
        else target.BringIntoView();
        if (spot == Spot.PcDns && FindChild<System.Windows.Controls.ComboBox>(target) is { } list) list.IsDropDownOpen = true;
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T t) return t;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindChild<T>(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    /// <summary>«Стратегия ▾» в блоке роутера: меню из strategies, текущая отмечена.</summary>
    void RouterStrategy_Click(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        var menu = new System.Windows.Controls.ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var router = _vm.Router;
        foreach (var s in router.Strategies)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = (s.Id == router.CurrentStrategyId ? "✓ " : "    ") + s.Title,
                InputGestureText = s.Stats,
                // Роли — если роутер их отдал: видно при наведении, копируется пунктом ниже.
                ToolTip = s.HasParams ? new System.Windows.Controls.TextBlock { Text = s.Params, FontFamily = new System.Windows.Media.FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap, MaxWidth = 640 } : null,
            };
            var id = s.Id;
            item.Click += (_, _) => _ = router.ChooseStrategyAsync(id);
            menu.Items.Add(item);
        }
        if (router.Strategies.FirstOrDefault(s => s.Id == router.CurrentStrategyId) is { HasParams: true } current)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var copy = new System.Windows.Controls.MenuItem { Header = "Скопировать параметры: " + current.Title };
            copy.Click += (_, _) => router.CopyParams(current);
            menu.Items.Add(copy);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Список стратегий роутера пуст", IsEnabled = false });
        menu.IsOpen = true;
    }

    /// <summary>
    /// Список внутри прокручиваемой страницы (стратегии на главной): свой ScrollViewer списка съедает колесо, даже когда
    /// прокручивать ему нечего, — отдаём колесо странице.
    /// </summary>
    void PassWheelToPage(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement { Parent: UIElement parent }) return;
        e.Handled = true;
        parent.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = sender });
    }

    /// <summary>Щелчок по подробностям в списке стратегий не выбирает стратегию (выбор там же включает её).</summary>
    void KeepSelection(object sender, System.Windows.Input.MouseButtonEventArgs e) => e.Handled = true;

    readonly DispatcherTimer _netSettle;

    /// <summary>Событие приходит из чужого потока и пачкой — перезапускаем отсчёт на потоке окна.</summary>
    void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _netSettle.Stop(); _netSettle.Start(); });

    /// <summary>Показать окно (из трея, по второму запуску); false — программа уже выходит.</summary>
    public bool ShowFromTray()
    {
        if (_exiting) return false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = _vm.TickAsync();
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            // Крестик — по настройке: в трей или выход из программы (без режима службы выход выключает и обход:
            // winws программы в Job-объекте и завершается вместе с ней; при следующем запуске ResumeIfNeeded вернёт его).
            if (_vm.CloseToTray) { Hide(); ShowTrayHintOnce(); }
            // Выход — после обработки закрытия: Close() внутри Closing бросает InvalidOperationException, и выход обрывался
            // до Shutdown (значок уже убран, окно закрыто вторым щелчком, процесс жив).
            else Dispatcher.BeginInvoke(() => _ = ExitAsync());
        }
        base.OnClosing(e);
    }

    void ShowTrayHintOnce()
    {
        // Отметка — в настройках окна (%LocalAppData%): папка программы окну без прав закрыта.
        if (_vm.Settings.TrayHintShown) return;
        _tray.Hint("Программа свернулась в трей. Выход — правой кнопкой по значку.");
        _vm.Settings.TrayHintShown = true;
        try { _vm.Settings.Save(); }
        catch { /* подсказка не важна */ }
    }

    /// <summary>Выход; подменяется в тестах. Сбой любого шага — в журнал, процесс завершается всё равно (<see cref="AppExit"/>).</summary>
    internal AppExit Exit { get; set; } = new()
    {
        Log = App.TryLog,
        Shutdown = () => Application.Current?.Shutdown(),
    };

    /// <summary>Запуск другой копии программы; в тестах — подмена (настоящую не запускаем).</summary>
    internal Action<System.Diagnostics.ProcessStartInfo> StartProcess { get; set; } = psi => System.Diagnostics.Process.Start(psi)?.Dispose();

    /// <summary>
    /// Обновление и откат: запустить другую копию (она ждёт выхода этой по --wait-pid) и выйти обычным путём —
    /// с вопросами и уборкой, как «Выход» в трее. Не запустилась — исключение вернётся в «Информация и обновления», окно остаётся.
    /// </summary>
    async Task RestartIntoAsync(DpiShell.Core.RestartPlan plan)
    {
        StartProcess(plan.ToStartInfo());
        await ExitAsync();
    }

    Task ExitAsync()
    {
        if (_exiting) return Task.CompletedTask;
        _exiting = true;
        _timer.Stop();
        _clock.Stop();
        _netSettle.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        return Exit.RunAsync(PrepareExitAsync, _tray.Dispose, _vm.Router.Dispose, Hide);
    }

    async Task PrepareExitAsync()
    {
        // «Все адреса» без программы не вернутся сами до следующего запуска — спросить (architecture.md «IP-списки и игровой режим»).
        if (_vm.Net.AnyActive && !_vm.Busy && _vm.Confirm("Сейчас включены «Все адреса». Вернуть обычный режим перед выходом?"))
        {
            // Через помощника: режимы меняет только он (без него «Все адреса» и не включить).
            if (_vm.Helper.Client is { } c)
            {
                try { await c.StopAllAddressesAsync(); }
                catch (Exception ex) { _vm.Fail(Texts.Friendly(ex)); }
            }
        }
        // Подбор держит временный winws и остановленную службу — дать ему вернуть всё на место.
        if (_vm.Busy)
        {
            _vm.Message = "Завершаю текущую операцию…";
            await _vm.WhenIdleAsync(TimeSpan.FromSeconds(20));
        }
    }
}

/// <summary>false → Visible, true → Collapsed: пустое состояние («роутер не найден»).</summary>
public sealed class FalseToVisible : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>0 → Visible, иначе Collapsed: «ничего не найдено». Invert — наоборот (заголовок таблицы, когда есть строки).</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int n && n == 0) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
