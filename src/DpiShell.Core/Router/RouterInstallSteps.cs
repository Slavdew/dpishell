namespace DpiShell.Core.Router;

/// <summary>Шаг установки на роутер — код, а не текст: окно ставит отметки по нему, а подписи берёт из языка.</summary>
public enum RouterInstallStepCode
{
    /// <summary>Подключение, проверка роутера, туннель, загрузка ядра на роутер.</summary>
    Prepare = 0,
    /// <summary>Ключ и витрина пакетов.</summary>
    Feed = 1,
    /// <summary>Пакеты DpiShell и зависимости.</summary>
    Packages = 2,
    /// <summary>Ядро zapret на роутере.</summary>
    Core = 3,
    /// <summary>Запуск и итоговая проверка.</summary>
    Start = 4,
}

public static class RouterInstallSteps
{
    /// <summary>
    /// Строка хода из сценария install.sh на роутере («# Ключ витрины DpiShell» без «# ») → шаг. Строки сценария — протокол
    /// на русском, не язык окна; служебная строка (не шаг) — null.
    /// </summary>
    public static RouterInstallStepCode? FromScriptLine(string line) => line switch
    {
        _ when line.StartsWith("Ключ витрины", StringComparison.Ordinal) || line.StartsWith("Витрина пакетов", StringComparison.Ordinal) => RouterInstallStepCode.Feed,
        _ when line.StartsWith("Пакеты", StringComparison.Ordinal) => RouterInstallStepCode.Packages,
        _ when line.StartsWith("Ядро zapret", StringComparison.Ordinal) => RouterInstallStepCode.Core,
        "Проверка" => RouterInstallStepCode.Start,
        _ => null,
    };
}
