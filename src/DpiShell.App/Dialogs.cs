using System.Windows;

namespace DpiShell.App;

/// <summary>
/// Единая точка для MessageBox (аудит уведомлений, №16). Окно видно — диалог принадлежит ему (по центру окна, не уходит за него).
/// Окна нет (трей, свёрнуто, старт) — диалог над всеми окнами через невидимый якорь: иначе вопрос из меню трея оказывался
/// за чужими окнами, а свёрнутый владелец уводил его за экран. Под тестами — <see cref="UiGuard"/> до любого окна.
/// </summary>
public static class Dialogs
{
    /// <summary>Владелец диалога: окно показано и не свёрнуто; иначе null (диалог выводится поверх всех окон).</summary>
    public static Window? OwnerFor(Window? main) =>
        main is { IsVisible: true } && main.WindowState != WindowState.Minimized ? main : null;

    /// <summary>Владелец для диалога сейчас: главное окно программы, если его видно.</summary>
    public static Window? CurrentOwner => OwnerFor(Application.Current?.MainWindow);

    public static MessageBoxResult Show(string text, MessageBoxButton buttons, MessageBoxImage image,
        MessageBoxResult byDefault = MessageBoxResult.None, Window? owner = null)
    {
        UiGuard.Check(text);
        owner ??= CurrentOwner;
        if (owner is not null) return MessageBox.Show(owner, text, "DpiShell", buttons, image, byDefault);
        // Окна не видно: якорь поверх всех окон, прозрачный и без значка на панели задач; диалог, принадлежащий ему, — тоже поверх.
        var anchor = new Window
        {
            Title = "DpiShell",
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Width = 1,
            Height = 1,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        try
        {
            anchor.Show();
            anchor.Activate();
            return MessageBox.Show(anchor, text, "DpiShell", buttons, image, byDefault);
        }
        finally { anchor.Close(); }
    }

    /// <summary>Да/Нет; по умолчанию — «Нет».</summary>
    public static bool YesNo(string text, MessageBoxImage image = MessageBoxImage.Warning, MessageBoxResult byDefault = MessageBoxResult.No, Window? owner = null) =>
        Show(text, MessageBoxButton.YesNo, image, byDefault, owner) == MessageBoxResult.Yes;

    /// <summary>Сообщение с одной кнопкой «ОК».</summary>
    public static void Tell(string text, MessageBoxImage image) => Show(text, MessageBoxButton.OK, image);
}
