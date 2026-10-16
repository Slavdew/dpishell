using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>Только чтение служб Windows (sc query, реестр) — без прав администратора, ничего не меняет.</summary>
public sealed class ServiceQueryTests
{
    // Служба есть на любой Windows и всегда запущена; на русской Windows sc пишет «Состояние», а не STATE.
    [Fact]
    public void Query_RunningSystemService_RecognizedOnAnyLanguage() =>
        Assert.Equal(SvcState.Running, ServiceManager.Query("EventLog"));

    [Fact]
    public void Query_Missing_NotInstalled() =>
        Assert.Equal(SvcState.NotInstalled, ServiceManager.Query("DpiShellNoSuchService_" + Guid.NewGuid().ToString("N")[..8]));

    [Fact]
    public void QueryBinPath_FromRegistry() =>
        Assert.Contains("svchost", ServiceManager.QueryBinPath("EventLog") ?? "", StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void Sc_OutputReadable_NotMojibake()
    {
        var (_, o) = ServiceManager.Sc("query", "EventLog");
        Assert.DoesNotContain('�', o);   // символ-замена появляется при неверной кодировке
        Assert.Contains("RUNNING", o);
    }
}
