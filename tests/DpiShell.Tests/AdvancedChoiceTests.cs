using System.Windows.Controls;
using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.WindowSmokeTests;

namespace DpiShell.Tests;

/// <summary>
/// «Свои настройки для сайтов» (аудит Б-1): окно открыто сразу на языке и после смены языка туда и обратно — у каждой строки в списке «Стратегия» видно её значение,
/// «Сохранить» отправляет те же стратегии, что были загружены.
/// </summary>
[Collection(LangCollection.Name)]
public sealed class AdvancedChoiceTests : IDisposable
{
    public void Dispose() => L.ResetForTests();

    [Theory]
    [InlineData("ru")]
    [InlineData("en")]
    public void Rows_show_their_strategy_and_save_unchanged(string lang)
    {
        using var tmp = new TempDir();
        var errors = WithBindingErrors(() =>
        {
            ThemeManager.Apply(ThemeManager.Light);
            L.Set(lang);
            var shell = new Shell(new AppPaths(tmp.Sub("root"), Repo.Data));
            shell.Data.Seed();
            var settings = TestWindow.Settings(shell);
            settings.ShowAdvanced = true;
            var vm = TestWindow.Model(shell, null, settings);
            var a = vm.AdvancedPc;
            var ids = shell.Catalog.Strategies.Take(2).Select(s => s.Id).ToList();
            a.Strategies = () => [.. shell.Catalog.Strategies.Select(s => new AdvancedChoice(s.Id, s.Title))];
            a.Unavailable = () => null;
            AdvancedState loaded = new(true, AdvancedSites.OthersLists,
            [
                new(["youtube.com"], AdvancedSites.Auto, ids[1]),
                new(["bank.example"], AdvancedSites.Direct, null),
                new(["games.example"], ids[0], ids[0]),
            ]);
            a.Load = () => Task.FromResult<AdvancedState?>(loaded);
            AdvancedState? sent = null;
            a.Save = (s, _) => { sent = s; return Task.FromResult(new AdvancedSaveResult(s, null)); };

            var w = Open(vm);
            SetProp(vm, "IsLoading", false);
            vm.GoTo(Pages.Sites);
            Pump();
            WaitUi(a.RefreshAsync(force: true));
            Pump();
            // Список стратегий поменялся после того, как строки уже показаны (стратегии пришли позже) — выбор не теряется.
            var more = shell.Catalog.Strategies.Take(3).Select(s => new AdvancedChoice(s.Id, s.Title)).ToList();
            a.Strategies = () => more;
            WaitUi(a.RefreshAsync(force: true));
            Pump();
            Check();
            // Язык туда и обратно («Настройки» → «Язык»): список пересобирается дважды — выбор строк остаётся (так терялся в Б-1).
            L.Set(lang == L.Ru ? L.En : L.Ru);
            Pump();
            Check();
            L.Set(lang);
            Pump();
            Check();

            void Check()
            {
                var combos = FindAll<ComboBox>(w, c => c.IsVisible && c.DataContext is AdvancedRowVm).ToList();
                Assert.Equal(3, combos.Count);
                Assert.All(combos, c => Assert.NotNull(c.SelectedItem));
                Assert.Equal([AdvancedSites.Auto, AdvancedSites.Direct, ids[0]], combos.Select(c => c.SelectedValue as string));
            }

            // Сохранить без правок стратегий: строку сайта поменяли — стратегии уходят те же.
            a.Rows[1].DomainsText = "bank2.example";
            WaitUi(a.ApplyAsync());
            Assert.NotNull(sent);
            Assert.Equal([AdvancedSites.Auto, AdvancedSites.Direct, ids[0]], sent!.Rows.Select(r => r.Strategy));
            Close(w);
        });
        Assert.Empty(errors);
    }
}
