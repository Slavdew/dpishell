using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Вкладки «Починить адреса», «DNS», «Прокси», «Диагностика» на русском и английском: привязки без ошибок,
/// ни одна строка не осталась ключом. Снимки (DPISHELL_SNAPSHOTS): lang-&lt;вкладка&gt;-&lt;ru|en&gt;.png — окно высокое, разделы раскрыты.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangTabsHostsDnsProxyDiagTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Tabs_bind_and_snap_in_language(string lang)
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            Fill(vm, shell);
            FillProxy(vm);
            // Тексты «Диагностики», которые окно строит само по ответам помощника: здесь — как строит их код, а не русские литералы заполнения.
            SetProp(vm, "ServiceText", L.T("diag.service.with_strategy", new { state = Texts.Svc(SvcState.Running), strategy = "ALT11" }));
            SetProp(vm, "DriverText", Texts.Svc(SvcState.Running));
            SetProp(vm, "CoreText", L.T("diag.core.changed", new { files = "winws.exe" }));
            SetProp(vm, "DnsSummary", L.T("dns.summary.spoofed"));
            SetProp(vm, "DiagUpdated", L.T("diag.updated", new { time = L.Time(new DateTime(2026, 10, 20, 14, 30, 0)) }));
            vm.Findings.Clear();
            vm.Findings.Add(new(true, L.T("diag.find.bfe.title"), L.T("diag.find.ok_running")));
            vm.Findings.Add(new(false, L.T("diag.find.proxy.title"), SystemChecks.ProxyFinding((true, "127.0.0.1:8888")).Text, SystemFinding.GoProxy));
            vm.Findings.Add(new(false, L.T("diag.find.folder.title"), SystemChecks.PathProblem(@"C:\Users\Иван\OneDrive\DpiShell") ?? ""));
            vm.ConflictList.Clear();
            vm.ConflictList.Add(L.T("diag.conflict.line", new { name = L.T("diag.conflict.name_service_strategy", new { owner = L.T("diag.conflict.owner_flowseal"), service = "zapret", strategy = "general (ALT11)" }), detail = L.T("diag.conflict.stop_flowseal_service") }));
            vm.Hosts.PasteText = "140.82.121.3 github.com";

            var w = Open(vm);
            w.Width = 1100;
            w.Height = 2600;
            SetProp(vm, "IsLoading", false);
            Pump();
            var tabs = Find<TabControl>(w)!;
            foreach (var (page, name) in new[] { (Pages.Hosts, "hosts"), (Pages.Dns, "dns"), (Pages.Proxy, "proxy"), (Pages.Diag, "diag") })
            {
                vm.Page = page;
                Pump();
                vm.DnsDetailsOpen = true;
                foreach (var e in FindAll<Expander>(tabs, x => x.IsVisible)) e.IsExpanded = true;
                Pump();
                // Ни одной строки-ключа вида «hosts.try.title» на вкладке.
                var raw = FindAll<TextBlock>(tabs, t => t.IsVisible && System.Text.RegularExpressions.Regex.IsMatch(t.Text, @"^(hosts|dns|proxy|diag|common)\.[a-z_.]+$")).Select(t => t.Text).ToList();
                if (raw.Count > 0) throw new Exception($"{name}/{lang}: показаны ключи: {string.Join(", ", raw)}");
                CyrillicScan.CheckIfEn(tabs, name);
                if (snapDir is not null)
                {
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"lang-{name}-{lang}.png"));
                    // Длинная вкладка не умещается в окно — ещё и низ.
                    if (Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } sv)
                    {
                        sv.ScrollToEnd();
                        Pump();
                        Snap(w, Path.Combine(snapDir, $"lang-{name}-{lang}-end.png"));
                        sv.ScrollToHome();
                        Pump();
                    }
                }
            }
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
