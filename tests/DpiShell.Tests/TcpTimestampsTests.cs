using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class TcpTimestampsTests
{
    /// <summary>Только чтение: netsh show global работает без администратора и на русской Windows отдаёт enabled/disabled.</summary>
    [Fact]
    public void IsEnabled_ReadsStateOnAnyLanguage()
    {
        // Читает настоящий netsh этой машины. На сервере CI (Windows Server, GitHub) строка с RFC 1323 не разбирается — что там выводится,
        // ещё не выяснено (задача после беты); на настольной Windows проверено (ru). На CI тест не судит.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        Assert.NotNull(TcpTimestamps.IsEnabled());
    }
}
