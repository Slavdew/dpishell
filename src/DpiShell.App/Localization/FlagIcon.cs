using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DpiShell.App;

/// <summary>
/// Флаг языка — векторный: Windows не рисует эмодзи флагов (Segoe UI Emoji показывает две буквы).
/// Code — код страны ISO 3166 (ru, gb, de…). Есть рисунок — полосы или упрощённый флаг Великобритании; нет — плашка с кодом.
/// </summary>
public sealed class FlagIcon : Border
{
    public static readonly DependencyProperty CodeProperty =
        DependencyProperty.Register(nameof(Code), typeof(string), typeof(FlagIcon), new PropertyMetadata(null, (d, _) => ((FlagIcon)d).Build()));

    public string? Code { get => (string?)GetValue(CodeProperty); set => SetValue(CodeProperty, value); }

    // Полосы сверху вниз (h) или слева направо (v).
    static readonly Dictionary<string, (bool Vertical, string[] Colors)> Stripes = new()
    {
        ["ru"] = (false, ["#FFFFFF", "#0039A6", "#D52B1E"]),
        ["ua"] = (false, ["#0057B7", "#FFD700"]),
        ["de"] = (false, ["#000000", "#DD0000", "#FFCE00"]),
        ["pl"] = (false, ["#FFFFFF", "#DC143C"]),
        ["nl"] = (false, ["#AE1C28", "#FFFFFF", "#21468B"]),
        ["at"] = (false, ["#ED2939", "#FFFFFF", "#ED2939"]),
        ["fr"] = (true, ["#0055A4", "#FFFFFF", "#EF4135"]),
        ["it"] = (true, ["#009246", "#FFFFFF", "#CE2B37"]),
        ["ie"] = (true, ["#169B62", "#FFFFFF", "#FF883E"]),
        ["be"] = (true, ["#000000", "#FDDA24", "#EF3340"]),
    };

    public FlagIcon()
    {
        Width = 20;
        Height = 14;
        BorderThickness = new Thickness(0.5);
        CornerRadius = new CornerRadius(2);
        SnapsToDevicePixels = true;
        SetResourceReference(BorderBrushProperty, "Line");
        Build();
    }

    static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

    void Build()
    {
        var code = (Code ?? "").ToLowerInvariant();
        Background = null;
        if (Stripes.TryGetValue(code, out var s))
        {
            var g = new UniformGrid { Rows = s.Vertical ? 1 : s.Colors.Length, Columns = s.Vertical ? s.Colors.Length : 1 };
            foreach (var c in s.Colors) g.Children.Add(new Rectangle { Fill = Brush(c) });
            Child = g;
        }
        else if (code == "gb") Child = UnionJack();
        else
        {
            // Своего рисунка нет — плашка с кодом.
            var text = new TextBlock
            {
                Text = code.Length > 0 ? code.ToUpperInvariant()[..Math.Min(3, code.Length)] : "?",
                FontSize = 8, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            SetResourceReference(BackgroundProperty, "Hover");
            Child = text;
        }
    }

    /// <summary>Флаг Великобритании, упрощённо: синее поле, белые и красные диагонали, белый и красный крест.</summary>
    static UIElement UnionJack()
    {
        var c = new Canvas { Width = 60, Height = 30, ClipToBounds = true, Background = Brush("#012169") };
        void Line(double x1, double y1, double x2, double y2, string color, double width) =>
            c.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = Brush(color), StrokeThickness = width });
        Line(0, 0, 60, 30, "#FFFFFF", 6);
        Line(60, 0, 0, 30, "#FFFFFF", 6);
        Line(0, 0, 60, 30, "#C8102E", 2);
        Line(60, 0, 0, 30, "#C8102E", 2);
        Line(30, 0, 30, 30, "#FFFFFF", 10);
        Line(0, 15, 60, 15, "#FFFFFF", 10);
        Line(30, 0, 30, 30, "#C8102E", 6);
        Line(0, 15, 60, 15, "#C8102E", 6);
        return new Viewbox { Stretch = Stretch.Fill, Child = c };
    }
}
