using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Блок «Игровой режим» (Выкл / Свои профили / Общий), раскрываемые подводные камни и импорт профиля в окне, на русском и английском,
/// в светлой и тёмной теме (снимки при DPISHELL_SNAPSHOTS: games-*-&lt;язык&gt;-&lt;тема&gt;.png). Ссылки не открываются, диалогов нет.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class GameProfilesWindowTests : IDisposable
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
        for (int i = 0; i < 400 && !cond(); i++) { Pump(); Thread.Sleep(15); }
        if (!cond()) throw new Exception("Не дождались: " + what);
    }

    [Theory]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    [InlineData("en", false)]
    [InlineData("en", true)]
    public void Game_mode_block_and_import_bind_and_snap(string lang, bool dark)
    {
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        if (snapDir is not null) Directory.CreateDirectory(snapDir);
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(dark ? ThemeManager.Dark : ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var f = new HelperFixture(backend: PcHelper.Over(shell));
            var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
            var vm = TestWindow.Model(shell, link, TestWindow.Settings(shell));
            vm.ApplyHelperData();
            vm.Apps.OpenUrl = _ => { };
            vm.Net.OpenUrl = _ => { };
            var theme = dark ? "dark" : "light";

            var w = Open(vm);
            w.Height = 1000;
            SetProp(vm, "IsLoading", false);
            var tabs = Find<TabControl>(w)!;
            void Shot(string name)
            {
                CyrillicScan.CheckIfEn(w, name);
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{name}-{lang}-{theme}.png"));
            }
            void Show(FrameworkElement e) { e.BringIntoView(); Pump(); Pump(); }

            vm.GoTo(Pages.Apps);
            PumpUntil(() => vm.Apps.Variants.Count > 0, "варианты параметров от помощника");
            // Профилей нет — «Свои профили» серая, с подсказкой почему.
            RadioButton ProfilesRadio() => Find<RadioButton>(tabs, r => r.IsVisible && r.Content as string == L.T("apps.game.mode.profiles"))!;
            Assert.False(ProfilesRadio().IsEnabled);
            Assert.Equal(L.T("apps.game.mode.profiles_none"), ProfilesRadio().ToolTip as string);
            Assert.True(ToolTipService.GetShowOnDisabled(ProfilesRadio()));

            // Профиль из импорта (образец форка: TCP 12001, 79 байт, 00 00 04 4D): сохраняется и становится «Свои профили».
            vm.Apps.ImportTitle = "Mistfall Hunter";
            vm.Apps.ImportFilter = "outbound and tcp.DstPort = 12001 and tcp.PayloadLength = 79 and tcp.Payload32[0] = 0x0000044d";
            vm.Apps.ImportParams = "--filter-tcp=12001 --dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-fooling=ts --dpi-desync-repeats=8";
            vm.Apps.ImportOpen = true;
            Pump();
            Show(Find<Expander>(tabs, e => e.IsVisible && e.Name == "ImportSection")!);
            Shot("games-import");
            WaitUi(vm.Apps.ImportAsync());
            Assert.True(vm.Apps.HasProfiles, "msg=" + vm.Apps.ImportMessage + " main=" + vm.Message + " busy=" + vm.Busy);
            Assert.False(vm.Apps.ImportFailed);
            PumpUntil(() => Find<TextBox>(tabs, t => t.IsVisible && t.Text.Contains(L.T("apps.profile.sig_mark"))) is not null, "профиль в списке");
            WaitUi(link.FetchAsync(full: false));
            vm.Net.Refresh();
            Assert.True(vm.Net.GameProfiles);
            Pump();
            Assert.True(ProfilesRadio().IsEnabled);
            Assert.Equal(L.T("apps.game.mode.profiles_tip"), ProfilesRadio().ToolTip as string);

            // Отказ по лишнему: сообщение с причиной, ничего не записано.
            vm.Apps.ImportFilter = "outbound and tcp.DstPort = 1 and tcp.Syn = 1";
            vm.Apps.ImportParams = "--filter-tcp=1 --dpi-desync=fake";
            WaitUi(vm.Apps.ImportAsync());
            Assert.True(vm.Apps.ImportFailed);
            Assert.Single(vm.Apps.Profiles);
            Pump();
            Show(Find<Expander>(tabs, e => e.IsVisible && e.Name == "ImportSection")!);
            Shot("games-import-refused");

            // Блок режима: «Свои профили» с раскрытыми подводными камнями.
            var pit = Find<Expander>(tabs, e => e.IsVisible && e.Header as string == L.T("apps.game.pit.header"))!;
            pit.IsExpanded = true;
            Show(pit);
            Shot("games-mode-profiles");
            Assert.NotNull(Find<RadioButton>(tabs, r => r.IsVisible && r.Content as string == L.T("apps.game.mode.general")));

            // «Общий»: широкий режим, короткое предупреждение и четыре пункта.
            WaitUi(vm.Net.SetGameModeAsync(GameMode.TcpUdp));
            Pump();
            Show(pit);
            Shot("games-mode-general");

            WaitUi(vm.Net.SetGameModeAsync(GameMode.Off));
            Pump();
            Show(pit);
            Shot("games-mode-off");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
