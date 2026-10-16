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

        ThemeManager.Apply(shell.State.Theme);
        ThemeManager.Listen();

        var vm = new MainViewModel(shell);
        RelayCommand.OnError = ex => { TryLog(ex); vm.Message = Texts.Friendly(ex); };
        try
        {
            var window = new MainWindow(vm);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            // Окно не открылось — сказать и выйти, а не висеть в фоне без окна.
            TryLog(ex);
            MessageBox.Show($"Не удалось открыть окно DpiShell. Подробности — в {Path.Combine(new AppPaths().Logs, "app-errors.log")}.\n\n" + Texts.Friendly(ex),
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        _ = vm.InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ThemeManager.StopListening();
        base.OnExit(e);
    }

    void OnUiError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        TryLog(e.Exception);
        if (MainWindow is { IsLoaded: true, DataContext: MainViewModel vm }) vm.Message = Texts.Friendly(e.Exception);
        else
        {
            // Ошибка до того, как окно показалось: без окна приложение бесполезно и висело бы в фоне.
            MessageBox.Show(Texts.Friendly(e.Exception), "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
