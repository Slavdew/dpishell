using System.Windows;
using System.Windows.Controls;

namespace DpiShell.App;

/// <summary>Вид «Продвинутого режима» (<see cref="AdvancedViewModel"/>). На ПК у «Применить» и «Вернуть как было» щит — нужны права (помощник).</summary>
public partial class AdvancedView : UserControl
{
    public AdvancedView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AdvancedViewModel { ShowShield: true } && TryFindResource("Shield") is DataTemplate shield)
            {
                ApplyButton.ContentTemplate = shield;
                RevertButton.ContentTemplate = shield;
            }
        };
    }
}
