using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// Смена языка не сбрасывает выбор в выпадающих списках, названия которых зависят от языка (аудит перед бетой, Б-1):
/// «Свои настройки для сайтов» — стратегия строки, роутер — расписание проверки, DNS — вариант. После «ru → en → ru»
/// значения на месте и «Сохранить» отправляет те же стратегии.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangComboKeepTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    static void WaitUi(Task t)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        t.ContinueWith(_ => frame.Continue = false, TaskScheduler.Default);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        t.GetAwaiter().GetResult();
    }

    [Fact]
    public void Sites_advanced_rows_keep_strategies_after_language_switch_and_save_sends_the_same()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set("ru");
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var vm = TestWindow.Model(shell);
            var a = vm.AdvancedPc;
            var ids = shell.Catalog.Strategies.Take(3).Select(s => s.Id).ToList();
            a.Strategies = () => [.. shell.Catalog.Strategies.Select(s => new AdvancedChoice(s.Id, s.Title))];
            AdvancedState? sent = null;
            a.Save = (s, _) => { sent = s; return Task.FromResult(new AdvancedSaveResult(s, null)); };
            var w = Open(vm);
            try
            {
                w.Height = 900;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(Pages.Sites);
                Pump();
                a.Load = () => Task.FromResult<AdvancedState?>(new(true, "lists",
                [
                    new(["youtube.com"], "auto", ids[1]),
                    new(["bank.example"], "direct", null),
                    new(["games.example"], ids[0], ids[0]),
                ]));
                WaitUi(a.RefreshAsync(force: true));
                Pump();
                var tabs = Find<TabControl>(w)!;

                string[] Shown() => [.. FindAll<ComboBox>(tabs, c => c.IsVisible && c.Items.Count >= 3 && c.DataContext is AdvancedRowVm).Select(c => c.SelectedValue as string ?? "<пусто>")];
                string[] want = ["auto", "direct", ids[0]];
                Assert.Equal(want, Shown());

                foreach (var lang in new[] { "en", "ru", "en", "ru" })
                {
                    L.Set(lang);
                    Pump();
                    Assert.Equal(want, Shown());
                    Assert.Equal(want, a.Rows.Select(r => r.Strategy));
                    Assert.False(a.Dirty, "смена языка не должна считаться правкой");
                }

                // «Сохранить» отправляет те же стратегии (правка текста строки — чтобы было что сохранять).
                a.Rows[0].DomainsText = "youtube.com, example.org";
                WaitUi(a.ApplyAsync());
                Assert.NotNull(sent);
                Assert.Equal(want, sent!.Rows.Select(r => r.Strategy));
            }
            finally { Close(w); }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Dns_picker_and_router_schedule_keep_choice_after_language_switch()
    {
        using var tmp = new TempDir();
        using var fake = new FakeRouter();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set("ru");
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var vm = TestWindow.Model(shell);
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            vm.Router.Confirm = _ => true;
            vm.Router.AppVersion = new Version(1, 0, 0);
            var said = new List<string>();
            vm.Router.Say = s => said.Add(s);
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
            var w = Open(vm);
            try
            {
                w.Height = 1000;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(NetPages.Dns);
                Pump();
                var pick = vm.PcDns.Options.First(o => o.Id != vm.PcDns.Selected?.Id && o.Ips.Count > 0).Id;
                vm.PcDns.Selected = vm.PcDns.Options.First(o => o.Id == pick);
                Pump();
                string? Dns() => Find<ComboBox>(w, c => c.IsVisible && ReferenceEquals(c.ItemsSource, vm.PcDns.Options))?.SelectedItem is DnsOption o ? o.Id : null;
                Assert.Equal(pick, Dns());

                vm.GoTo(Pages.Router);
                for (int i = 0; i < 400 && vm.Router.Busy; i++) { Pump(); Thread.Sleep(15); }
                WaitUi(vm.Router.SetScheduleAsync(true, 6));
                Pump();
                string? Sched() => Find<ComboBox>(w, c => c.IsVisible && c.SelectedValuePath == "Hours")?.SelectedValue is double h ? h.ToString() : null;
                Assert.True(vm.Router.ScheduleHours == 6, "VM: " + vm.Router.ScheduleHours + " / " + fake.ScheduleHours + " / " + string.Join(" | ", said));
                Assert.Equal("6", Sched());

                foreach (var lang in new[] { "en", "ru" })
                {
                    L.Set(lang);
                    Pump();
                    Assert.Equal(pick, vm.PcDns.Selected?.Id);
                    Assert.Equal(6, vm.Router.ScheduleHours);
                    Assert.Equal("6", Sched());
                    vm.GoTo(NetPages.Dns);
                    Pump();
                    Assert.Equal(pick, Dns());
                    vm.GoTo(Pages.Router);
                    Pump();
                }
            }
            finally { Close(w); }
        });
        Assert.Empty(errors);
    }
}
