using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// «Роутер» → «Совместная работа с роутером» в обеих темах: выключена по умолчанию (карточка, переключатель, подсказка, «(i)»), включена —
    /// короткий статус «Роутер ведёт YouTube и Discord, этот ПК — свои сайты (N)» и пояснение, на главной та же фраза, у строки ПК с youtube.com — «ведёт роутер»,
    /// роутер пропал — «Роутер не найден — этот ПК работает сам». В тексте нет слов «обход» и «у всех». Снимки: &lt;тема&gt;-router-joint-*.png, -home-joint, -sites-joint.
    /// </summary>
    [Fact]
    public void Router_JointWork_Card_Binds_AndSnap()
    {
        // Сбой на CI: RefreshAsync после fake.Remove() мог быть пустым вызовом (идёт другое обновление) — теперь повторяется (6e31b6d).
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
            {
                ThemeManager.Apply(theme);
                using var fake = new FakeRouter { WarningsJson = "[]" }.As020();
                fake.UpdateFieldOn = false;
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + theme), Repo.Data));
                var f = new HelperFixture();
                f.Backend.TrackEnabled = true;
                f.Backend.StatusValue = f.Backend.StatusValue with { Enabled = false, Active = false, StrategyId = "alt9", Service = SvcState.Stopped, UserTargets = ["mysite.example", "youtube.com"] };
                var link = Task.Run(f.LinkAsync).GetAwaiter().GetResult();
                var vm = TestWindow.Model(shell, link);
                // Роутер подключается до открытия окна: его события трогают окно, а ConnectAsync здесь идёт с другого потока.
                vm.Router.AddressAuto = false;
                vm.Router.CustomAddress = fake.Address;
                vm.Router.PcTimestamps = () => true;
                vm.Router.OwnMac = _ => "aa:bb:cc:dd:ee:01";
                Task.Run(() => vm.Router.ConnectAsync("secret")).GetAwaiter().GetResult();
                var w = Open(vm);
                w.Height = 900;
                SetProp(vm, "IsLoading", false);
                vm.GoTo(Pages.Router);
                Pump();
                var tabs = Find<TabControl>(w)!;
                bool Visible(Func<TextBlock, bool> m) => VisibleText(tabs, m);
                CheckBox Switch() => Find<CheckBox>(tabs, c => c.IsVisible && System.Windows.Automation.AutomationProperties.GetName(c) == L.T("router.joint.title"))
                                     ?? throw new Exception("Нет переключателя «Совместная работа с роутером»");

                // Выключена по умолчанию: карточка с заголовком, «(i)», переключатель и подсказка; статуса нет.
                if (!Visible(t => t.Text == L.T("router.joint.title"))) throw new Exception("Нет заголовка карточки");
                if (Find<InfoButton>(tabs, b => b.Topic == InfoTexts.Joint && b.IsVisible) is null) throw new Exception("Нет «(i)» у совместной работы");
                var sw = Switch();
                if (sw.IsChecked == true) throw new Exception("Совместная работа включена по умолчанию");
                if (!sw.IsEnabled) throw new Exception("Переключатель недоступен при подключённом роутере");
                if (!Visible(t => t.Text == L.T("router.joint.hint"))) throw new Exception("Нет подсказки под выключенным переключателем");
                if (Visible(t => t.Text.StartsWith("Роутер ведёт YouTube и Discord, этот ПК — свои сайты ("))) throw new Exception("Статус виден у выключенной совместной работы");
                if (snapDir is not null) { Directory.CreateDirectory(snapDir); Snap(w, Path.Combine(snapDir, $"{theme}-router-joint-off.png")); }

                // Включена: связка на роутере, ПК включается и ведёт свои сайты; youtube.com ведёт роутер.
                vm.Router.JointWanted = true;
                PumpUntil(() => vm.Router.JointStatus.StartsWith("Роутер ведёт") && vm.Router.JointAvailable, "связка не поднялась: " + vm.Router.JointStatus);
                Pump();
                if (Switch().IsChecked != true) throw new Exception("Переключатель не показывает включённую совместную работу");
                if (!Visible(t => t.Text == "Включено") || Visible(t => t.Text == "Выключите, чтобы оптимизацию вёл только роутер: на этом ПК она выключится.")) throw new Exception("Подпись включённого переключателя — не «Включено» или осталась угроза");
                if (!Visible(t => t.Text == "Роутер ведёт YouTube и Discord, этот ПК — свои сайты (1)")) throw new Exception("Нет короткого статуса");
                if (!Visible(t => t.Text.StartsWith("Ведёт роутер: youtube.com"))) throw new Exception("Нет пояснения «Ведёт роутер»");
                foreach (var t in FindAll<TextBlock>(tabs, t => t.IsVisible && t.DataContext == vm.Router && (t.Text.Contains("Совместн") || t.Text.Contains("Роутер ведёт") || t.Text.Contains("Ведёт роутер"))))
                    if (t.Text.Contains("обход", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("у всех", StringComparison.OrdinalIgnoreCase)) throw new Exception("В тексте запретное слово: " + t.Text);
                if (!vm.IsRunning) throw new Exception("Оптимизация на ПК не включилась вместе со связкой");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-joint-on.png"));
                // Оптимизацию на ПК включила связка, не «Включить»: проверка сайтов идёт сама (UX-аудит 7, п. 1; сеть — подмена). Включаем после запуска связки,
                // чтобы лишние чтения состояния не мешали самому запуску.
                vm.ProbeGroups = groups => Task.FromResult<IReadOnlyList<TargetVerdict>>(groups.SelectMany(g => g.Targets.Select(t => new TargetVerdict(g.Id, t, true, 0.3, []))).ToList());
                vm.AutoCheck = true;
                WaitUi(vm.RefreshStatusAsync());

                // Главная: в строке «Этот ПК» — что он ведёт, в строке роутера — YouTube и Discord.
                vm.GoTo(Pages.Home);
                Pump();
                // В строке — только состояние (решение владельца), что ведёт ПК — в подсказке строки.
                if (vm.PcStateTip != L.T("home.pc.state.on_joint")) throw new Exception("В подсказке строки «Этот ПК» нет фразы о совместной работе");
                if (!vm.RouterStateTip.Contains(L.T("home.router.state.joint"))) throw new Exception("В подсказке строки роутера нет «YouTube и Discord»");
                PumpUntil(() => vm.HasCheck && !vm.Checking && vm.CheckSummary.StartsWith("Открывается"), "после включения связкой проверка сайтов не пошла сама");
                Pump();
                if (!Visible(t => t.Text.StartsWith("Открывается ") && t.Text.Contains(" из "))) throw new Exception("В состоянии «Работает» нет строки «Открывается N из M»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-home-joint.png"));

                // «Сайты» → «Свои настройки для сайтов»: строка с youtube.com — «ведёт роутер».
                vm.AdvancedPc.Load = () => Task.FromResult<AdvancedState?>(new(true, "lists", [new(["youtube.com", "bank.example"], "auto", "alt9")]));
                WaitUi(vm.AdvancedPc.RefreshAsync(force: true));
                vm.AdvancedPc.RefreshHints();
                vm.GoTo(Pages.Sites);
                Pump();
                tabs = Find<TabControl>(w)!;
                if (!Visible(t => t.Text.Contains(L.T("sites.adv.hint.router_owned", new { sites = "youtube.com" })))) throw new Exception("У строки с youtube.com нет пометки «ведёт роутер»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-sites-joint.png"));

                // Роутер пропал: ПК сразу работает сам, переключатель показывает «Выключено» (связки нет), ждать роутер можно перестать.
                vm.GoTo(Pages.Router);
                Pump();
                tabs = Find<TabControl>(w)!;
                fake.Remove();
                // RefreshAsync молча ничего не делает, пока идёт другое обновление (таймер окна, Busy): одного вызова мало — повторяем, пока роутер не признан пропавшим.
                // Проверку связки запускает перемена роутера в фоне — после каждого обновления дожидаемся её, а не времени.
                for (var until = DateTime.UtcNow.AddSeconds(60); vm.Router.JointStatus != "Роутер не найден — этот ПК работает сам" && DateTime.UtcNow < until;)
                {
                    WaitUi(vm.Router.RefreshAsync());
                    WaitUi(vm.Router.JointWhenIdleAsync());
                    Pump();
                    Thread.Sleep(50);
                }
                PumpUntil(() => vm.Router.JointStatus == "Роутер не найден — этот ПК работает сам", "откат к полной настройке: " + vm.Router.JointStatus);
                Pump();
                if (!vm.IsRunning) throw new Exception("ПК выключился вместе с пропавшим роутером");
                if (f.Backend.JointValue.Active) throw new Exception("У помощника осталась связка при пропавшем роутере");
                // Карточка остаётся (её нужно уметь выключить), статус отката — в ней же и на главной.
                if (!Visible(t => t.Text == "Роутер не найден — этот ПК работает сам")) throw new Exception("В карточке нет статуса отката");
                // Переключатель показывает настоящее состояние: связки нет — он выключен («Выключено»), а ждать роутер можно перестать.
                if (Switch().IsChecked == true) throw new Exception("Переключатель при пропавшем роутере не должен выглядеть включённым");
                if (!Visible(t => t.Text == "Выключено")) throw new Exception("Подпись переключателя при пропавшем роутере — не «Выключено»");
                if (!Visible(t => t.Text == L.T("router.joint.hint_waiting"))) throw new Exception("Нет строки «Включится само, когда роутер найдётся»");
                if (Find<Button>(tabs, b => b.IsVisible && b.Content as string == "Не ждать роутер") is null) throw new Exception("Нет «Не ждать роутер»");
                if (Visible(t => t.Text.StartsWith("Выключите, чтобы"))) throw new Exception("В карточке осталась фраза «Выключите, чтобы…»");
                if (snapDir is not null) Snap(w, Path.Combine(snapDir, $"{theme}-router-joint-fallback.png"));
                vm.GoTo(Pages.Home);
                Pump();
                if (!vm.PcItems.Any(i => i.Value.Contains("Роутер не найден — этот ПК работает сам"))) throw new Exception("На главной (панель ПК) нет фразы отката");
                Close(w);
                Stop(vm, f);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
