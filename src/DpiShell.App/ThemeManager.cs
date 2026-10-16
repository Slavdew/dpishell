using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace DpiShell.App;

/// <summary>
/// Светлая/тёмная тема: подмена словаря цветов (Themes/Light.xaml или Themes/Dark.xaml) в ресурсах приложения
/// и тёмный заголовок окна через DWM. «Как в системе» — по AppsUseLightTheme, с переключением на лету.
/// </summary>
public static class ThemeManager
{
    public const string Auto = "system", Light = "light", Dark = "dark";
    const string Marker = "ThemeIsDark";

    static string _choice = Auto;
    static bool _listening;

    public static bool IsDark { get; private set; }

    public static string Normalize(string? choice) => choice is Light or Dark ? choice : Auto;

    /// <summary>Выбор пользователя (Auto/Light/Dark) — применить сейчас.</summary>
    public static void Apply(string? choice)
    {
        _choice = Normalize(choice);
        Refresh();
    }

    /// <summary>Пересчитать тему (для «как в системе» — заново прочитать реестр) и обновить окна.</summary>
    public static void Refresh()
    {
        var app = Application.Current;
        if (app is null) return;
        var dark = _choice switch { Dark => true, Light => false, _ => SystemPrefersDark() };
        var merged = app.Resources.MergedDictionaries;
        var i = -1;
        for (int k = 0; k < merged.Count; k++)
            if (merged[k].Contains(Marker)) { i = k; break; }
        if (i < 0 || merged[i][Marker] is not bool current || current != dark)
        {
            var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/DpiShell;component/Themes/{(dark ? "Dark" : "Light")}.xaml") };
            if (i >= 0) merged[i] = dict;
            else merged.Insert(0, dict);
        }
        IsDark = dark;
        foreach (Window w in app.Windows) SetTitleBar(w);
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Следить за сменой темы Windows. Только из приложения: тестам подписка на системные события не нужна.</summary>
    public static void Listen()
    {
        if (_listening) return;
        _listening = true;
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
    }

    public static void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
    }

    static void OnSystemChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle or UserPreferenceCategory.Color)) return;
        if (_choice != Auto) return;
        // Событие приходит из служебного потока SystemEvents.
        Application.Current?.Dispatcher.BeginInvoke(Refresh);
    }

    /// <summary>Заголовок окна в цвет темы (Windows 10 20H1+ / 11). Окно без HWND пропускаем — вызовут ещё раз из SourceInitialized.</summary>
    public static void SetTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
