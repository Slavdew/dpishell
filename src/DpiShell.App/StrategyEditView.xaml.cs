using System.Windows;
using System.Windows.Controls;

namespace DpiShell.App;

/// <summary>Вид правки стратегии (<see cref="StrategyEditor"/>) и окно для него.</summary>
public partial class StrategyEditView : UserControl
{
    public StrategyEditView() => InitializeComponent();

    /// <summary>Показать окно правки поверх главного; true — нажали «Сохранить».</summary>
    public static bool ShowDialog(StrategyEditor editor)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = "DpiShell — " + editor.Header,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            Content = new StrategyEditView { DataContext = editor },
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        editor.Close += w.Close;
        w.ShowDialog();
        return editor.Saved;
    }
}
