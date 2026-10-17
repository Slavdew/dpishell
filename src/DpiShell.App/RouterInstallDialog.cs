using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DpiShell.Core;

namespace DpiShell.App;

/// <summary>Общее для диалогов роутера: окно в теме, привязки видимости, «Подробнее» с журналом.</summary>
static class DialogKit
{
    static readonly BooleanToVisibilityConverter Vis = new();

    public static Window Window(string title, object dataContext, Window? owner)
    {
        owner = owner is { IsVisible: true } ? owner : null;
        var w = new Window
        {
            Title = title,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner,
            ShowInTaskbar = owner is null,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            DataContext = dataContext,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Page");
        w.SetResourceReference(Control.ForegroundProperty, "Text");
        w.SourceInitialized += (_, _) => ThemeManager.SetTitleBar(w);
        return w;
    }

    public static T Show<T>(this T e, string path) where T : FrameworkElement
    {
        e.SetBinding(UIElement.VisibilityProperty, new Binding(path) { Converter = Vis });
        return e;
    }

    public static T Enable<T>(this T e, string path) where T : FrameworkElement
    {
        e.SetBinding(UIElement.IsEnabledProperty, path);
        return e;
    }

    public static TextBlock Text(string path, Thickness margin, string? brush = null, bool bold = false)
    {
        var t = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = margin, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        t.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay });
        if (brush is not null) t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static TextBlock Hint(string text, Thickness margin)
    {
        var t = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
        if (Application.Current?.TryFindResource("Hint") is Style hint) t.Style = hint;
        return t;
    }

    /// <summary>«Подробнее» — хвост журнала, моноширинно, можно выделить и скопировать.</summary>
    public static Expander Details(string textPath, string visiblePath)
    {
        var box = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12,
            MaxHeight = 180, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 4, 6, 4),
        };
        box.SetBinding(TextBox.TextProperty, new Binding(textPath) { Mode = BindingMode.OneWay });
        AutomationProperties.SetName(box, "Журнал");
        box.TextChanged += (_, _) => box.ScrollToEnd();
        return new Expander { Header = "Подробнее", Content = box, Margin = new Thickness(0, 4, 0, 8) }.Show(visiblePath);
    }

    public static Button Button(string text, bool primary = false, bool cancel = false)
    {
        var b = new Button { Content = text, IsCancel = cancel };
        if (primary && Application.Current?.TryFindResource("Primary") is Style p) b.Style = p;
        return b;
    }

    /// <summary>Плашка: текст на фоне предупреждения.</summary>
    public static Border Plate(UIElement content, Thickness margin)
    {
        var b = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(10, 6, 10, 6), Margin = margin, Child = content };
        b.SetResourceReference(Border.BackgroundProperty, "WarnBack");
        b.SetResourceReference(Border.BorderBrushProperty, "WarnLine");
        return b;
    }
}

/// <summary>
/// «Установить на роутер»: адрес (по умолчанию шлюз) и пароль root → «Проверить роутер» → «Установить» → ход по шагам → итог.
/// Пароль — только из PasswordBox в момент действия. Закрыть во время работы — прервать (с вопросом).
/// </summary>
public static class RouterInstallDialog
{
    public static void Show(RouterInstallViewModel vm, Window? owner) => Create(vm, owner).ShowDialog();

    public static Window Create(RouterInstallViewModel vm, Window? owner)
    {
        vm.Router.RefreshGateway();
        var w = DialogKit.Window("Установка DpiShell на роутер", vm, owner);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = "DpiShell ставится на роутер с OpenWrt — обход заработает для всех устройств дома. Нужен пароль администратора роутера, тот же, что для входа в его настройки (LuCI).",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        var input = new StackPanel().Enable(nameof(RouterInstallViewModel.CanEdit)).Show(nameof(RouterInstallViewModel.ShowCheck));
        var password = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(password, "Пароль роутера");
        input.Children.Add(password);
        var noPassword = new CheckBox { Content = "Пароль не задан (свежая OpenWrt)", Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        noPassword.Checked += (_, _) => { password.Clear(); password.IsEnabled = false; };
        noPassword.Unchecked += (_, _) => { password.IsEnabled = true; password.Focus(); };
        input.Children.Add(noPassword);
        if (vm.Router.HasSavedPassword) input.Children.Add(DialogKit.Hint("Поле пустое — сохранённый пароль.", new Thickness(0, 0, 0, 6)));
        input.Children.Add(RouterLoginDialog.AddressExpander(vm.Router));
        panel.Children.Add(input);

        // Итог проверки: годится / не годится и почему / через этот ПК.
        var verdict = new StackPanel();
        var title = DialogKit.Text(nameof(RouterInstallViewModel.VerdictTitle), new Thickness(0, 0, 0, 2), bold: true);
        var titleStyle = new Style(typeof(TextBlock));
        titleStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Danger")));
        var ok = new DataTrigger { Binding = new Binding(nameof(RouterInstallViewModel.VerdictOk)), Value = true };
        ok.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Ok")));
        titleStyle.Triggers.Add(ok);
        title.Style = titleStyle;
        verdict.Children.Add(title);
        verdict.Children.Add(DialogKit.Text(nameof(RouterInstallViewModel.VerdictText), new Thickness(0, 2, 0, 2)).Show(nameof(RouterInstallViewModel.HasVerdictText)));
        verdict.Children.Add(DialogKit.Plate(DialogKit.Text(nameof(RouterInstallViewModel.TunnelNote), new Thickness(0)), new Thickness(0, 6, 0, 0))
            .Show(nameof(RouterInstallViewModel.HasTunnelNote)));
        verdict.Margin = new Thickness(0, 0, 0, 10);
        panel.Children.Add(verdict.Show(nameof(RouterInstallViewModel.HasVerdict)));

        // Ход установки по шагам.
        var steps = new ItemsControl { ItemsSource = vm.Steps, Margin = new Thickness(0, 0, 0, 8) };
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));
        var mark = new FrameworkElementFactory(typeof(TextBlock));
        mark.SetBinding(TextBlock.TextProperty, new Binding(nameof(RouterInstallStep.Mark)));
        mark.SetValue(FrameworkElement.WidthProperty, 22.0);
        mark.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        row.AppendChild(mark);
        var name = new FrameworkElementFactory(typeof(TextBlock));
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(RouterInstallStep.Title)));
        row.AppendChild(name);
        steps.ItemTemplate = new DataTemplate { VisualTree = row };
        panel.Children.Add(steps.Show(nameof(RouterInstallViewModel.ShowSteps)));

        var progress = DialogKit.Text(nameof(RouterInstallViewModel.ProgressText), new Thickness(0, 0, 0, 8));
        if (Application.Current?.TryFindResource("Hint") is Style hint) progress.Style = hint;
        panel.Children.Add(progress.Show(nameof(RouterInstallViewModel.Working)));
        panel.Children.Add(DialogKit.Text(nameof(RouterInstallViewModel.ResultText), new Thickness(0, 0, 0, 8), "Ok", bold: true).Show(nameof(RouterInstallViewModel.HasResult)));
        panel.Children.Add(DialogKit.Text(nameof(RouterInstallViewModel.ErrorText), new Thickness(0, 0, 0, 4), "Danger").Show(nameof(RouterInstallViewModel.HasError)));
        panel.Children.Add(DialogKit.Details(nameof(RouterInstallViewModel.Details), nameof(RouterInstallViewModel.HasDetails)));

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var check = DialogKit.Button("Проверить роутер").Enable(nameof(RouterInstallViewModel.CanCheck)).Show(nameof(RouterInstallViewModel.ShowCheck));
        var install = DialogKit.Button("Установить", primary: true).Enable(nameof(RouterInstallViewModel.CanInstall)).Show(nameof(RouterInstallViewModel.ShowCheck));
        var select = DialogKit.Button("Подобрать на роутере", primary: true).Show(nameof(RouterInstallViewModel.CanGoSelect));
        var close = DialogKit.Button("Закрыть", cancel: true);
        check.IsDefault = true;
        foreach (var b in new[] { check, install, select, close }) buttons.Children.Add(b);
        panel.Children.Add(buttons);
        w.Content = panel;

        async Task Run(Func<string?, bool, Task<bool>> action)
        {
            try { await action(password.Password, noPassword.IsChecked == true); }
            catch (Exception ex) { vm.Router.Say(Texts.Friendly(ex)); }
            install.IsDefault = vm.CanInstall;
            check.IsDefault = !vm.CanInstall;
        }
        check.Click += async (_, _) => await Run(vm.CheckAsync);
        install.Click += async (_, _) => { await Run(vm.InstallAsync); if (vm.IsDone) password.Clear(); };
        select.Click += (_, _) =>
        {
            w.Close();
            // Ход — на экране «Подбор»; ошибку старта скажет сам подбор (Guard), здесь — только неожиданное.
            _ = vm.SelectNowAsync().ContinueWith(t => vm.Router.Say(Texts.Friendly(t.Exception!.GetBaseException())),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.FromCurrentSynchronizationContext());
        };
        close.Click += (_, _) => w.Close();
        w.Closing += (_, e) =>
        {
            if (!vm.Working) return;
            if (MessageBox.Show(w, "Прервать установку? Роутер может остаться наполовину настроенным — тогда просто запустите установку ещё раз.", "DpiShell",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) vm.Cancel();
            e.Cancel = true;
        };
        w.Closed += (_, _) => password.Clear();
        w.Loaded += (_, _) => password.Focus();
        return w;
    }
}

/// <summary>
/// «Удалить DpiShell с роутера»: что произойдёт, выбор «сохранить настройки / удалить полностью», галочка-подтверждение,
/// «Отмена» — по умолчанию (Enter и Esc). После — «что осталось» и «Установить заново».
/// </summary>
public static class RouterUninstallDialog
{
    public static void Show(RouterUninstallViewModel vm, Window? owner) => Create(vm, owner).ShowDialog();

    public static Window Create(RouterUninstallViewModel vm, Window? owner)
    {
        vm.Router.RefreshGateway();
        var w = DialogKit.Window("Удаление DpiShell с роутера", vm, owner);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 460 };

        var confirm = new StackPanel().Show(nameof(RouterUninstallViewModel.IsConfirm)).Enable(nameof(RouterUninstallViewModel.IsConfirm));
        confirm.Children.Add(new TextBlock
        {
            Text = "Обход на роутере перестанет работать для всех устройств дома. Что DpiShell менял в настройках роутера (DNS, ускорение сети, расписание), вернётся как было; ядро обхода и пакеты удалятся.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });
        var keep = new RadioButton { Content = "Сохранить настройки — чтобы поставить заново с ними", Margin = new Thickness(0, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
        keep.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(RouterUninstallViewModel.KeepSettings));
        var full = new RadioButton { Content = "Удалить полностью — вместе с настройками", Margin = new Thickness(0, 2, 0, 10), HorizontalAlignment = HorizontalAlignment.Left };
        full.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(RouterUninstallViewModel.Full));
        confirm.Children.Add(keep);
        confirm.Children.Add(full);

        // Не вошли на роутер — удаление по SSH, нужен пароль.
        var pwBlock = new StackPanel().Show(nameof(RouterUninstallViewModel.ShowPassword));
        pwBlock.Children.Add(new TextBlock { Text = "Пароль роутера (удаление пойдёт по SSH):", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        var password = new PasswordBox { Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 0, 0, 6) };
        AutomationProperties.SetName(password, "Пароль роутера");
        pwBlock.Children.Add(password);
        var noPassword = new CheckBox { Content = "Пароль не задан (свежая OpenWrt)", Margin = new Thickness(0, 2, 0, 6), HorizontalAlignment = HorizontalAlignment.Left };
        noPassword.Checked += (_, _) => { password.Clear(); password.IsEnabled = false; };
        noPassword.Unchecked += (_, _) => password.IsEnabled = true;
        pwBlock.Children.Add(noPassword);
        if (vm.Router.HasSavedPassword) pwBlock.Children.Add(DialogKit.Hint("Поле пустое — сохранённый пароль.", new Thickness(0, 0, 0, 6)));
        confirm.Children.Add(pwBlock);

        var sure = new CheckBox { Content = "Да, удалить DpiShell с роутера", Margin = new Thickness(0, 6, 0, 4), HorizontalAlignment = HorizontalAlignment.Left, FontWeight = FontWeights.SemiBold };
        sure.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, nameof(RouterUninstallViewModel.Understood));
        confirm.Children.Add(sure);
        panel.Children.Add(confirm);

        var progress = DialogKit.Text(nameof(RouterUninstallViewModel.ProgressText), new Thickness(0, 8, 0, 8));
        if (Application.Current?.TryFindResource("Hint") is Style hint) progress.Style = hint;
        panel.Children.Add(progress.Show(nameof(RouterUninstallViewModel.Working)));

        var done = new StackPanel().Show(nameof(RouterUninstallViewModel.IsDone));
        var title = DialogKit.Text(nameof(RouterUninstallViewModel.ResultTitle), new Thickness(0, 0, 0, 6), bold: true);
        title.FontSize = 16;
        done.Children.Add(title);
        done.Children.Add(DialogKit.Text(nameof(RouterUninstallViewModel.LeftText), new Thickness(0, 0, 0, 6)).Show(nameof(RouterUninstallViewModel.HasLeft)));
        done.Children.Add(DialogKit.Hint("Поставить DpiShell снова можно в любой момент — «Установить на роутер».", new Thickness(0, 4, 0, 0)));
        panel.Children.Add(done);

        panel.Children.Add(DialogKit.Text(nameof(RouterUninstallViewModel.ErrorText), new Thickness(0, 8, 0, 4), "Danger").Show(nameof(RouterUninstallViewModel.HasError)));
        panel.Children.Add(DialogKit.Details(nameof(RouterUninstallViewModel.Details), nameof(RouterUninstallViewModel.HasDetails)));

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var remove = DialogKit.Button("Удалить").Enable(nameof(RouterUninstallViewModel.CanRemove)).Show(nameof(RouterUninstallViewModel.IsConfirm));
        if (Application.Current?.TryFindResource("DangerButton") is Style danger) remove.Style = danger;
        var reinstall = DialogKit.Button("Установить заново", primary: true).Show(nameof(RouterUninstallViewModel.IsDone));
        var cancel = DialogKit.Button("Отмена", cancel: true).Show(nameof(RouterUninstallViewModel.IsConfirm));
        cancel.IsDefault = true;   // Enter — отмена: удаление не в один клик
        var close = DialogKit.Button("Закрыть").Show(nameof(RouterUninstallViewModel.IsDone));
        foreach (var b in new[] { remove, reinstall, cancel, close }) buttons.Children.Add(b);
        panel.Children.Add(buttons);
        w.Content = panel;

        remove.Click += async (_, _) =>
        {
            try { await vm.UninstallAsync(password.Password, noPassword.IsChecked == true); }
            catch (Exception ex) { vm.Router.Say(Texts.Friendly(ex)); }
            password.Clear();
        };
        reinstall.Click += (_, _) => { w.Close(); vm.Router.InstallCmd?.Execute(null); };
        close.Click += (_, _) => w.Close();
        cancel.Click += (_, _) => w.Close();
        w.Closing += (_, e) =>
        {
            if (!vm.Working) return;
            e.Cancel = true;
            vm.Router.Say("Подождите: удаление ещё идёт");
        };
        w.Closed += (_, _) => password.Clear();
        return w;
    }
}

/// <summary>«Ключ роутера изменился»: «Доверять новому» — только осознанно, по умолчанию (Enter, Esc) — «Отмена».</summary>
public static class HostKeyDialog
{
    public static bool Ask(string text)
    {
        var owner = Application.Current?.MainWindow is { IsVisible: true } m ? m : null;
        var (w, result) = Create(text, owner);
        w.ShowDialog();
        return result();
    }

    public static (Window Window, Func<bool> Trusted) Create(string text, Window? owner)
    {
        bool trusted = false;
        var w = DialogKit.Window("Ключ роутера изменился", text, owner);
        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 440 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var trust = DialogKit.Button("Доверять новому");
        var cancel = DialogKit.Button("Отмена", primary: true, cancel: true);
        cancel.IsDefault = true;
        trust.Click += (_, _) => { trusted = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();
        buttons.Children.Add(trust);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        w.Content = panel;
        return (w, () => trusted);
    }
}
