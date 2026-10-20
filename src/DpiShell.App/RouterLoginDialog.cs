using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>
/// Вход на роутер небольшим окном на месте (UX-аудит п. 14): раньше «Войти» уводило в конец «Диагностики».
/// Пароль — только из PasswordBox в момент входа, сразу стирается; адрес — в «Дополнительно». Enter — «Войти», Esc — «Отмена».
/// </summary>
public static class RouterLoginDialog
{
    public static void Show(RouterViewModel router, Window? owner) => Create(router, owner).ShowDialog();

    /// <summary>Окно входа (без показа — для теста окна и снимка).</summary>
    public static Window Create(RouterViewModel router, Window? owner)
    {
        owner = owner is { IsVisible: true } ? owner : null;
        var w = new Window
        {
            Title = "Вход на роутер",
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
            FontSize = 14,
            DataContext = router,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(w);

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 380 };
        panel.Children.Add(new TextBlock { Text = "Пароль администратора роутера — обычно тот же, что для входа в его настройки (LuCI).", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var password = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(password, "Пароль роутера");
        panel.Children.Add(password);

        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4), Visibility = Visibility.Collapsed };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        panel.Children.Add(error);
        // У свежей OpenWrt пароль root не задан (UX-аудит 2, п. 11): явный выбор, а не «пустое поле», — пустое поле значит «взять сохранённый».
        var noPassword = new CheckBox { Content = "Пароль не задан (свежая OpenWrt)", Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        noPassword.Checked += (_, _) => { password.Clear(); password.IsEnabled = false; };
        noPassword.Unchecked += (_, _) => { password.IsEnabled = true; password.Focus(); };
        panel.Children.Add(noPassword);
        // «Поле пустое — вход с сохранённым» — только когда сохранённый есть.
        var note = new TextBlock
        {
            Text = "Пароль хранится зашифрованным и только на этом компьютере." + (router.HasSavedPassword ? " Поле пустое — вход с сохранённым." : ""),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 10),
        };
        if (Application.Current?.TryFindResource("Hint") is Style hint) note.Style = hint;
        panel.Children.Add(note);

        panel.Children.Add(AddressExpander(router));

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var login = new Button { Content = "Войти", IsDefault = true };
        if (Application.Current?.TryFindResource("Primary") is Style primary) login.Style = primary;
        var cancel = new Button { Content = L.T("common.cancel"), IsCancel = true };
        buttons.Children.Add(login);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        w.Content = panel;

        login.Click += async (_, _) =>
        {
            var pw = password.Password;
            password.Clear();
            login.IsEnabled = false;
            error.Visibility = Visibility.Collapsed;
            try { await router.ConnectAsync(pw, noPassword: noPassword.IsChecked == true); }
            catch (Exception ex) { router.Failed(Texts.Friendly(ex)); }
            finally { login.IsEnabled = true; }
            if (router.Connected) { w.Close(); return; }
            error.Text = router.ConnectionText;
            error.Visibility = Visibility.Visible;
            password.Focus();
        };
        w.Loaded += (_, _) => password.Focus();
        return w;
    }

    /// <summary>«Дополнительно: адрес роутера» — шлюз или свой (обычно определяется сам). DataContext — роутер.</summary>
    public static Expander AddressExpander(RouterViewModel router)
    {
        // «Дополнительно»: адрес роутера (обычно определяется сам, менять не нужно).
        var address = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var auto = new RadioButton { Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left };
        auto.SetBinding(ContentControl.ContentProperty, new Binding(nameof(RouterViewModel.AutoAddressText)) { Mode = BindingMode.OneWay });
        auto.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(RouterViewModel.AddressAuto));
        address.Children.Add(auto);
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        var custom = new RadioButton { Content = "Свой:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        custom.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(RouterViewModel.AddressCustom));
        var customText = new TextBox { Width = 200, Padding = new Thickness(6, 4, 6, 4), ToolTip = "IP или имя роутера, например 192.168.1.1" };
        customText.SetBinding(TextBox.TextProperty, new Binding(nameof(RouterViewModel.CustomAddress)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
        customText.SetBinding(UIElement.IsEnabledProperty, nameof(RouterViewModel.AddressCustom));
        AutomationProperties.SetName(customText, "Адрес роутера");
        customRow.Children.Add(custom);
        customRow.Children.Add(customText);
        address.Children.Add(customRow);
        return new Expander { Header = "Дополнительно: адрес роутера", Content = address, Margin = new Thickness(0, 0, 0, 16), DataContext = router };

    }
}
