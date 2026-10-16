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

    static System.Windows.Controls.TabControl? FindTabControl(DependencyObject root)
    {
        if (root is System.Windows.Controls.TabControl t) return t;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindTabControl(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    [Fact]
    public void MainWindow_OpensAndAllPagesBind_WithoutErrors()
    {
        using var tmp = new TempDir();
        var errors = new BindingErrors();
        RunSta(() =>
        {
            // Только стили (Theme.xaml) на голом Application. Не DpiShell.App.App: его конструктор ставит в очередь
            // OnStartup, и тот показывал пользователю «нужны права администратора» прямо во время тестов.
            if (Application.Current is null)
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/DpiShell;component/Theme.xaml") });
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
            var tabs = FindTabControl(w)!;
            foreach (var page in new[] { Pages.Select, Pages.Targets, Pages.Diag, Pages.Home })
            {
                vm.Page = page;
                Pump();
                // Пункты меню стоят на экране в порядке вкладок, какая бы ни была выбрана (TabPanel переставлял выбранную в конец).
                var ys = tabs.Items.Cast<System.Windows.Controls.TabItem>().Select(t => t.TranslatePoint(new Point(0, 0), w).Y).ToList();
                if (!ys.SequenceEqual(ys.Order())) throw new Exception($"Меню перемешано при вкладке {page}: {string.Join(", ", ys)}");
            }
            w.Hide();
        });
        Assert.True(errors.Lines.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors.Lines));
    }
}
