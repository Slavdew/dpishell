using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using ShapePath = System.Windows.Shapes.Path;

namespace DpiShell.App;

/// <summary>Действие в вопросе с пояснениями: название, строка «что будет», значок (ключ Geometry в Theme.xaml). Недоступное — серым, с подсказкой вместо пояснения.</summary>
public sealed record ChoiceOption(string Title, string Detail, string? Icon = null, bool Enabled = true, string? DisabledHint = null)
{
    public string Shown => Enabled || DisabledHint is null ? Detail : DisabledHint;
}

/// <summary>Вопрос с пояснениями под кнопками (диалог «Оптимизация уже работает на роутере»): заголовок, текст, действия карточками, строка внизу, «Отмена».</summary>
public sealed record ChoiceRequest(string Title, string Text, IReadOnlyList<ChoiceOption> Options, string Footer = "", string? Icon = null);

/// <summary>Вопрос с несколькими действиями (у MessageBox подписи кнопок не поменять). Первая кнопка — главная, последняя — «Отмена».</summary>
public static class ChoiceDialog
{
    /// <returns>Индекс выбранного действия; -1 — «Отмена» или окно закрыли.</returns>
    public static int Show(string text, IReadOnlyList<string> options)
    {
        UiGuard.Check(text);
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
        buttons.Children.Add(new Button { Content = L.T("common.cancel"), IsCancel = true });
        ((Button)buttons.Children[^1]).Click += (_, _) => w.Close();
        panel.Children.Add(buttons);
        w.Content = panel;
        w.ShowDialog();
        return result;
    }

    /// <returns>Индекс выбранного действия; -1 — «Отмена» или окно закрыли.</returns>
    public static int Show(ChoiceRequest request)
    {
        UiGuard.Check(request.Title);
        int result = -1;
        var w = Create(request, i => result = i);
        w.ShowDialog();
        return result;
    }

    /// <summary>Окно вопроса с пояснениями (без показа — для теста окна и снимка). <paramref name="chosen"/> получает индекс действия.</summary>
    public static Window Create(ChoiceRequest request, Action<int> chosen)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = "DpiShell",
            SizeToContent = SizeToContent.Height,
            Width = 520,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(w);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        if (Glyph(request.Icon, 22, "AccentText") is { } hi) { hi.Margin = new Thickness(0, 0, 10, 0); head.Children.Add(hi); }
        head.Children.Add(new TextBlock { Text = request.Title, FontSize = 17, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(head);
        var text = new TextBlock { Text = request.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        panel.Children.Add(text);
        var card = Application.Current?.TryFindResource("ChoiceCard") as Style;
        for (int i = 0; i < request.Options.Count; i++)
        {
            var o = request.Options[i];
            var index = i;
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            if (Glyph(o.Icon, 18, o.Enabled ? "AccentText" : "Muted") is { } ic) { ic.Margin = new Thickness(0, 2, 12, 0); ic.VerticalAlignment = VerticalAlignment.Top; grid.Children.Add(ic); }
            var words = new StackPanel();
            Grid.SetColumn(words, 1);
            words.Children.Add(new TextBlock { Text = o.Title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            var detail = new TextBlock { Text = o.Shown, FontSize = 13, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            words.Children.Add(detail);
            grid.Children.Add(words);
            if (o.Enabled && Glyph("HomeIcon.ChevronRight", 14, "Muted") is { } chev) { Grid.SetColumn(chev, 2); chev.Margin = new Thickness(12, 0, 0, 0); chev.VerticalAlignment = VerticalAlignment.Center; grid.Children.Add(chev); }
            var b = new Button { Content = grid, IsEnabled = o.Enabled, Margin = new Thickness(0, 0, 0, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            if (card is not null) b.Style = card;
            System.Windows.Automation.AutomationProperties.SetName(b, o.Title);
            // Первое доступное действие — по Enter и выделено рамкой акцента.
            if (o.Enabled && request.Options.Take(i).All(x => !x.Enabled)) { b.IsDefault = true; b.SetResourceReference(Control.BorderBrushProperty, "Accent"); }
            // Недоступное — подсказка видна и при наведении (кнопка серая, но строка «куда добавить» читается).
            if (!o.Enabled && o.DisabledHint is not null) { b.ToolTip = o.DisabledHint; ToolTipService.SetShowOnDisabled(b, true); }
            b.Click += (_, _) => { chosen(index); w.Close(); };
            panel.Children.Add(b);
        }
        if (request.Footer.Length > 0)
        {
            var foot = new TextBlock { Text = request.Footer, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 12) };
            foot.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            panel.Children.Add(foot);
        }
        var cancel = new Button { Content = L.T("common.cancel"), IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0) };
        cancel.Click += (_, _) => w.Close();
        panel.Children.Add(cancel);
        w.Content = panel;
        return w;
    }

    /// <summary>Значок Fluent из Theme.xaml (Geometry по ключу) цветом темы; нет ключа — null.</summary>
    static ShapePath? Glyph(string? key, double size, string brush)
    {
        if (key is null || Application.Current?.TryFindResource(key) is not Geometry g) return null;
        var p = new ShapePath { Data = g, Width = size, Height = size, Stretch = Stretch.Uniform };
        p.SetResourceReference(Shape.FillProperty, brush);
        return p;
    }
}
