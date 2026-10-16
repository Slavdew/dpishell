using System.Windows;
using System.Windows.Controls;

namespace DpiShell.App;

/// <summary>Вопрос с несколькими действиями (у MessageBox подписи кнопок не поменять). Первая кнопка — главная, последняя — «Отмена».</summary>
public static class ChoiceDialog
{
    /// <returns>Индекс выбранного действия; -1 — «Отмена» или окно закрыли.</returns>
    public static int Show(string text, IReadOnlyList<string> options)
    {
        int result = -1;
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = "DpiShell",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), MaxWidth = 520 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        for (int i = 0; i < options.Count; i++)
        {
            var index = i;
            var b = new Button { Content = options[i], IsDefault = i == 0 };
            if (i == 0 && Application.Current?.TryFindResource("Primary") is Style primary) b.Style = primary;
            b.Click += (_, _) => { result = index; w.Close(); };
            buttons.Children.Add(b);
        }
        buttons.Children.Add(new Button { Content = "Отмена", IsCancel = true });
        ((Button)buttons.Children[^1]).Click += (_, _) => w.Close();
        panel.Children.Add(buttons);
        w.Content = panel;
        w.ShowDialog();
        return result;
    }
}
