using System.Windows;
using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// «Настройки», «Информация и обновления» и заставка ожидания помощника на обоих языках: страницы целиком (не только видимая часть) —
/// loc-settings-ru/en.png, loc-about-ru/en.png, loc-loading-ru/en.png (DPISHELL_SNAPSHOTS). Без папки — только проверка, что окно строится на обоих языках без ошибок привязок.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class LangSettingsAboutSnapshotTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Fact]
    public void Settings_About_Loading_build_in_ru_and_en()
    {
        using var tmp = new TempDir();
        var snapDir = Environment.GetEnvironmentVariable("DPISHELL_SNAPSHOTS");
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            foreach (var lang in new[] { L.Ru, "en" })
            {
                L.Set(lang);
                var shell = new Shell(new AppPaths(tmp.Sub("root-" + lang), Repo.Data));
                var vm = TestWindow.Model(shell);
                var w = Open(vm);
                var tabs = Find<TabControl>(w)!;
                void Page(int page, string name)
                {
                    vm.Page = page;
                    Pump();
                    if (snapDir is null) return;
                    Directory.CreateDirectory(snapDir);
                    var content = (FrameworkElement)((ScrollViewer)((TabItem)tabs.SelectedItem).Content).Content;
                    Save(content, Path.Combine(snapDir, $"loc-{name}-{lang}.png"));
                }

                // Заставка: помощник долго не отвечает.
                if (snapDir is not null)
                {
                    SetProp(vm, "LoadSlow", true);
                    SetProp(vm, "LoadingText", L.T("shell.loading.slow"));
                    Pump();
                    Directory.CreateDirectory(snapDir);
                    Snap(w, Path.Combine(snapDir, $"loc-loading-{lang}.png"));
                    SetProp(vm, "LoadSlow", false);
                }
                SetProp(vm, "IsLoading", false);
                Pump();

                vm.ShowAdvancedSetting = true;
                var at = DateTime.UtcNow;
                vm.Changes.Show([
                    new("1", "A", at, UndoStatus.Restored, ""),
                    new("2", "B", at, UndoStatus.ChangedByOthers, "DNS: Wi-Fi"),
                    new("3", "C", at, UndoStatus.Failed, "hosts is busy"),
                    new("4", "D", at, UndoStatus.AlreadyBack, ""),
                ]);
                Page(Pages.Settings, "settings");
                Page(Pages.About, "about");
                Close(w);
            }
        });
        Assert.True(errors.Count == 0, "Ошибки привязок:\n" + string.Join("\n", errors));
    }
}
