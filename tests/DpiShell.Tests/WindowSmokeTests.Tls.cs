using System.Security.Authentication;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Windows 10 (без клиентского TLS 1.3): в пояснении к подбору — строка «TLS 1.3 проверяется только на Windows 11 — здесь по TLS 1.2»,
    /// в «Диагностике» (за «Подробнее») и в отчёте — проверка по TLS 1.2.
    /// </summary>
    [Fact]
    public void Windows10_Tls12Only_Shown_In_SelectSummary_And_Diag()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            using var tls = TlsSupport.Assume(tls13: false);
            ThemeManager.Apply(ThemeManager.Light);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            var groups = shell.DefaultTargets.Groups.Where(g => g.Id is "youtube" or "discord").ToList();
            var strategy = shell.Catalog.Strategies.First();
            var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
                new TargetVerdict(g.Id, t, true, 0.4, [new ProbeResult(t, SslProtocols.Tls12, ProbeOutcome.Ok, 0.4, 0, "HTTP 200")]))).ToList();
            var report = new SelectionReport([], groups, [Selector.Score(strategy, groups, verdicts)], false);

            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.GoTo(Pages.Select);
            typeof(MainViewModel).GetMethod("ShowReport", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(vm, [report]);
            Pump();
            var tabs = Find<TabControl>(w)!;
            var note = L.T("select.note.tls12");
            Assert.Contains(note, vm.ResultHint);
            Assert.True(VisibleText(tabs, t => t.Text.Contains(note)), "В пояснении к подбору нет строки про TLS 1.2");

            vm.GoTo(Pages.Diag);
            Pump();
            foreach (var e in FindAll<Expander>(tabs, x => x.IsVisible)) e.IsExpanded = true;
            Pump();
            Assert.Equal(L.T("diag.tls.only12"), vm.TlsText);
            Assert.True(VisibleText(tabs, t => t.Text == L.T("diag.tls.only12")), "В «Диагностике» нет строки о проверке по TLS 1.2");
            Close(w);
        });
        Assert.Empty(errors);
    }
}
