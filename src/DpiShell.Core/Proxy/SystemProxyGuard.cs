namespace DpiShell.Core;

/// <summary>
/// Защита тестов: настоящий доступ к системному прокси (реестр Internet Settings, WinHTTP, InternetSetOption) под тестом — исключение.
/// Тестовый проект включает <see cref="Forbidden"/> при загрузке; тестам остаются подмены в памяти.
/// </summary>
public static class SystemProxyGuard
{
    public static bool Forbidden { get; set; }

    public static void Check(string what)
    {
        if (Forbidden) throw new InvalidOperationException("Настоящий системный прокси в тесте — подмените реестр/WinHTTP/уведомление: " + what);
    }
}
