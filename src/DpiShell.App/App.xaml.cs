using System.IO;
using System.Windows;
using System.Windows.Threading;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

public partial class App : Application
{
    Mutex? _single;
    EventWaitHandle? _showSignal;
    EventWaitHandle? _shownSignal;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnUiError;
        TaskScheduler.UnobservedTaskException += (_, a) => a.SetObserved();
        AppDomain.CurrentDomain.UnhandledException += (_, a) => TryLog(a.ExceptionObject as Exception);

        // Тот же DpiShell.exe — и помощник (helper-protocol §2): служба и её установка/удаление окна не открывают.
        if (HelperMode(e.Args) is { } code)
        {
            Shutdown(code);
            return;
        }

        // Одна копия: повторный запуск показывает окно уже работающей (она могла быть свёрнута в трей) и выходит;
        // не ответила за 3 с — предложить завершить её (SingleInstance).
        bool first;
        try
        {
            _single = new Mutex(true, @"Global\DpiShell.App", out first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\DpiShell.App.Show");
            _shownSignal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Global\DpiShell.App.Shown");
        }
        catch (UnauthorizedAccessException)
        {
            // Мьютекс создала копия, запущенная с правами (прежняя версия), — окно без прав к нему не допущено: копия уже есть.
            MessageBox.Show("DpiShell уже запущен с правами администратора. Закройте его (значок в трее → «Выход») и откройте снова.",
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }
        var gate = new SingleInstance
        {
            Acquire = () => first || TakeOver(),
            SignalShow = () => { _shownSignal.Reset(); _showSignal.Set(); },
            WaitShown = t => _shownSignal.WaitOne(t),
            Ask = q => MessageBox.Show(q, "DpiShell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.Yes) == MessageBoxResult.Yes,
            KillOld = SingleInstance.KillOtherWindows,
            Tell = s => MessageBox.Show(s, "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error),
        };
        if (!gate.ShouldRun())
        {
            Shutdown(0);
            return;
        }
        _showSignal.Reset();   // просьба, оставшаяся от завершённой копии, — не к нам
        var listener = new Thread(() =>
        {
            // Подтверждение — только когда окно правда показано: поток окна завис или окна нет — второй запуск это увидит.
            while (_showSignal.WaitOne())
                Dispatcher.BeginInvoke(() =>
                {
                    try { if (MainWindow is MainWindow w && w.ShowFromTray()) _shownSignal.Set(); }
                    catch (Exception ex) { TryLog(ex); }
                });
        }) { IsBackground = true, Name = "DpiShell show signal" };
        listener.Start();

        Shell shell;
        try { shell = new Shell(); }
        catch (Exception ex)
        {
            MessageBox.Show("Не найдены файлы программы (папка data рядом с DpiShell). Распакуйте архив заново.\n\n" + ex.Message,
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // Настройки окна — свои, в %LocalAppData% (первый раз — перенос из прежнего state.json, если он читается).
        var settings = WindowSettings.Load(legacy: shell.Paths);
        ThemeManager.Apply(settings.Theme);
        ThemeManager.Listen();

        var helper = new HelperLink { Post = a => Dispatcher.BeginInvoke(a) };
        var vm = new MainViewModel(shell, helper, settings);
        // Настоящие реестр и сеть — только здесь: тесты окна получают память и выключенную проверку обновлений.
        vm.Autostart.Store = new RegistryRunKeyStore();
        vm.Autostart.Apply();
        vm.LocalDns = new WindowsDnsSystem();   // только чтение DNS адаптеров без помощника; меняет помощник
        vm.Updates.Http = () => new System.Net.Http.HttpClient(new System.Net.Http.SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
            { Timeout = Timeout.InfiniteTimeSpan };
        RelayCommand.OnError = ex => { TryLog(ex); vm.Fail(Texts.Friendly(ex)); };
        try
        {
            var window = new MainWindow(vm);
            MainWindow = window;
            // Автозапуск при входе (--tray): окно не показываем — только значок в трее, обход поднимается как обычно.
            if (!e.Args.Contains(Autostart.TrayArg, StringComparer.OrdinalIgnoreCase)) window.Show();
        }
        catch (Exception ex)
        {
            // Окно не открылось — сказать и выйти, а не висеть в фоне без окна.
            TryLog(ex);
            MessageBox.Show($"Не удалось открыть окно DpiShell. Подробности — в {ErrorLog}.\n\n" + Texts.Friendly(ex),
                "DpiShell", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        _ = vm.InitializeAsync();
    }

    /// <summary>Прежняя копия завершена — взять мьютекс (брошенный мертвым процессом — тоже наш).</summary>
    bool TakeOver()
    {
        try { return _single!.WaitOne(TimeSpan.FromSeconds(3)); }
        catch (AbandonedMutexException) { return true; }
    }

    /// <summary>
    /// --helper (служба, вызывает SCM), --install-helper [SID] и --uninstall-helper (окно запускает их с runas).
    /// Null — обычный запуск окна. Ход установки — в журнал помощника (logs\helper.log), окно показывает код выхода.
    /// </summary>
    static int? HelperMode(string[] args)
    {
        if (args.Length == 0) return null;
        // Синхронно: Progress<T> отправлял бы строки в очередь окна, а она здесь не крутится.
        var log = new LogProgress(new HelperLog(new AppPaths()).Write);
        return args[0] switch
        {
            "--helper" => HelperEntry.RunService(args),
            "--install-helper" => HelperEntry.Install(args.Length > 1 ? args[1] : null, log),
            "--uninstall-helper" => HelperEntry.Uninstall(log),
            _ => null,
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { ThemeManager.StopListening(); }
        catch (Exception ex) { TryLog(ex); }
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

    sealed class LogProgress(Action<string> write) : IProgress<string>
    {
        public void Report(string value) => write(value);
    }

    /// <summary>Журнал окна — в %LocalAppData%\DpiShell\logs: папка программы окну без прав закрыта.</summary>
    static string ErrorLog => Path.Combine(WindowSettings.DefaultDir, "logs", "app-errors.log");

    internal static void TryLog(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLog)!);
            File.AppendAllText(ErrorLog, $"{DateTime.Now:s} {ex}{Environment.NewLine}");
        }
        catch { /* лог не важнее работы */ }
    }
}
