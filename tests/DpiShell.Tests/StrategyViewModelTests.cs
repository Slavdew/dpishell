using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Свои стратегии в модели окна: «Скопировать стратегию», «Вставить стратегию», проверка перед применением, «Изменить», «Удалить».
/// Помощник — фейк по каналу (по вызовам видно, что окно ему отправило); для вставки — ещё и настоящий над временной папкой.
/// </summary>
public sealed class StrategyViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    MainViewModel Model(HelperLink link, Shell? shell = null)
    {
        var vm = TestWindow.Model(shell ?? NewShell(), link);
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.IsLoading))!.SetValue(vm, false);
        vm.ToastTime = TimeSpan.FromMinutes(5);
        vm.Confirm = _ => true;
        return vm;
    }

    static async Task WaitFor(Func<bool> cond)
    {
        for (int i = 0; i < 300 * 10 && !cond(); i++) await Task.Delay(10);
        Assert.True(cond(), "не дождались");
    }

    static string Exported(Shell shell, string id, string title) =>
        StrategyExchange.Encode(title, shell.Catalog.Find(id)!.Roles);

    [Fact]
    public async Task Copy_PutsDecodableString_AndShowsToast()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        string? copied = null;
        vm.CopyText = t => copied = t;
        var s = shell.Catalog.Strategies[3];

        vm.CopyStrategyCmd.Execute(s.Id);

        Assert.StartsWith("dpishell:1:", copied);
        var back = StrategyExchange.Parse(copied);
        Assert.Equal(s.Title, back.Title);
        Assert.Equal(s.Roles.OrderBy(r => r.Key), back.Roles.OrderBy(r => r.Key));
        Assert.Equal(L.T("select.copied"), vm.Toast);
        Assert.False(vm.ToastError);
    }

    [Fact]
    public async Task Copy_ClipboardBusy_FailsWithMessage()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.CopyText = _ => throw new System.Runtime.InteropServices.ExternalException("занят");

        vm.CopyStrategyCmd.Execute(shell.Catalog.Strategies[0].Id);

        Assert.True(vm.ToastError);
        Assert.Contains("буфер обмена занят", vm.Toast);
    }

    [Fact]
    public async Task Paste_Hostile_NeverReachesHelper_AndNamesReason()
    {
        await using var f = new HelperFixture();
        var vm = Model(await f.LinkAsync());
        var bad = new Dictionary<string, string>
        {
            [StrategyExchange.Encode("x", new Dictionary<string, string> { ["general"] = "--dpi-desync=fake --dpi-desync-repeats=6" })[..^12]] = "Строка повреждена",
            ["dpishell:1:!!!"] = "Строка повреждена",
            ["обычный текст"] = "Это не строка DpiShell",
            ["winws.exe --wf-raw=x --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake"] = "Параметр --wf-raw не разрешён",
            ["winws.exe --filter-tcp=80,443 --hostlist=\"%LISTS%a.txt\" --dpi-desync=fake --dpi-desync-fake-tls=\"C:\\x.bin\""] = "недопустимые символы",
            [""] = "пусто",
        };
        foreach (var (text, reason) in bad)
        {
            vm.ReadText = () => text;
            await vm.PasteStrategyAsync();
            Assert.True(vm.ToastError, text);
            Assert.Contains(reason, vm.Toast);
            Assert.StartsWith("Не вставлено", vm.Toast);
        }
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("strategy_add"));
        Assert.Empty(vm.Scores);
        Assert.Null(vm.PendingCheckId);
    }

    [Fact]
    public async Task Paste_Valid_GoesToSelect_AsUncheckedRow_WithCheckOffered()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.ReadText = () => Exported(shell, shell.Catalog.Strategies[2].Id, "Чужая стратегия");

        await vm.PasteStrategyAsync();

        Assert.Contains(f.Backend.Calls, c => c.StartsWith("strategy_add pasted Чужая стратегия"));
        Assert.Equal(Pages.Select, vm.Page);
        var row = Assert.Single(vm.Scores);
        Assert.True(row.Unchecked);
        Assert.Equal("вставлена", row.Label);
        Assert.Equal("не проверена", row.Error);
        Assert.Equal("—", row.Share);
        Assert.Equal(row.Id, vm.PendingCheckId);
        Assert.True(vm.HasPendingCheck);
        Assert.False(vm.HasBest);           // «Применить» — не раньше проверки
        Assert.Contains("Вставлена: Чужая стратегия", vm.ResultText);
        // В списке на главной — с меткой, её можно изменить и удалить.
        var choice = Assert.Single(vm.StrategyChoices, c => c.Id == row.Id);
        Assert.Equal("вставлена", choice.Label);
        Assert.True(choice.Personal && choice.Removable);
        Assert.Equal("Изменить", choice.EditText);
    }

    [Fact]
    public async Task Check_ThenApply_OnlyAfterCheck()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.ReadText = () => Exported(shell, shell.Catalog.Strategies[0].Id, "Чужая");
        await vm.PasteStrategyAsync();
        var id = vm.PendingCheckId!;

        await vm.CheckOneAsync(id);

        Assert.Contains(f.Backend.Calls, c => c.StartsWith($"strategy_check {id}"));
        Assert.Null(vm.PendingCheckId);
        var row = Assert.Single(vm.Scores);
        Assert.False(row.Unchecked);
        Assert.True(row.Usable);
        Assert.True(vm.HasBest);
        Assert.StartsWith("Проверена:", vm.ResultText);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("enable"));   // сама не включается

        vm.ApplyBestCmd.Execute(null);
        await WaitFor(() => f.Backend.Calls.Contains("enable " + id));
    }

    [Fact]
    public async Task ApplySelected_Unchecked_NeedsConfirmation()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.ReadText = () => Exported(shell, shell.Catalog.Strategies[0].Id, "Чужая");
        await vm.PasteStrategyAsync();
        var id = vm.PendingCheckId!;
        vm.SelectedScore = vm.Scores[0];
        Assert.True(vm.ApplySelectedCmd.CanExecute(null));

        string? asked = null;
        vm.Confirm = q => { asked = q; return false; };
        await vm.ApplySelectedAsync();
        Assert.Contains("не проверена", asked);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("enable"));

        vm.Confirm = _ => true;
        await vm.ApplySelectedAsync();
        Assert.Contains("enable " + id, f.Backend.Calls);
    }

    [Fact]
    public async Task ChoosingUncheckedPersonalStrategy_AsksFirst_AndRefusalKeepsOldChoice()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.ReadText = () => Exported(shell, shell.Catalog.Strategies[0].Id, "Чужая");
        await vm.PasteStrategyAsync();
        var id = vm.PendingCheckId!;
        var before = vm.ChosenStrategyId;

        vm.Confirm = _ => false;
        vm.ChosenStrategyId = id;
        Assert.Equal(before, vm.ChosenStrategyId);
        Assert.DoesNotContain("choose " + id, f.Backend.Calls);

        vm.Confirm = _ => true;
        vm.ChosenStrategyId = id;
        await WaitFor(() => f.Backend.Calls.Contains("choose " + id));
        // Готовая стратегия подтверждения не просит.
        bool asked = false;
        vm.Confirm = _ => { asked = true; return true; };
        vm.ChosenStrategyId = "alt10";
        Assert.False(asked);
    }

    [Fact]
    public async Task Edit_Catalog_MakesCopy_SavedAsUser_AndWaitsForCheck()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        var src = shell.Catalog.Strategies[1];
        StrategyEditor? shown = null;
        vm.EditStrategy = e =>
        {
            shown = e;
            e.Title = "Моя правка";
            e.Fields.First(x => x.Role == "general").Text = "--dpi-desync=multisplit\n  --dpi-desync-split-pos=2";   // переносы строк — в пробел
            e.SaveCmd.Execute(null);
            return true;
        };

        await vm.EditStrategyAsync(src.Id);

        Assert.True(shown!.Catalog);
        Assert.Equal("Копия стратегии", shown.Header);
        var call = Assert.Single(f.Backend.Calls, c => c.StartsWith("strategy_add user Моя правка"));
        Assert.Contains("general=--dpi-desync=multisplit --dpi-desync-split-pos=2", call);
        Assert.Equal("Сохранена: Моя правка", vm.ResultText);
        Assert.True(vm.HasPendingCheck);
        Assert.Equal("своя", vm.Scores[0].Label);
    }

    [Fact]
    public async Task Edit_Cancelled_SavesNothing()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.EditStrategy = _ => false;
        await vm.EditStrategyAsync(shell.Catalog.Strategies[0].Id);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("strategy_add"));
    }

    [Fact]
    public async Task Delete_AsksFirst_RemovesRow_AndRefusesCurrent()
    {
        await using var f = new HelperFixture();
        var shell = NewShell();
        var vm = Model(await f.LinkAsync(), shell);
        vm.ReadText = () => Exported(shell, shell.Catalog.Strategies[0].Id, "Чужая");
        await vm.PasteStrategyAsync();
        var id = vm.PendingCheckId!;

        vm.Confirm = _ => false;
        await vm.DeleteStrategyAsync(id);
        Assert.DoesNotContain("strategy_delete " + id, f.Backend.Calls);

        vm.Confirm = _ => true;
        await vm.DeleteStrategyAsync(id);
        Assert.Contains("strategy_delete " + id, f.Backend.Calls);
        Assert.Empty(vm.Scores);
        Assert.DoesNotContain(vm.StrategyChoices, c => c.Id == id);

        // Готовую удалить нельзя — до помощника не доходит.
        f.Backend.Calls.Clear();
        await vm.DeleteStrategyAsync(shell.Catalog.Strategies[0].Id);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("strategy_delete"));
    }

    [Fact]
    public void Labels_AndRouterOffer_FollowSource()
    {
        StrategyChoice C(string src) => new("x", "X", "", src == "search") { Source = src };
        Assert.False(C("flowseal").Removable);
        Assert.False(C("flowseal").HasLabel);
        Assert.Equal(L.T("home.edit_copy"), C("flowseal").EditText);
        Assert.NotNull(C("flowseal").EditTip);   // «сохранится копия» — только у готовых
        Assert.Null(C("user").EditTip);
        Assert.True(C("search").Removable);
        Assert.False(C("search").Personal);
        Assert.Equal("найдена поиском", C("search").Label);
        Assert.True(C("pasted").Personal && C("pasted").Removable);
        Assert.True(C("user").Personal);
        Assert.Equal("своя", C("user").Label);
    }

    // ---------- окно правки ----------

    static StrategyEditor Editor(string original = "Исходная") => new StrategyEditor(original, "Новая",
        new Dictionary<string, string> { ["general"] = "--dpi-desync=fake --dpi-desync-repeats=6" }, catalog: false,
        name => File.Exists(Path.Combine(Repo.Data, "fake", name))).Start();

    [Fact]
    public void Editor_ValidatesOnTheFly_WithWhitelist()
    {
        var e = Editor();
        Assert.True(e.CanSave);
        Assert.False(e.HasSummary);
        var field = e.Fields.Single();

        field.Text = "--wf-raw=true";
        Assert.False(e.CanSave);
        Assert.Equal("Параметр --wf-raw не разрешён", field.Error);
        Assert.Contains("Параметр --wf-raw не разрешён", e.Summary);
        Assert.False(e.SaveCmd.CanExecute(null));

        field.Text = "--dpi-desync=fake --dpi-desync-fake-tls=C:\\Windows\\x.bin";
        Assert.False(e.CanSave);
        field.Text = "--dpi-desync=fake & calc";
        Assert.False(e.CanSave);
        field.Text = "--dpi-desync=fake --dpi-desync-fake-tls={fake}/missing.bin";
        Assert.Contains("нет в папке программы", e.Summary);
        field.Text = "--dpi-desync=fake\r\n--dpi-desync-repeats=8";    // перенос строки допустим: станет пробелом
        Assert.True(e.CanSave);
        Assert.Equal("--dpi-desync=fake --dpi-desync-repeats=8", e.Result.Roles["general"]);
    }

    [Fact]
    public void Editor_NeedsNewName_AndAtLeastOneRole()
    {
        var e = Editor("Исходная");
        e.Title = "Исходная";
        Assert.Contains(L.T("select.editor.title_same"), e.Summary);
        e.Title = "  ";
        Assert.Contains(L.T("select.editor.title_empty"), e.Summary);
        e.Title = "Другая";
        Assert.True(e.CanSave);
        e.Fields.Single().Text = "";                      // пустое поле — роль не используется
        Assert.Contains("хотя бы одна роль", e.Summary);
        e.AddRole("google");
        e.Fields.Single(x => x.Role == "google").Text = "--dpi-desync=multisplit";
        Assert.True(e.CanSave);
        Assert.Equal(["google"], e.Result.Roles.Keys);
        Assert.DoesNotContain("google", e.MissingRoles);
    }

    [Fact]
    public void Editor_GameOptionsOnlyInGameRoles()
    {
        var e = Editor();
        e.Fields.Single().Text = "--dpi-desync=fake --dpi-desync-any-protocol=1";
        Assert.False(e.CanSave);
        e.Fields.Single().Text = "--dpi-desync=fake";
        e.AddRole("game_udp");
        e.Fields.Single(x => x.Role == "game_udp").Text = "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n2";
        Assert.True(e.CanSave);
    }
}
