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
                    settings.UpdatesData = "2099-01-01";
                    settings.UpdatesApp = "99.0";
                    settings.UpdatesCheckedAt = settings.UpdatesOkAt = new DateTime(2026, 10, 17, 12, 0, 0);
                    if (theme == ThemeManager.Dark) shell.State.RunMode = "process";
                    var (vm, helper) = WithPcHelper(shell, settings);
                    Fill(vm, shell);
                    // «Сайты»: формат ввода — на example.com (это поле не размыто), список сохранён.
                    vm.TargetsText = "example.com\r\nvideo.example.com\r\nchat.example.org\r\nhttps://cdn.example.net/";
                    vm.SaveTargetsCmd.Execute(null);
                    for (int i = 0; i < 500 && vm.TargetsDirty; i++) { Pump(); Thread.Sleep(10); }
                    if (vm.TargetsDirty) throw new Exception("Список сайтов не сохранился: " + vm.TargetsMessage);
                    // Ничего с машины: шлюз и TCP-метки роутера — подмена (иначе читались бы настройки Windows).
                    vm.Router.Gateway = () => "192.168.1.1";
                    vm.Router.PcTimestamps = () => true;
                    var w = Open(vm);
                    foreach (var (page, name) in DemoPages)
                    {
                        SetProp(vm, "IsLoading", true);
                        vm.Page = page;
                        Pump();
                        SetProp(vm, "IsLoading", false);
                        typeof(MainViewModel).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null);
                        Pump();
                        Snap(w, Path.Combine(dir, $"{theme}-{name}.png"));
                    }

                    // Главная: «Подробнее» проверки (адреса DNS этого компьютера — размыты), затем список стратегий.
                    vm.Page = Pages.Home;
                    Pump();
                    var more = Find<System.Windows.Controls.Expander>(Find<System.Windows.Controls.TabControl>(w)!, e => e.Header as string == "Подробнее" && e.IsVisible)
                        ?? throw new Exception("На главной нет «Подробнее» проверки");
                    more.IsExpanded = true;
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-home-details.png"));
                    more.IsExpanded = false;
                    vm.ShowStrategies = true;
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-home-strategies.png"));

                    // Роутер подключён: FakeRouter в процессе (loopback), пароль не сохраняется на диск вне временной папки.
                    using var fake = new FakeRouter().As020().AsR8();
                    vm.Router.AddressAuto = false;
                    vm.Router.CustomAddress = fake.Address;
                    vm.Router.PollInterval = TimeSpan.FromMilliseconds(10);
                    WaitUi(vm.Router.ConnectAsync("secret"));
                    vm.Page = Pages.Router;
                    Pump();
                    Snap(w, Path.Combine(dir, $"{theme}-router.png"));
                    Close(w);
                    Stop(vm, helper);
                }
            }
            finally { Demo.Active = false; }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
