using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DpiShell.App;

/// <summary>
/// Меню слева: значок пункта (Fluent), свёрнутое состояние (только значки), точка-отметка («есть обновление»).
/// Свёрнутость задаётся у TabControl и наследуется пунктами.
/// </summary>
public static class Nav
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(Geometry), typeof(Nav), new FrameworkPropertyMetadata(null));
    public static Geometry? GetIcon(DependencyObject o) => (Geometry?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, Geometry? v) => o.SetValue(IconProperty, v);

    /// <summary>Меню свёрнуто до значков.</summary>
    public static readonly DependencyProperty CollapsedProperty = DependencyProperty.RegisterAttached(
        "Collapsed", typeof(bool), typeof(Nav), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));
    public static bool GetCollapsed(DependencyObject o) => (bool)o.GetValue(CollapsedProperty);
    public static void SetCollapsed(DependencyObject o, bool v) => o.SetValue(CollapsedProperty, v);

    /// <summary>Точка на значке пункта (например, «Информация и обновления»: есть что обновить).</summary>
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(bool), typeof(Nav), new FrameworkPropertyMetadata(false));
    public static bool GetBadge(DependencyObject o) => (bool)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, bool v) => o.SetValue(BadgeProperty, v);
}

/// <summary>
/// Панель пунктов меню: первые пункты сверху, последние <see cref="BottomCount"/> — прижаты к низу («Настройки», «Информация и обновления»).
/// Скрытые пункты (Collapsed) места не занимают. Не хватает высоты — нижние пункты идут сразу за верхними.
/// </summary>
public sealed class NavPanel : Panel
{
    /// <summary>Сколько последних пунктов прижато к низу.</summary>
    public int BottomCount { get; set; } = 2;

    protected override Size MeasureOverride(Size available)
    {
        double w = 0, h = 0;
        foreach (UIElement c in InternalChildren)
        {
            c.Measure(new Size(available.Width, double.PositiveInfinity));
            w = Math.Max(w, c.DesiredSize.Width);
            h += c.DesiredSize.Height;
        }
        return new Size(double.IsInfinity(available.Width) ? w : available.Width, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var items = InternalChildren.Cast<UIElement>().ToList();
        int split = Math.Max(0, items.Count - BottomCount);
        double y = 0;
        for (int i = 0; i < split; i++)
        {
            items[i].Arrange(new Rect(0, y, finalSize.Width, items[i].DesiredSize.Height));
            y += items[i].DesiredSize.Height;
        }
        double bottom = items.Skip(split).Sum(c => c.DesiredSize.Height);
        y = Math.Max(y, finalSize.Height - bottom);
        for (int i = split; i < items.Count; i++)
        {
            items[i].Arrange(new Rect(0, y, finalSize.Width, items[i].DesiredSize.Height));
            y += items[i].DesiredSize.Height;
        }
        return finalSize;
    }
}
