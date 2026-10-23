using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    const string FailingJson = """[ { "code": "strategy_failing", "text": "Стратегия не проходит проверку — нужен подбор", "key": "web.warn.strategy_failing", "params": { "strategy": "alt10" } } ]""";

    /// <summary>Строка видна целиком — TextBlock (в том числе из Run, как пункты панели «Роутер: …») или поле только для чтения.</summary>
    static bool Shows(DependencyObject root, Func<string, bool> m) =>
        FindAll<TextBlock>(root, t => t.IsVisible).Any(t => m(t.Inlines.Count > 0 ? string.Concat(t.Inlines.OfType<Run>().Select(r => r.Text)) : t.Text))
        || FindAll<TextBox>(root, b => b.IsVisible && b.IsReadOnly).Any(b => m(b.Text));

    /// <summary>
    /// Ручная проверка днём не прошла (YouTube), таймер проверок выключен, сейчас вечер: итог старше часа — не текущая ошибка.
    /// «Роутер: …» на главной его не поднимает; вкладка «Роутер» — серым «проверено 5 ч назад — открывалось не всё» и «Проверить»;
    /// strategy_failing, который держится на этом итоге, — не строкой предупреждения.
    /// </summary>
    [Fact]
    public void Router_StaleFailedCheck_TimerOff_NotRaised()
    {
        RunStaleCheck(timerOn: false, fresh: false);
    }

    /// <summary>Таймер включён, итог старше часа: в «Роутер: …» — серым с возрастом и «Проверить сейчас» (check_now); после проверки — свежий итог.</summary>
    [Fact]
    public void Router_StaleFailedCheck_TimerOn_GreyWithCheckNow()
    {
        RunStaleCheck(timerOn: true, fresh: false);
    }

    /// <summary>Свежая (меньше часа) неудачная — как раньше: ошибка «YouTube: не открывается через роутер · проверено в …» и strategy_failing.</summary>
    [Fact]
    public void Router_FreshFailedCheck_ShownAsError()
    {
        RunStaleCheck(timerOn: false, fresh: true);
    }

    static void RunStaleCheck(bool timerOn, bool fresh)
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter { WarningsJson = FailingJson, LastCheckOk = false, LastCheckSource = "manual", ScheduleEnabled = timerOn }.As020();
            var now = fake.CheckedAt + (fresh ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(5) + TimeSpan.FromMinutes(50));
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            var vm = TestWindow.Model(shell);
            vm.Router.AddressAuto = false;
            vm.Router.CustomAddress = fake.Address;
            vm.Router.PcTimestamps = () => true;
            vm.Router.PollInterval = TimeSpan.FromMilliseconds(10);
            vm.Router.UtcNow = () => now;   // часы подставные: «сейчас» — через 10 мин или почти 6 ч после проверки
            Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            var tabs = Find<TabControl>(w)!;

            var at = RouterTexts.Time(fake.CheckedAt);
            var closed = L.T("home.r.site.closed_at", new { when = L.T("router.home.checked", new { time = at }) });
            var staleBad = L.T("router.home.checked_bad_ago", new { ago = L.T("router.check.age_h", new { n = 5 }) });
            var failing = L.T("web.warn.strategy_failing");

            // Главная → значок роутера: панель «Роутер: …» («что мешает»).
            vm.GoTo(Pages.Home);
            vm.Panel = HomePanel.Router;
            Pump();
            PumpUntil(() => !vm.Router.Busy, "diag роутера для панели не дочитался");
            Pump();
            var home = ((TabItem)tabs.Items[Pages.Home]).Content as DependencyObject;
            if (fresh)
            {
                if (!Shows(home!, s => s.Contains(closed))) throw new Exception("Свежая неудача: нет «YouTube: " + closed + "» в «Роутер: …»");
                if (!vm.RouterItems.Any(i => i.State == Health.Error && i.Label == "YouTube")) throw new Exception("Свежая неудача — не ошибкой");
            }
            else
            {
                if (Shows(home!, s => s.Contains(L.T("home.r.site.closed")))) throw new Exception("Старый итог показан действующей ошибкой «не открывается через роутер»");
                if (vm.RouterItems.Any(i => i.State == Health.Error && i.Label == "YouTube")) throw new Exception("Старый итог — ошибкой в «Роутер: …»");
                if (Shows(home!, s => s == failing || s.Contains(failing))) throw new Exception("strategy_failing по старому итогу — в «Роутер: …»");
                if (timerOn)
                {
                    if (!Shows(home!, s => s.Contains(staleBad))) throw new Exception("Таймер включён: нет серого «" + staleBad + "»");
                    var row = vm.RouterItems.Single(i => i.Value == staleBad);
                    if (row.State != Health.None) throw new Exception("Старый итог не серым: " + row.State);
                    var btn = Find<Button>(home!, b => b.IsVisible && b.Content as string == L.T("home.check_now") && Equals(b.DataContext, row))
                              ?? throw new Exception("У старого итога нет «Проверить сейчас»");
                    // «Проверить сейчас» — check_now: новый итог прошёл и он свежий.
                    fake.LastCheckOk = true;
                    now = fake.CheckedAt + TimeSpan.FromMinutes(2);
                    btn.Command.Execute(btn.CommandParameter);
                    PumpUntil(() => vm.Router.Status?.LastCheck?.Ok == true && !vm.Router.Busy, "check_now не дал нового итога");
                    Pump();
                    if (!vm.RouterItems.Any(i => i.State == Health.Ok && i.Label == L.T("home.r.sites.label"))) throw new Exception("После «Проверить сейчас» нет «Сайты: открываются»");
                    if (Shows(home!, s => s.Contains(staleBad))) throw new Exception("После проверки остался старый итог");
                    fake.LastCheckOk = false;
                    now = fake.CheckedAt + TimeSpan.FromHours(5) + TimeSpan.FromMinutes(50);
                    WaitUi(vm.Router.RefreshAsync());
                    Pump();
                }
                else
                {
                    if (Shows(home!, s => s.Contains(staleBad))) throw new Exception("Таймер выключен: старый провал поднят в «Роутер: …»");
                    if (vm.RouterItems.Any(i => i.Label == L.T("home.r.sites.label"))) throw new Exception("Таймер выключен: пункт «Сайты» по старому итогу");
                }
            }

            // Вкладка «Роутер»: карточка проверки и предупреждения.
            vm.Panel = HomePanel.None;
            vm.GoTo(Pages.Router);
            Pump();
            var page = ((TabItem)tabs.Items[Pages.Router]).Content as DependencyObject;
            if (fresh)
            {
                var bad = L.T("router.home.checked_bad", new { time = at });
                if (!Shows(page!, s => s.Contains(bad))) throw new Exception("Свежая неудача: нет «" + bad + "» на вкладке");
                if (!Shows(page!, s => s == failing)) throw new Exception("Свежая неудача: нет strategy_failing на вкладке");
            }
            else
            {
                if (!Shows(page!, s => s.Contains(staleBad))) throw new Exception("На вкладке нет серого «" + staleBad + "»");
                var detail = FindAll<TextBox>(page!, b => b.IsVisible && b.Text.Contains(staleBad)).First();
                if (detail.Style != (Style)w.FindResource("SelectableHint")) throw new Exception("Старый итог на вкладке не серым");
                if (Shows(page!, s => s == failing)) throw new Exception("strategy_failing по старому итогу — строкой предупреждения на вкладке");
                if (Find<Button>(page!, b => b.IsVisible && b.Content as string == L.T("router.check") && b.Command == vm.Router.CheckCmd) is null)
                    throw new Exception("На вкладке нет «Проверить» рядом со старым итогом");
                if (vm.Router.HomeFixText.Length > 0) throw new Exception("strategy_failing по старому итогу считается в предупреждениях: " + vm.Router.HomeFixText);
            }
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
