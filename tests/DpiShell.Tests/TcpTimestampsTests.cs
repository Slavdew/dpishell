using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class TcpTimestampsTests
{
    /// <summary>Только чтение: netsh show global работает без администратора и на русской Windows отдаёт enabled/disabled.</summary>
    [Fact]
    public void IsEnabled_ReadsStateOnAnyLanguage()
    {
        // Читает настоящий netsh этой машины. На сервере CI (Windows Server, GitHub) вывод вживую не проверить без сервера: разбор вывода
        // (в т.ч. английского, allowed) покрыт тестами Parse_* ниже, а здесь на CI тест не судит; на настольной Windows проверено (ru).
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true") return;
        Assert.NotNull(TcpTimestamps.IsEnabled());
    }

    /// <summary>Английская Windows Server / десктоп: номер RFC и значение не переводятся; на новых сборках значение allowed.</summary>
    [Theory]
    [InlineData("Querying active state...\r\n\r\nTCP Global Parameters\r\n----------------------------------------------\r\nECN Capability                      : disabled\r\nRFC 1323 Timestamps                 : enabled\r\nInitial RTO                         : 1000\r\n", true)]
    [InlineData("TCP Global Parameters\nRFC 1323 Timestamps                 : disabled\n", false)]
    [InlineData("TCP Global Parameters\nRFC 1323 Timestamps                 : allowed\n", false)]
    [InlineData("Параметры TCP\r\nМетки времени RFC 1323               : enabled\r\n", true)]
    [InlineData("rfc 1323 timestamps : ENABLED", true)]
    [InlineData("RFC 1323 Timestamps : something-new", null)]
    [InlineData("ничего похожего", null)]
    [InlineData("", null)]
    public void Parse_HandlesLanguagesAndValues(string output, bool? expected) =>
        Assert.Equal(expected, TcpTimestamps.Parse(output));
}
