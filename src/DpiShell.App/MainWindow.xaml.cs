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

        // Автопроверка запасных: по таймеру и после смены сети (с паузой, пока сеть поднимается).
        vm.Notify += _tray.Hint;
        _watchTimer = new DispatcherTimer { Interval = WatchdogPolicy.Interval };
        _watchTimer.Tick += (_, _) => _ = vm.WatchAsync();
        _watchTimer.Start();
        _netSettle = new DispatcherTimer { Interval = WatchdogPolicy.NetworkSettle };
        _netSettle.Tick += (_, _) => { _netSettle.Stop(); _ = vm.WatchAsync(); };
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    readonly DispatcherTimer _watchTimer, _netSettle;

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
            // Крестик — в трей: обход работает службой и без окна.
            e.Cancel = true;
            Hide();
            ShowTrayHintOnce();
        }
        base.OnClosing(e);
    }

    void ShowTrayHintOnce()
    {
        try
        {
            var marker = Path.Combine(_vm.Shell.Paths.Root, "app-tray-hint");
            if (File.Exists(marker)) return;
            _tray.Hint("Программа свернулась в трей. Выход — правой кнопкой по значку.");
            Directory.CreateDirectory(_vm.Shell.Paths.Root);
            File.WriteAllText(marker, "");
        }
        catch { /* подсказка не важна */ }
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        _watchTimer.Stop();
        _netSettle.Stop();
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        // Подбор держит временный winws и остановленную службу — дать ему вернуть всё на место.
        if (_vm.Busy)
        {
            _vm.Message = "Завершаю текущую операцию…";
            await _vm.WhenIdleAsync(TimeSpan.FromSeconds(20));
        }
        _tray.Dispose();
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
