using System.Windows;
using System.Windows.Controls;

namespace DpiShell.App;

/// <summary>Вид выбора DNS (<see cref="DnsPicker"/>). На ПК у «Применить» щит — нужны права (помощник).</summary>
public partial class DnsPickerView : UserControl
{
    public DnsPickerView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is DnsPicker { ShowShield: true } && TryFindResource("Shield") is DataTemplate shield)
                ApplyButton.ContentTemplate = shield;
        };
    }
}
