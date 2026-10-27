using System.Windows;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>
/// Съёмщик для README: <c>DPISHELL_DEMO_SHOTS=&lt;папка&gt; dotnet test --filter DemoScreenshots</c> — главные вкладки в обеих темах,
/// файлы <c>&lt;тема&gt;-&lt;вкладка&gt;.png</c>. Без переменной тест ничего не делает.
/// Окно собрано так же, как в остальных тестах окна (настоящий помощник над временной папкой, <see cref="FakeDnsSystem"/>, <see cref="FakeRouter"/>,
/// <see cref="Fill"/> с выдуманными данными): с машины не читается ничего — адаптеры, VPN, DNS, процессы, hosts, роутер, подборы и пути подделаны,
/// шлюз подменён, сети нет. <see cref="Demo.Active"/> размывает помеченное <c>Demo.Sensitive</c> поверх выдуманных значений.
/// </summary>
public sealed partial class WindowSmokeTests
{
    static readonly (int Page, string Name)[] DemoPages = [.. AllPages];

    [Fact]
    public void DemoScreenshots()
    {
        var dir = Environment.GetEnvironmentVariable("DPISHELL_DEMO_SHOTS");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            Demo.Active = true;
            try
            {
                foreach (var theme in new[] { ThemeManager.Light, ThemeManager.Dark })
                {
                    ThemeManager.Apply(theme);
                    var shell = new Shell(new AppPaths(tmp.Sub("demo-" + theme), Repo.Data));
                    var settings = TestWindow.Settings(shell);
                    settings.Theme = theme;
                    settings.UpdatesData = "2026-10-18";
                    settings.UpdatesApp = "0.9.3";
                    settings.UpdatesCheckedAt = settings.UpdatesOkAt = new DateTime(2026, 10, 17, 12, 0, 0);
                    if (theme == ThemeManager.Dark) shell.State.RunMode = "process";
                    // Запись подключений «Игр и программ» — на подменённых источнике потоков и списке процессов (выдуманные game.exe/chrome.exe).
                    var flows = new FakeFlowSource();
                    var procs = new FakeProcesses(new ProcInfo(4, 0, "System"), new ProcInfo(900, 4, "chrome.exe"));
                    var helper = new HelperFixture(backend: PcHelper.Over(shell, flows, procs));
                    var link = Task.Run(helper.LinkAsync).GetAwaiter().GetResult();
                    var vm = TestWindow.Model(shell, link, settings);
                    vm.Apps.ListProcesses = () => [new(900, "chrome.exe", "Новая вкладка"), new(2000, "game.exe", "Моя игра")];
                    Fill(vm, shell);
                    // «Сайты»: формат ввода — на example.com (это поле не размыто). Список — сразу в данные помощника, затем окно перечитывает.
                    shell.SetUserTargets(["example.com", "video.example.com", "chat.example.org", "https://cdn.example.net/"]);
                    WaitUi((Task)typeof(MainViewModel).GetMethod("FetchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, [false])!);
                    // Ничего с машины: шлюз и TCP-метки роутера — подмена (иначе читались бы настройки Windows).
                    vm.Router.Gateway = () => "192.168.1.1";
                    vm.Router.PcTimestamps = () => true;
                    var w = Open(vm);
                    foreach (var (page, name) in DemoPages)
                    {
                        SetProp(vm, "IsLoading", true);
                        vm.GoTo(page);
                        Pump();
                        SetProp(vm, "IsLoading", false);
                        typeof(MainViewModel).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                        // Во временной папке нет файлов ядра («повреждены») — на снимке всё в порядке.
                        SetProp(vm, "CoreBroken", false);   // CoreText ниже перечитывает строку «Ядро»
                        SetProp(vm, "CoreText", "в порядке (версия v72.13)");
                        Pump();
                        Snap(w, Path.Combine(dir, $"{theme}-{name}.png"));
                    }

                    // Главная: список стратегий (новая главная — «Подробнее» проверки на плитке, отдельного снимка не нужно).
                    vm.GoTo(Pages.Home);
                    vm.ShowStrategies = true;
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-home-strategies.png"));
                    vm.ShowStrategies = false;

                    // Роутер подключён: FakeRouter в процессе (loopback), пароль не сохраняется на диск вне временной папки.
                    using var fake = new FakeRouter().As020().AsR8();
                    vm.Router.AddressAuto = false;
                    vm.Router.CustomAddress = fake.Address;
                    vm.Router.PollInterval = TimeSpan.FromMilliseconds(10);
                    WaitUi(vm.Router.ConnectAsync("secret"));
                    vm.GoTo(Pages.Router);
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-router.png"));
                    // Главная с роутером: строки «Этот ПК» и «Роутер».
                    vm.GoTo(Pages.Home);
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-home-router.png"));

                    // «Игры и программы»: простой режим «Начать» → «Готово» → «Заработало?» (профиль в списке), затем для опытных — запись со списком.
                    vm.GoTo(Pages.Apps);
                    Pump();
                    var tabs = Find<System.Windows.Controls.TabControl>(w)!;
                    System.Windows.Controls.Button? Btn(string content) => Find<System.Windows.Controls.Button>(tabs, b => b.IsVisible && b.Content as string == content);
                    Btn("Начать")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.IsRecording, "запись");
                    procs.List.Add(new ProcInfo(2000, 4, "game.exe"));
                    flows.Push("udp", "203.0.113.50", 27015, 2000);
                    flows.Push("tcp", "198.51.100.7", 443, 2000, FlowHint.Established);
                    Task.Run(() => vm.Apps.TickAsync()).GetAwaiter().GetResult();
                    Pump();
                    Btn("Готово")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.IsCheck, "применение и вопрос");
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-apps-simple.png"));
                    Btn("Да, заработало")!.Command.Execute(null);
                    Pump();
                    vm.Apps.DebugOpen = true;
                    Pump();
                    vm.Apps.RefreshProcesses();
                    Btn("Записать")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.AdvRecording, "запись");
                    flows.Push("udp", "203.0.113.51", 27016, 2000);
                    flows.Push("udp", "192.168.1.1", 53, 2000);
                    flows.Push("tcp", "198.51.100.60", 443, 900);
                    Btn("Стоп")!.Command.Execute(null);
                    PumpUntil(() => vm.Apps.HasRows, "список подключений");
                    Pump();
                    var grid = Find<System.Windows.Controls.DataGrid>(tabs, g => g.IsVisible && g.Items.Count > 0) ?? throw new Exception("Нет таблицы подключений");
                    grid.BringIntoView();
                    if (Find<System.Windows.Controls.ScrollViewer>(tabs, sv => sv.IsVisible && sv.ScrollableHeight > 0) is { } scroll)
                        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight * 0.55);
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-apps-advanced.png"));
                    Close(w);
                    Stop(vm, helper);
                }
            }
            finally { Demo.Active = false; }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
