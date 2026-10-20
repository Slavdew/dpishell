using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace DpiShell.App;

/// <summary>Пустая строка — виден (подсказка в поле), иначе скрыт.</summary>
public sealed class EmptyToVisible : IValueConverter
{
    public static readonly EmptyToVisible Instance = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Окно «Выгрузить / Загрузить настройки» (<see cref="TransferViewModel"/>).</summary>
public partial class TransferView : UserControl
{
    public TransferView() => InitializeComponent();

    /// <summary>Окно поверх главного. DataContext окна — модель: значок щита (шаблон Shield) смотрит на NeedsHelper окна.</summary>
    public static Window Create(TransferViewModel model)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var w = new Window
        {
            Title = model.Title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            DataContext = model,
            Content = new TransferView(),
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(w);
        model.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(TransferViewModel.Title) or "") w.Title = model.Title; };
        model.Close = w.Close;
        return w;
    }

    public static void ShowDialog(TransferViewModel model) => Create(model).ShowDialog();
}
