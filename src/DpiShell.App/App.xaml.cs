using System.IO;
using System.Windows;
using System.Windows.Threading;
using DpiShell.Core;

namespace DpiShell.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUiError;
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();
        AppDomain.CurrentDomain.UnhandledException += (_, a) => TryLog(a.ExceptionObject as Exception);

        if (!Shell.IsAdmin())
        {
            MessageBox.Show("DpiShell нужны права администратора: без них нельзя управлять обходом.\nЗапустите программу через «Запуск от имени администратора».",
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        Shell shell;
        try { shell = new Shell(); }
        catch (Exception ex)
        {
            MessageBox.Show("Не найдены файлы программы (папка data рядом с DpiShell). Распакуйте архив заново.\n\n" + ex.Message,
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var vm = new MainViewModel(shell);
        RelayCommand.OnError = ex => { TryLog(ex); vm.Message = Texts.Friendly(ex); };
        var window = new MainWindow(vm);
        MainWindow = window;
        window.Show();
        _ = vm.InitializeAsync();
    }

    void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        TryLog(e.Exception);
        if (MainWindow?.DataContext is MainViewModel vm) vm.Message = Texts.Friendly(e.Exception);
        else MessageBox.Show(Texts.Friendly(e.Exception), "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    static void TryLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            var p = new AppPaths();
            Directory.CreateDirectory(p.Logs);
            File.AppendAllText(Path.Combine(p.Logs, "app-errors.log"), $"{DateTime.Now:s} {ex}{Environment.NewLine}");
        }
        catch { /* лог не важнее работы */ }
    }
}
