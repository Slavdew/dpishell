using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Окно открывается в памяти (за пределами экрана), все вкладки строятся, ошибок привязок нет.
/// Без прав администратора и сети: ядро не ставится (InitializeAsync не вызывается).
/// Поймал бы падение 2026-10-16: TwoWay-привязка ProgressBar.Value к свойству без публичного set.
/// </summary>
public sealed class WindowSmokeTests
{
    sealed class BindingErrors : TraceListener
    {
        public readonly List<string> Lines = [];
        public override void Write(string? message) { }
        public override void WriteLine(string? message) { if (message is not null) lock (Lines) Lines.Add(message); }
    }

    static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception e) { error = e; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Exception("Окно упало: " + error.Message, error);
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void MainWindow_OpensAndAllPagesBind_WithoutErrors()
    {
        using var tmp = new TempDir();
        var errors = new BindingErrors();
        RunSta(() =>
        {
            // Общие стили окна живут в App.xaml: поднимаем приложение без OnStartup (тот требует админа).
            if (Application.Current is null)
            {
                var app = new DpiShell.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.InitializeComponent();
            }
            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;

            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = new MainViewModel(shell);
            var w = new MainWindow(vm)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
            };
            w.Show();
            Pump();
            foreach (var page in new[] { Pages.Select, Pages.Targets, Pages.Diag, Pages.Home })
            {
                vm.Page = page;
                Pump();
            }
            w.Hide();
        });
        Assert.True(errors.Lines.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors.Lines));
    }
}
