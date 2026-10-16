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
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += () => _ = ExitAsync();
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
        // Сеть сменилась — и роутер ищем заново: ушли из дома — блок роутера пропадёт (07-router-api §6).
        _netSettle.Tick += (_, _) => { _netSettle.Stop(); _ = vm.Router.RefreshAsync(); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;

        vm.Router.LoginRequested += () => Dispatcher.BeginInvoke(() =>
        {
            RouterSettings.BringIntoView();
            RouterPassword.Focus();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Пароль берётся из PasswordBox только в момент входа и сразу стирается из поля.</summary>
    async void ConnectRouter_Click(object sender, RoutedEventArgs e)
    {
        var password = RouterPassword.Password;
        RouterPassword.Clear();
        try { await _vm.Router.ConnectAsync(password); }
        catch (Exception ex) { _vm.Message = Texts.Friendly(ex); }
    }

    void RouterPassword_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter || !_vm.Router.CanConnect) return;
        e.Handled = true;
        ConnectRouter_Click(sender, e);
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
            };
            var id = s.Id;
            item.Click += (_, _) => _ = router.ChooseStrategyAsync(id);
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) menu.Items.Add(new System.Windows.Controls.MenuItem { Header = "Список стратегий роутера пуст", IsEnabled = false });
        menu.IsOpen = true;
    }

    readonly DispatcherTimer _netSettle;

    /// <summary>Событие приходит из чужого потока и пачкой — перезапускаем отсчёт на потоке окна.</summary>
    void OnNetworkChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => { _netSettle.Stop(); _netSettle.Start(); });

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        _ = _vm.TickAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_exiting)
        {
            e.Cancel = true;
            // Крестик — по настройке: в трей или выход из программы (без режима службы выход выключает и обход:
            // winws программы в Job-объекте и завершается вместе с ней; при следующем запуске ResumeIfNeeded вернёт его).
            if (_vm.CloseToTray) { Hide(); ShowTrayHintOnce(); }
            else _ = ExitAsync();
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

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _clock.Stop();
        _netSettle.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        // «Все адреса» без программы не вернутся сами до следующего запуска — спросить (ТЗ v2 §2).
        if (_vm.Net.AnyActive && !_vm.Busy && _vm.Confirm("Сейчас включены «Все адреса». Вернуть обычный режим перед выходом?"))
        {
            // Через помощника: режимы меняет только он (без него «Все адреса» и не включить).
            if (_vm.Helper.Client is { } c)
            {
                try { await c.StopAllAddressesAsync(); }
                catch (Exception ex) { _vm.Message = Texts.Friendly(ex); }
            }
        }
        // Подбор держит временный winws и остановленную службу — дать ему вернуть всё на место.
        if (_vm.Busy)
        {
            _vm.Message = "Завершаю текущую операцию…";
            await _vm.WhenIdleAsync(TimeSpan.FromSeconds(20));
        }
        _tray.Dispose();
        _vm.Router.Dispose();
        Close();
        Application.Current.Shutdown();
    }
}

/// <summary>0 → Visible, иначе Collapsed: «ничего не найдено». Invert — наоборот (заголовок таблицы, когда есть строки).</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is int n && n == 0) != Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
