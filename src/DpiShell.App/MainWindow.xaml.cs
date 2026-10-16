using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
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

        _tray = new Tray(vm);
        _tray.OpenRequested += ShowFromTray;
        _tray.ExitRequested += () => _ = ExitAsync();
        vm.StatusChanged += () => Icon = Icons.ImageFor(vm.Kind);

        // Состояние службы может поменяться и без нас (сбой, чужая программа) — тихо перечитываем.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) => _ = vm.TickAsync();
        _timer.Start();
    }

    void ShowFromTray()
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

/// <summary>0 → Visible, иначе Collapsed: «ничего не найдено».</summary>
public sealed class ZeroToVisible : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int n && n == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
