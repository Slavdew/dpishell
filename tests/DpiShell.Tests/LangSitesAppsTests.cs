using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Вкладки «Сайты» и «Игры и программы» на русском и английском: привязки целы, ни одна строка не осталась ключом,
/// английский не обрезан (проверка по снимкам, DPISHELL_SNAPSHOTS): sites-&lt;язык&gt;-*.png, apps-&lt;язык&gt;-*.png.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangSitesAppsTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static void WaitUi(Task t)
    {
        var frame = new DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        Dispatcher.PushFrame(frame);
        t.GetAwaiter().GetResult();
    }

    static void PumpUntil(Func<bool> cond, string what)
    {
        for (int i = 0; i < 400 * 10 && !cond(); i++) { Pump(); Thread.Sleep(15); }
        if (!cond()) throw new Exception("Не дождались: " + what);
    }

    /// <summary>В дереве нет текста, похожего на ключ (область.что), — перевод не потерялся.</summary>
    static void NoRawKeys(DependencyObject root, string where)
    {
        var raw = new System.Text.RegularExpressions.Regex(@"^(sites|apps|common)\.[a-z_.]+$");
        foreach (var t in FindAll<TextBlock>(root, t => t.IsVisible))
            if (raw.IsMatch(t.Text)) throw new Exception($"{where}: показан ключ «{t.Text}»");
        foreach (var b in FindAll<Button>(root, b => b.IsVisible))
            if (b.Content is string s && raw.IsMatch(s)) throw new Exception($"{where}: на кнопке ключ «{s}»");
    }

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Sites_and_Apps_pages_bind_and_snap_in_both_languages(string lang)
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snapDir is not null) Directory.CreateDirectory(snapDir);
        using var tmp = new TempDir();
        using var fake = new FakeRouter { WarningsJson = "[]", AdvancedField_On = true }.As020();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var flows = new FakeFlowSource();
            var procs = new FakeProcesses(new ProcInfo(4, 0, "System"), new ProcInfo(900, 4, "chrome.exe"));
            var f = new HelperFixture(backend: PcHelper.Over(shell, flows, procs));
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var settings = TestWindow.Settings(shell);
            var vm = TestWindow.Model(shell, link, settings);
            vm.Apps.DebugOpen = true;
            vm.Apps.ListProcesses = () => [new(900, "chrome.exe", "Chrome"), new(2000, "game.exe", "Game")];
            vm.ApplyHelperData();

            var a = vm.AdvancedPc;
            var ids = shell.Catalog.Strategies.Take(3).ToList();
            a.Strategies = () => [.. shell.Catalog.Strategies.Select(s => new AdvancedChoice(s.Id, s.Title))];
            vm.Router.Advanced.Strategies = a.Strategies;
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();

            var w = Open(vm);
            w.Height = 1000;
            SetProp(vm, "IsLoading", false);
            var tabs = Find<TabControl>(w)!;
            void Top() { if (Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } sv) { sv.ScrollToHome(); Pump(); } }
            void Bottom() { if (Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } sv) { sv.ScrollToEnd(); Pump(); } }
            void Shot(string name) { CyrillicScan.CheckIfEn(w, name); if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{name}-{lang}.png")); }

            // Сайты, этот ПК: свои строки, «Остальные сайты», исключения, «Не сохранено», сообщение об ошибке.
            vm.GoTo(Pages.Sites);
            Pump();
            a.Load = () => Task.FromResult<AdvancedState?>(new(true, "lists",
            [
                new(["youtube.com", "example.org"], "auto", ids[1].Id),
                new(["bank.example"], "direct", null),
                new(["games.example", "cdn.games.example"], ids[0].Id, ids[0].Id),
            ]));
            WaitUi(a.RefreshAsync(force: true));
            vm.ExcludeText = "bank.example";
            Pump();
            NoRawKeys(tabs, "Сайты (ПК)");
            Shot("sites-pc");
            // Аудит Б-1: окно на языке (у ru — после проверки en на лету и возврата) — у каждой строки в списке «Стратегия» видно её значение.
            var combos = FindAll<ComboBox>(tabs, c => c.IsVisible && c.DataContext is AdvancedRowVm).ToList();
            Assert.Equal(3, combos.Count);
            Assert.All(combos, c => Assert.Equal(((AdvancedRowVm)c.DataContext).Strategy, c.SelectedValue as string));
            Assert.All(combos, c => Assert.NotNull(c.SelectedItem));
            a.Rows[1].DomainsText = "bank example";
            WaitUi(a.ApplyAsync());
            Pump();
            Shot("sites-pc-error");
            Bottom();
            Shot("sites-pc-end");
            Top();

            // Сайты, роутер: «Сайт не открывается», режим, свои адреса, списки.
            vm.Router.TargetsOnRouter = true;
            WaitUi(vm.Router.Advanced.RefreshAsync(force: true));
            Pump();
            NoRawKeys(tabs, "Сайты (роутер)");
            Shot("sites-router");
            Bottom();
            Shot("sites-router-end");
            Top();

            // Игры и программы: простой режим — запись, вопрос «Заработало?», итог.
            vm.GoTo(Pages.Apps);
            PumpUntil(() => vm.Apps.Variants.Count > 0, "варианты параметров от помощника");
            Button? Btn(string key) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == L.T(key));
            NoRawKeys(tabs, "Игры (покой)");
            Shot("apps-idle");
            Btn("apps.profiles.record")!.Command.Execute(null);   // карточка записи — по «Записать игру…»
            Pump();
            NoRawKeys(tabs, "Игры (карточка записи)");
            Btn("apps.start")!.Command.Execute(null);
            PumpUntil(() => vm.Apps.IsRecording, "запись");
            procs.List.Add(new ProcInfo(2000, 4, "game.exe"));
            flows.Push("udp", "203.0.113.50", 27015, 2000);
            flows.Push("tcp", "198.51.100.7", 443, 2000, FlowHint.Established);
            Task.Run(() => vm.Apps.TickAsync()).GetAwaiter().GetResult();
            Pump();
            Shot("apps-recording");
            Btn("apps.rec.done")!.Command.Execute(null);
            PumpUntil(() => vm.Apps.IsCheck, "применение и вопрос");
            Pump();
            NoRawKeys(tabs, "Игры (вопрос)");
            Shot("apps-check");
            Btn("apps.check.yes")!.Command.Execute(null);
            Pump();
            Shot("apps-done");

            // Игры и программы: «Отладка подключений» с записанным и «Дополнительно» раскрыто.
            vm.Apps.RefreshProcesses();
            Btn("apps.debug.record")!.Command.Execute(null);
            PumpUntil(() => vm.Apps.AdvRecording, "запись");
            flows.Push("udp", "203.0.113.51", 27016, 2000);
            flows.Push("udp", "192.168.1.1", 53, 2000);
            flows.Push("tcp", "198.51.100.60", 443, 900);
            Btn("apps.debug.stop")!.Command.Execute(null);
            PumpUntil(() => vm.Apps.HasRows, "список подключений");
            vm.Net.ListsOpen = true;
            Pump();
            NoRawKeys(tabs, "Игры (отладка)");
            Bottom();
            Shot("apps-debug-end");
            Top();
            if (Find<ScrollViewer>(tabs, s => s.IsVisible && s.ScrollableHeight > 0) is { } sv2) { sv2.ScrollToVerticalOffset(sv2.ScrollableHeight / 2); Pump(); }
            Shot("apps-debug-mid");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
