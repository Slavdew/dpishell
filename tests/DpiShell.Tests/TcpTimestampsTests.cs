using DpiShell.Core;

namespace DpiShell.Tests;

public sealed class TcpTimestampsTests
{
    /// <summary>Только чтение: netsh show global работает без администратора и на русской Windows отдаёт enabled/disabled.</summary>
    [Fact]
    public void IsEnabled_ReadsStateOnAnyLanguage() => Assert.NotNull(TcpTimestamps.IsEnabled());
}
