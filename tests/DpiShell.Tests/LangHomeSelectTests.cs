using System.IO;
using System.Security.Authentication;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// «Главная» и «Подбор» на русском и английском: состояния с настоящими текстами моделей (проверка, предупреждение про браузер, список стратегий,
/// «Сайт не открывается?», итог подбора, вставленная стратегия, окно правки). Без DPISHELL_SNAPSHOTS — просто проверка, что всё строится без ошибок привязок;
/// с ним — снимки home-*-{ru,en}.png и select-*-{ru,en}.png.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangHomeSelectTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static readonly System.Reflection.BindingFlags Priv = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;

    [Fact]
    public void Home_and_select_build_and_fit_in_ru_and_en()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        void Shot(Window w, string name)
        {
            if (snapDir is null) return;
            Directory.CreateDirectory(snapDir);
            Snap(w, Path.Combine(snapDir, name + ".png"));
        }
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            foreach (var lang in new[] { L.Ru, L.En })
            {
                L.Set(lang);
                Home(tmp.Sub("home-" + lang), lang, Shot);
                Select(tmp.Sub("select-" + lang), lang, Shot);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    static void Home(string root, string lang, Action<Window, string> shot)
    {
        var shell = new Shell(new AppPaths(root, Repo.Data));
        var f = new HelperFixture();
        f.Backend.TrackEnabled = true;
        f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, StrategyId = "alt9", Service = SvcState.Stopped };
        var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
        var vm = TestWindow.Model(shell, link);
        bool on = false;
        string? hostsIp = null;
        vm.ProbeGroups = groups => Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t =>
            new TargetVerdict(g.Id, t, g.Id == "youtube" || on, 0.3, [], g.Id == "youtube" && on
                ? new BrowserPath([System.Net.IPAddress.Parse("10.1.1.1")], [System.Net.IPAddress.Parse("142.250.74.14")], false, hostsIp, [])
                : null))).ToList());
        var w = Open(vm);
        w.Height = 700;
        SetProp(vm, "IsLoading", false);
        foreach (var g in vm.Groups) g.IsChecked = g.Id is "youtube" or "discord";
        vm.Page = Pages.Home;
        WaitUi(vm.RefreshStatusAsync());
        WaitUi(vm.CheckNowAsync());
        Pump();
        shot(w, $"home-off-{lang}");

        on = true;
        WaitUi(vm.EnableAsync());
        Pump();
        shot(w, $"home-working-{lang}");

        // «Сайт не открывается?»: адрес и ответ.
        vm.DirectOpen = true;
        vm.DirectSite = "bank.example";
        WaitUi(vm.OpenDirectAsync());
        vm.DirectOpen = true;
        Pump();
        shot(w, $"home-site-fix-{lang}");
        vm.DirectOpen = false;

        Close(w);
        Task.Run(async () => { await vm.Helper.DisposeAsync(); await f.DisposeAsync(); }).GetAwaiter().GetResult();
    }

    static void Select(string root, string lang, Action<Window, string> shot)
    {
        var shell = new Shell(new AppPaths(root, Repo.Data));
        shell.Paths.EnsureDirectories();
        foreach (var f in Directory.GetFiles(Path.Combine(Repo.Data, "fake"), "*.bin")) File.Copy(f, Path.Combine(shell.Paths.Fake, Path.GetFileName(f)), true);
        var (vm, helper) = WithPcHelper(shell, TestWindow.Settings(shell));
        try
        {
            SetProp(vm, "IsLoading", false);
            var groups = shell.DefaultTargets.Groups.Where(g => g.Id is "youtube" or "discord").ToList();
            var strategies = shell.Catalog.Strategies.Take(7).ToList();
            StrategyScore Score(Strategy s, bool breakDiscord)
            {
                var verdicts = groups.SelectMany(g => g.Targets.Select(t =>
                {
                    bool ok = !(breakDiscord && g.Id == "discord");
                    var ps = new[] { SslProtocols.Tls13, SslProtocols.Tls12 }.Select(v => new ProbeResult(t, v, ok ? ProbeOutcome.Ok : ProbeOutcome.Reset, 0.4, 0, ok ? "HTTP 200" : "")).ToList();
                    return new TargetVerdict(g.Id, t, ok, 0.4, ps);
                })).ToList();
                return DpiShell.Core.Selector.Score(s, groups, verdicts);
            }
            var w = Open(vm);
            w.Height = 700;
            vm.Page = Pages.Select;
            Pump();
            shot(w, $"select-start-{lang}");

            // Итог подбора: лучшая не открывает Discord — есть что предложить («Искать глубже»).
            var scores = strategies.Select((s, i) => Score(s, i != 0 && i != 2)).Append(Score(strategies[0], true) with { Error = "winws: не запустился" }).ToList();
            var report = new SelectionReport([], groups, scores, false);
            typeof(MainViewModel).GetMethod("ShowReport", Priv)!.Invoke(vm, [report]);
            Pump();
            shot(w, $"select-result-{lang}");

            // Вставленная стратегия: «не проверена», «Проверить».
            var roles = new Dictionary<string, string>(shell.Catalog.Strategies[7].Roles);
            vm.ReadText = () => StrategyExchange.Encode("Стратегия от друга", roles);
            vm.ToastTime = TimeSpan.FromMinutes(5);
            WaitUi(vm.PasteStrategyAsync());
            Pump();
            shot(w, $"select-pasted-{lang}");

            // Главная: список стратегий, у вставленной раскрыты параметры.
            SetProp(vm, "ShowStrategies", true);
            vm.Page = Pages.Home;
            Pump();
            var own = vm.StrategyChoices.First(c => c.Personal);
            vm.StrategyChoices.Move(vm.StrategyChoices.IndexOf(own), 0);
            own.ShowParams = true;
            Pump();
            shot(w, $"home-strategies-{lang}");
            vm.Page = Pages.Select;
            Pump();

            // Окно правки: с ошибкой.
            var editor = new StrategyEditor("Стратегия от друга", "Моя правка", roles, catalog: false, n => File.Exists(Path.Combine(Repo.Data, "fake", n))).Start();
            var host = new Window
            {
                Content = new StrategyEditView { DataContext = editor }, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false,
                FontFamily = new FontFamily("Segoe UI"), FontSize = 14,
            };
            host.SetResourceReference(Control.BackgroundProperty, "Page");
            host.SetResourceReference(Control.ForegroundProperty, "Text");
            host.Show();
            Pump();
            editor.Fields.First(f => f.Role == "general").Text += " --wf-raw=true";
            Pump();
            shot(host, $"select-edit-{lang}");
            host.Close();
            Close(w);
        }
        finally { Stop(vm, helper); }
    }
}
