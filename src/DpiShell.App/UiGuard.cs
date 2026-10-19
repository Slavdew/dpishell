namespace DpiShell.App;

/// <summary>Защита тестов: настоящий диалог под тестом — ошибка, а не окно на рабочем столе владельца.</summary>
public static class UiGuard
{
    /// <summary>Включает тестовый проект при загрузке.</summary>
    public static bool Forbidden { get; set; }

    public static void Check(string text)
    {
        if (Forbidden) throw new InvalidOperationException("Настоящий диалог в тесте — подмените Confirm/Ask: " + text);
    }
}
