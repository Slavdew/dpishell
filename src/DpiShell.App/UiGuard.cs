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

    /// <summary>Открытие ссылки, системной страницы или папки под тестом — ошибка: иначе у владельца открывается браузер или Проводник.</summary>
    public static void CheckOpen(string target)
    {
        if (!Forbidden) return;
        Hits.Add(target);
        throw new UiGuardException("Настоящее открытие ссылки или папки в тесте — подмените LinkOpener/OpenUrl: " + target);
    }

    /// <summary>Попытки открытия под тестом: команды окна могут поймать исключение, а тестовый проект проверяет этот список в конце прогона.</summary>
    public static System.Collections.Concurrent.ConcurrentBag<string> Hits { get; } = [];
}

/// <summary>Свой тип: обработчики ошибок открытия (Win32Exception/InvalidOperationException) его не глотают.</summary>
public sealed class UiGuardException(string message) : Exception(message);
