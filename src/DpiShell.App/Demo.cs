using System.Windows;
using System.Windows.Media.Effects;

namespace DpiShell.App;

/// <summary>
/// Демо-режим окна — только для снимков в README (тест-съёмщик <c>DemoScreenshots</c>, docs/dev/building.md). Из программы включить его нельзя:
/// ни кнопки, ни ключа запуска, ни настройки; <see cref="Active"/> выставляет только тест, который заодно подменяет всё, что окно читает с машины.
/// Личное в окне помечено <c>local:Demo.Sensitive="True"</c>: в демо-режиме поверх выдуманного значения ложится размытие
/// (видно, что данные есть, но не какие). Без демо-режима пометка ничего не делает.
/// </summary>
public static class Demo
{
    /// <summary>Включено только в тесте-съёмщике. Ставится до создания окна.</summary>
    public static bool Active { get; set; }

    public static readonly DependencyProperty SensitiveProperty =
        DependencyProperty.RegisterAttached("Sensitive", typeof(bool), typeof(Demo), new PropertyMetadata(false, OnSensitiveChanged));

    public static bool GetSensitive(DependencyObject d) => (bool)d.GetValue(SensitiveProperty);
    public static void SetSensitive(DependencyObject d, bool value) => d.SetValue(SensitiveProperty, value);

    static void OnSensitiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= Apply;
        if ((bool)e.NewValue)
        {
            fe.Loaded += Apply;
            if (fe.IsLoaded) Apply(fe, null!);   // пометку поставил триггер шаблона уже после загрузки
        }
        else if (fe.Effect is BlurEffect) fe.Effect = null;
    }

    // Размывается по Loaded: элементы шаблонов (строки списков) создаются позже окна и тоже проходят через Loaded.
    static void Apply(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && Active && GetSensitive(fe) && fe.Effect is null)
            fe.Effect = new BlurEffect { Radius = 6, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality };
    }

    /// <summary>Что показать вместо настоящего пути: в демо-режиме — выдуманный, иначе как есть.</summary>
    public static string Path(string real, string fake) => Active ? fake : real;
}
