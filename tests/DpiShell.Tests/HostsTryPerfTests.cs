using System.Diagnostics;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>Примерка на 500 строк (предел вставки) не вешает окно: показ таблицы и раскладка — быстро (жалоба владельца 29.10).</summary>
public sealed class HostsTryPerfTests
{
    [Fact]
    public void Try_500Rows_ShowsFast()
    {
        using var tmp = new TempDir();
        long ms = 0;
        RunUi(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            var vm = TestWindow.Model(new Shell(new AppPaths(tmp.Sub("root"), Repo.Data)));
            var w = Open(vm);
            vm.GoTo(NetPages.Hosts);
            Pump();
            var live = new EndpointCheck(EndpointOutcome.Ok, 0.2, "HTTP 200");
            var rows = Enumerable.Range(0, HostsPaste.MaxEntries).Select(i => i % 3 == 0
                ? new HostsTryRow($"s{i}.example.com", "0.0.0.0", HostsTryVerdict.Blocks, null, null, DnsMatch.Unknown, DnsState.Fails, "")
                : new HostsTryRow($"s{i}.example.com", "203.0.113." + (i % 250), HostsTryVerdict.Helps, live, "EXAMPLE", DnsMatch.Differs, DnsState.Fails, "")).ToList();
            var sw = Stopwatch.StartNew();
            vm.Hosts.ShowTry(new HostsTryReport(rows, 0, false, rows.Count, DateTime.Now));
            Pump();
            w.UpdateLayout();
            ms = sw.ElapsedMilliseconds;
            Assert.Equal(rows.Count, vm.Hosts.TryRows.Count);
            Close(w);
        });
        Console.WriteLine($"ShowTry 500: {ms} мс");
        // Без виртуализации 5000 строк — больше 10 с; запас — на полный прогон, где тесты идут параллельно.
        Assert.True(ms < 5000, $"показ {HostsPaste.MaxEntries} строк — {ms} мс");
    }
}
