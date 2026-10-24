using System.Windows;
using DpiShell.App;

namespace DpiShell.Tests;

/// <summary>
/// Аудит уведомлений, №16: все MessageBox — через <see cref="Dialogs"/>. Владелец — только видимое несвёрнутое окно;
/// под тестами диалог не показывается вовсе (UiGuard срабатывает до создания любого окна, в том числе якоря).
/// </summary>
public class DialogsTests
{
    [Fact]
    public void Owner_is_only_a_visible_not_minimized_window()
    {
        WindowSmokeTests.RunUi(() =>
        {
            Assert.Null(Dialogs.OwnerFor(null));
            var w = new Window
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, Width = 200, Height = 100,
                ShowActivated = false, ShowInTaskbar = false,
            };
            try
            {
                Assert.Null(Dialogs.OwnerFor(w));        // не показано
                w.Show();
                Assert.Same(w, Dialogs.OwnerFor(w));     // видно — диалог принадлежит окну
                w.WindowState = WindowState.Minimized;
                Assert.Null(Dialogs.OwnerFor(w));        // свёрнуто — диалог поверх всех, а не за экраном
                w.WindowState = WindowState.Normal;
                w.Hide();
                Assert.Null(Dialogs.OwnerFor(w));        // в трее
            }
            finally { w.Close(); }
        });
    }

    [Fact]
    public void Under_tests_no_dialog_and_no_anchor_window_is_created()
    {
        WindowSmokeTests.RunUi(() =>
        {
            int before = Application.Current.Windows.Count;
            Assert.Throws<InvalidOperationException>(() => Dialogs.YesNo("вопрос"));
            Assert.Throws<InvalidOperationException>(() => Dialogs.Tell("сообщение", MessageBoxImage.Error));
            Assert.Equal(before, Application.Current.Windows.Count);
        });
    }

    [Fact]
    public void Default_confirmations_of_the_window_and_the_router_go_through_the_guard()
    {
        using var t = new TempDir();
        var shell = new DpiShell.Core.Shell(new DpiShell.Core.AppPaths(t.Sub("root"), Repo.Data));
        var settings = WindowSettings.Load(t.Sub("w.json"));
        var router = new RouterViewModel(shell, settings);
        Assert.Throws<InvalidOperationException>(() => router.Confirm("вопрос"));
        var vm = TestWindow.Model(shell, settings: settings);
        Assert.Throws<InvalidOperationException>(() => vm.Confirm("вопрос"));
        Assert.Throws<InvalidOperationException>(() => vm.AskHelper("вопрос"));
    }
}
