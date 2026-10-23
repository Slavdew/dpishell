using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

public sealed partial class WindowSmokeTests
{
    /// <summary>
    /// Итог «Проверить обновления» — на месте, у кнопки (аудит С-23), одинаково для роутера и программы:
    /// роутер — «Проверка…» с недоступной ссылкой, «Последняя версия · проверено в …», «Доступна X» и «Обновить до X» без нажатия;
    /// программа — о версии известно из манифеста: сразу «Обновить до X», «Проверить обновления» нет; источники молчат — «Не проверено: …».
    /// Сеть — фейки (FakeRouter, MapHandler), перезапуск подменён.
    /// </summary>
    [Fact]
    public void UpdateCheck_ResultNextToButton_RouterAndApp()
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            using var fake = new FakeRouter().As020();
            fake.UpdateAvailable = false;
            var shell = new Shell(new AppPaths(tmp.Sub("upd-lines"), Repo.Data));
            var settings = TestWindow.Settings(shell);
            var store = new AppVersionStore(Path.Combine(Path.GetDirectoryName(settings.File)!, "versions"));
            var vm = TestWindow.Model(shell, settings: settings);
            var handler = new MapHandler();
            vm.Updates.Http = () => new HttpClient(handler, disposeHandler: false);
            vm.Updates.AppVersion = "1.0.0.0";
            vm.Updates.CreateUpdater = http => new AppUpdater(
                new AppUpdateClient(http, [AppUpdateTests.A]) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = AppUpdateTests.Key.PublicKey, OsVersion = new Version(10, 0, 26200) }, store)
            { CurrentVersion = "1.0.0", CurrentExe = Path.Combine(tmp.Sub("portable"), "DpiShell.exe"), CurrentPid = 4242, FreeSpace = _ => 5L << 30 };
            var w = Open(vm);
            vm.Updates.Restart = _ => new TaskCompletionSource().Task;
            SetProp(vm, "IsLoading", false);
            var router = vm.Router;
            router.AddressAuto = false;
            router.CustomAddress = fake.Address;
            router.PollInterval = TimeSpan.FromMilliseconds(10);
            router.Gateway = () => "192.168.1.1";
            router.PcTimestamps = () => true;
            WaitUi(router.ConnectAsync("secret"));
            if (!router.Connected) throw new Exception("Роутер не принял вход");
            vm.GoTo(Pages.Router);
            Pump();
            var tabs = Find<TabControl>(w)!;
            bool Shown(Func<string, bool> m) => Find<TextBlock>(tabs, t => t.IsVisible && m(t.Text)) is not null || Find<TextBox>(tabs, t => t.IsVisible && m(t.Text)) is not null;
            Button? Btn(string text) => Find<Button>(tabs, b => b.IsVisible && b.Content as string == text);

            // Роутер: проверено им самим — строка у ссылки сразу.
            if (!Shown(t => t.StartsWith("Последняя версия · проверено "))) throw new Exception("Нет строки «Последняя версия · проверено …» у ссылки");
            var link = Btn(L.T("router.update.check")) ?? throw new Exception("Нет ссылки «Проверить обновления»");
            // Во время — «Проверка…», ссылка недоступна.
            fake.UpdateAvailable = true;
            var check = router.CheckUpdatesAsync();
            Pump();
            if (!Shown(t => t == L.T("router.update.line_checking"))) throw new Exception("Нет «Проверка…» у ссылки во время проверки");
            if (link.IsEnabled) throw new Exception("Ссылка «Проверить обновления» доступна во время проверки");
            WaitUi(check);
            Pump();
            if (!Shown(t => t == "Доступна 0.2.1-r1")) throw new Exception("Нет «Доступна 0.2.1-r1» у ссылки после проверки");
            if (Btn("Обновить до 0.2.1-r1") is not { IsEnabled: true }) throw new Exception("Нет доступной «Обновить до 0.2.1-r1» рядом с итогом");

            // Программа: подсказка манифеста — сразу «Обновить до 1.2.0», без «Проверить обновления».
            settings.UpdatesApp = "1.2.0";
            vm.Updates.Refresh();
            vm.GoTo(Pages.About);
            Pump();
            tabs = Find<TabControl>(w)!;
            if (Btn(L.T("about.app.update_button", new { version = "1.2.0" })) is not { IsEnabled: true }) throw new Exception("Версия известна, а «Обновить до 1.2.0» нет");
            if (Btn(L.T("about.app.check_button")) is not null) throw new Exception("При известной версии просят «Проверить обновления»");
            if (!Shown(t => t == L.T("about.app.hint_only", new { version = "1.2.0" }))) throw new Exception("Нет строки «Доступна 1.2.0»");

            // Источники молчат: итог у кнопки — «Не проверено: источники не ответили».
            settings.UpdatesApp = null;
            WaitUi(vm.Updates.CheckAsync(quiet: false));
            Pump();
            if (Btn(L.T("about.app.check_button")) is null) throw new Exception("Нет «Проверить обновления», когда версия не известна");
            if (!Shown(t => t == "Не проверено: " + L.T("about.app.reason_sources"))) throw new Exception("Нет итога «Не проверено» у кнопки");
            Close(w);
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
