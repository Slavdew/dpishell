using DpiShell.App;
using DpiShell.Core;

namespace DpiShell.Tests;

/// <summary>«Отменить всё, что сделала DpiShell» в окне: журнал окна (автозапуск), список, откат, отчёт, предложение при удалении помощника.</summary>
public sealed class ChangesWindowTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    Shell NewShell() => new(new AppPaths(_tmp.Sub("root"), Repo.Data));

    // ---------- автозапуск: журнал окна ----------

    static (Autostart A, MemoryRunKeyStore Store, ChangeJournal J) Auto(TempDir t)
    {
        var settings = WindowSettings.Load(t.Sub("window.json"));
        var store = new MemoryRunKeyStore();
        var j = new ChangeJournal(Path.Combine(t.Sub("w"), "changes-window.jsonl"));
        return (new Autostart(settings) { Store = store, ExePath = @"D:\Apps\DpiShell\DpiShell.exe", Recorder = j }, store, j);
    }

    [Fact]
    public void Autostart_is_recorded_once_and_undo_removes_only_our_key()
    {
        var (a, store, j) = Auto(_tmp);
        a.OnRunMode(service: false);   // по умолчанию включается
        var e = Assert.Single(j.Active());
        Assert.Equal(ChangeKinds.Autostart, e.Kind);
        Assert.True(e.Completed);
        a.Apply();
        Assert.Single(j.Entries());   // повторный запуск запись не плодит

        // Папку с программой перенесли — путь поправился, запись обновилась (откат сравнит с новым путём).
        a.ExePath = @"E:\New\DpiShell.exe";
        a.Apply();
        e = Assert.Single(j.Active());
        Assert.Equal("\"E:\\New\\DpiShell.exe\" --tray", e.After!["command"]!.GetValue<string>());

        // Человек сам заменил команду — не наша; не трогаем.
        store.Values[Autostart.ValueName] = "\"C:\\Mine\\run.exe\"";
        Assert.Equal(UndoStatus.ChangedByOthers, a.Undo(e).Status);
        Assert.True(store.Values.ContainsKey(Autostart.ValueName));

        store.Values[Autostart.ValueName] = a.Command;
        Assert.Equal(UndoStatus.Restored, a.Undo(e).Status);
        Assert.False(store.Values.ContainsKey(Autostart.ValueName));
        Assert.False(a.Enabled);   // явный выбор: при следующем запуске ключ не вернётся
        Assert.Equal(UndoStatus.AlreadyBack, a.Undo(e).Status);
    }

    [Fact]
    public void Autostart_switched_off_by_user_closes_the_entry()
    {
        var (a, _, j) = Auto(_tmp);
        a.OnRunMode(service: false);
        a.Enabled = false;
        Assert.Empty(j.Active());
        Assert.Equal(ChangeState.Closed, j.Entries().Single().State);
    }

    // ---------- карточка: список, откат, отчёт ----------

    static ChangeInfo Info(string id, string title, int minute) =>
        new(id, new DateTime(2026, 1, 19, 12, minute, 0, DateTimeKind.Utc), "dns", title, true);

    [Fact]
    public async Task Card_lists_both_journals_newest_first_and_reports_each_item()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaaa", "DNS этого компьютера: Google", 5));
        f.Backend.ChangesValue.Add(Info("bbbbbbbbbbbb", "Включены временные метки TCP", 1));
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.Autostart.Recorder.Begin(ChangeKinds.Autostart, "Включён запуск окна вместе с Windows", null, null);
        string? asked = null;
        vm.Changes.Confirm = s => { asked = s; return true; };

        await vm.Changes.RefreshAsync();
        Assert.Equal(3, vm.Changes.Items.Count);
        Assert.True(vm.Changes.HasItems);
        Assert.False(vm.Changes.ShowEmpty);
        Assert.Equal(["Включён запуск окна вместе с Windows", "DNS этого компьютера: Google", "Включены временные метки TCP"], vm.Changes.Items.Select(i => i.Title));
        Assert.Matches(@"\d{1,2} января 2026, \d\d:\d\d", vm.Changes.Items[1].When);

        f.Backend.UndoAnswer = UndoStatus.Restored;
        Assert.True(await vm.Changes.UndoAllAsync());
        Assert.Contains("Вернутся значения, которые были до DpiShell; более поздние правки не затрагиваются.", asked);
        Assert.DoesNotContain("обход", asked, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changes_undo_all", f.Backend.Calls);
        Assert.Equal(3, vm.Changes.Report.Count);
        // Ключа Run в тесте нет вовсе (память пуста) — «уже было как раньше»; остальное вернул помощник.
        Assert.Equal(["Уже было как раньше", "Вернул", "Вернул"], vm.Changes.Report.Select(r => r.Status));
        Assert.StartsWith("Вернул: 2 · уже было как раньше: 1", vm.Changes.Summary);
        Assert.Empty(vm.Changes.Items);
        Assert.True(vm.Changes.ShowEmpty == false && vm.Changes.HasReport);
    }

    [Fact]
    public async Task Declined_confirm_does_nothing()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaaa", "DNS", 5));
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        vm.Changes.Confirm = _ => false;
        await vm.Changes.RefreshAsync();
        Assert.False(await vm.Changes.UndoAllAsync());
        Assert.DoesNotContain("changes_undo_all", f.Backend.Calls);
        Assert.Empty(vm.Changes.Report);
    }

    [Fact]
    public void Report_labels_every_outcome_and_asks_about_foreign_changes()
    {
        var vm = TestWindow.Model(NewShell()).Changes;
        var at = DateTime.UtcNow;
        vm.Show([
            new("1", "DNS этого компьютера", at, UndoStatus.ChangedByOthers, "DNS изменён после DpiShell: Wi-Fi"),
            new("2", "Служба", at.AddMinutes(-1), UndoStatus.Failed, "занято"),
            new("3", "Метки TCP", at.AddMinutes(-2), UndoStatus.AlreadyBack, ""),
            new("4", "Автозапуск", at.AddMinutes(-3), UndoStatus.Restored, ""),
        ]);
        Assert.Equal(["Не тронул: изменено после DpiShell", "Не смог", "Уже было как раньше", "Вернул"], vm.Report.Select(r => r.Status));
        Assert.Equal(["skip", "fail", "same", "ok"], vm.Report.Select(r => r.Mark));
        Assert.Equal("Вернул: 1 · уже было как раньше: 1 · не тронул: 1 · не смог: 1", vm.Summary);
        Assert.True(vm.HasQuestion);
        vm.Show([new("4", "Автозапуск", at, UndoStatus.Restored, "")]);
        Assert.False(vm.HasQuestion);
        vm.Show([]);
        Assert.Equal("Отменять было нечего.", vm.Summary);
    }

    [Fact]
    public async Task Without_helper_card_says_so_and_still_undoes_window_entries()
    {
        var vm = TestWindow.Model(NewShell());   // помощника нет
        vm.Autostart.Recorder.Begin(ChangeKinds.Autostart, "Включён запуск окна вместе с Windows", null, null);
        await vm.Changes.RefreshAsync();
        Assert.Contains("Помощник", vm.Changes.HelperNote);
        Assert.Single(vm.Changes.Items);
    }

    // ---------- удаление помощника ----------

    static Task Uninstall(MainViewModel vm) =>
        (Task)typeof(MainViewModel).GetMethod("UninstallHelperAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(vm, null)!;

    [Fact]
    public async Task Uninstalling_helper_offers_to_undo_everything_first()
    {
        await using var f = new HelperFixture();
        f.Backend.ChangesValue.Add(Info("aaaaaaaaaaaa", "DNS", 5));
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var questions = new List<string>();
        vm.Confirm = s => { questions.Add(s); return true; };
        await Uninstall(vm);
        Assert.Contains("changes_undo_all", f.Backend.Calls);
        Assert.Contains("Перед удалением помощника отменить всё", questions[0]);
        Assert.Contains("Удалить помощника DpiShell?", questions[1]);

        // «Нет» на первый вопрос — помощник удаляется как раньше, настройки остаются.
        await using var g = new HelperFixture();
        g.Backend.ChangesValue.Add(Info("cccccccccccc", "DNS", 5));
        var vm2 = TestWindow.Model(NewShell(), await g.LinkAsync());
        vm2.Confirm = s => !s.StartsWith("Перед удалением");
        await Uninstall(vm2);
        Assert.DoesNotContain("changes_undo_all", g.Backend.Calls);
    }

    [Fact]
    public async Task Nothing_to_undo_means_no_extra_question()
    {
        await using var f = new HelperFixture();
        var vm = TestWindow.Model(NewShell(), await f.LinkAsync());
        var questions = new List<string>();
        vm.Confirm = s => { questions.Add(s); return false; };
        await Uninstall(vm);
        Assert.Single(questions);
    }
}
