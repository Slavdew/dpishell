using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DpiShell.App;

/// <summary>Окно «Импорт профиля» над <see cref="AppsViewModel"/>: строка <c>dpishell-game:1:…</c>, предпросмотр, «Добавить».</summary>
public partial class ProfileImportView : UserControl
{
    public ProfileImportView()
    {
        InitializeComponent();
        Loaded += (_, _) => { CodeBox.Focus(); CodeBox.CaretIndex = CodeBox.Text.Length; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || DataContext is not AppsViewModel vm) return;
            vm.CancelCodeCmd.Execute(null);
            e.Handled = true;
        };
    }

    /// <summary>Показать окно поверх главного; закрывается по <see cref="AppsViewModel.CodeClosed"/> или крестиком.</summary>
    public static void ShowDialog(AppsViewModel vm)
    {
        UiGuard.Check(L.T("apps.code.title"));
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = Host(vm);
        w.Owner = owner;
        w.ShowInTaskbar = owner is null;
        w.WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        void Close() => w.Close();
        vm.CodeClosed += Close;
        try { w.ShowDialog(); }
        finally { vm.CodeClosed -= Close; }
    }

    /// <summary>Окно с видом, оформленное по теме (без показа): его же показывают тесты за краем экрана.</summary>
    public static Window Host(AppsViewModel vm)
    {
        var w = new Window
        {
            Title = L.T("apps.code.title"),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Content = new ProfileImportView { DataContext = vm },
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        return w;
    }
}
