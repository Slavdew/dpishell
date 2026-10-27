using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DpiShell.App;

/// <summary>Окно «Новый профиль» над <see cref="AppsViewModel"/>: название, фильтр, параметры, подсети (формат Flowseal).</summary>
public partial class ProfileAddView : UserControl
{
    public ProfileAddView()
    {
        InitializeComponent();
        Loaded += (_, _) => TitleBox.Focus();
        // Esc — отмена; Enter в многострочных полях — новая строка, «Добавить» только кнопкой.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not AppsViewModel vm) return;
            vm.CancelAddCmd.Execute(null);
            e.Handled = true;
        };
    }

    /// <summary>Показать окно поверх главного; закрывается по <see cref="AppsViewModel.AddClosed"/> или крестиком.</summary>
    public static void ShowDialog(AppsViewModel vm)
    {
        UiGuard.Check(L.T("apps.add.title"));
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = Host(vm);
        w.Owner = owner;
        w.ShowInTaskbar = owner is null;
        w.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        void Close() => w.Close();
        vm.AddClosed += Close;
        try { w.ShowDialog(); }
        finally { vm.AddClosed -= Close; }
    }

    /// <summary>Окно с видом, оформленное по теме (без показа): его же показывают тесты за краем экрана.</summary>
    public static Window Host(AppsViewModel vm)
    {
        var w = new Window
        {
            Title = L.T("apps.add.title"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Content = new ProfileAddView { DataContext = vm },
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        return w;
    }
}
