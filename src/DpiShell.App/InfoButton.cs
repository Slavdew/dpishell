using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace DpiShell.App;

/// <summary>
/// Круглая «(i)» у заголовка раздела: по нажатию — карточка с пояснением (InfoTexts по ключу Topic), закрывается щелчком вне её.
/// Вид кнопки — стиль в Theme.xaml; цвета карточки — из словаря темы, поэтому тёмная тема подхватывается на лету.
/// </summary>
public sealed class InfoButton : Button
{
    public static readonly DependencyProperty TopicProperty =
        DependencyProperty.Register(nameof(Topic), typeof(string), typeof(InfoButton), new PropertyMetadata(null,
            // Экранный диктор читает не «Кнопка», а о чём пояснение.
            (d, e) => AutomationProperties.SetName(d, e.NewValue is string t ? "Пояснение: " + t : "Пояснение")));

    /// <summary>Ключ раздела в <see cref="InfoTexts"/> (заголовок из docs/10-info-texts.md).</summary>
    public string? Topic { get => (string?)GetValue(TopicProperty); set => SetValue(TopicProperty, value); }

    /// <summary>Что покажет карточка.</summary>
    public string Text => InfoTexts.Get(Topic);

    readonly Popup _popup;
    readonly TextBlock _text;

    public InfoButton()
    {
        AutomationProperties.SetName(this, "Пояснение");
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontSize = 13.5, LineHeight = 20 };
        _text.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        var card = new Border
        {
            Child = _text, Padding = new Thickness(16, 12, 16, 14), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            Margin = new Thickness(6, 2, 10, 12),
            Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.25, Color = Colors.Black },
        };
        card.SetResourceReference(Border.BackgroundProperty, "Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Line");
        _popup = new Popup
        {
            Child = card, StaysOpen = false, AllowsTransparency = true, PopupAnimation = PopupAnimation.Fade,
            Placement = PlacementMode.Bottom, PlacementTarget = this, VerticalOffset = 4,
        };
        // Пока карточка открыта, сама кнопка не ловит щелчок: иначе щелчок по ней закрыл бы карточку и тут же открыл снова.
        _popup.Opened += (_, _) => IsHitTestVisible = false;
        _popup.Closed += (_, _) => IsHitTestVisible = true;
    }

    public bool IsOpen => _popup.IsOpen;

    protected override void OnClick()
    {
        base.OnClick();
        _text.Text = Text;
        _popup.IsOpen = true;
    }
}
