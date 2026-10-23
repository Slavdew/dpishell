using System.Text.Json.Nodes;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>
/// Исключение Защитника для папки ядра (ТЗ av-exclusion, API 1.16): путь — только свой, только по нажатию, отказы — понятной фразой и ручным путём,
/// снимается только своё. Защитник — подставной (<see cref="FakeDefender"/>), настоящий под тестами запрещён (<see cref="WmiDefender.Forbidden"/>).
/// </summary>
public class AvExclusionTests
{
    const string Root = @"C:\ProgramData\DpiShell";

    static AvExclusions Av(FakeDefender d, IChangeRecorder? j = null) => new(d, Root, j ?? new MemoryRecorder());

    [Fact]
    public void Real_defender_is_forbidden_under_tests()
    {
        Assert.True(WmiDefender.Forbidden);
        var d = new WmiDefender();
        Assert.Throws<InvalidOperationException>(() => d.Products());
        Assert.Throws<InvalidOperationException>(() => d.Mode());
        Assert.Throws<InvalidOperationException>(() => d.Exclusions());
        Assert.Throws<InvalidOperationException>(() => d.AddPath(Root));
        Assert.Throws<InvalidOperationException>(() => d.RemovePath(Root));
        // И через логику: «не узнать», а не падение.
        Assert.Equal(AvStates.Unavailable, new AvExclusions(d, Root, NullRecorder.Instance).Status().State);
    }

    [Theory]
    [InlineData(@"C:\ProgramData\DpiShell", true)]
    [InlineData(@"c:\programdata\dpishell\", true)]
    [InlineData(@"C:\ProgramData", true)]          // папка выше тоже покрывает
    [InlineData(@"C:\", true)]
    [InlineData(@"C:\ProgramData\DpiShell2", false)]
    [InlineData(@"C:\ProgramData\DpiShell\core", false)]  // папка ниже — нет
    [InlineData(@"D:\ProgramData\DpiShell", false)]
    public void Covers_itself_or_parent(string entry, bool covers) => Assert.Equal(covers, AvExclusions.Covers([entry], Root));

    [Fact]
    public void Status_states()
    {
        Assert.Equal(AvStates.Missing, Av(FakeDefender.Active()).Status().State);
        Assert.Equal(AvStates.Excluded, Av(FakeDefender.Active(@"C:\ProgramData")).Status().State);
        Assert.Equal(AvStates.Unknown, Av(new FakeDefender { Hidden = true }).Status().State);
        Assert.Equal(AvStates.Unknown, Av(new FakeDefender { Unreadable = true }).Status().State);
        Assert.Equal(AvStates.DefenderOff, Av(FakeDefender.Off()).Status().State);
        Assert.Equal(AvStates.Unavailable, Av(new FakeDefender { ModeValue = DefenderMode.Unknown }).Status().State);
        var third = Av(FakeDefender.ThirdParty("Kaspersky")).Status();
        Assert.Equal(AvStates.ThirdParty, third.State);
        Assert.Equal("Kaspersky", third.Product);
        Assert.Equal(Root, third.Path);
    }

    [Fact]
    public void Add_journals_before_and_writes_only_own_folder()
    {
        var d = FakeDefender.Active(@"D:\Games");
        var j = new MemoryRecorder();
        var r = Av(d, j).Add();
        Assert.True(r.Ok);
        Assert.Equal(AvOutcomes.Added, r.Outcome);
        Assert.Equal(AvStates.Excluded, r.Status.State);
        Assert.True(r.Status.Ours);
        Assert.Equal(["add " + Root], d.Calls);
        var e = Assert.Single(j.Active(ChangeKinds.AvExclusion));
        Assert.True(e.Completed);
        Assert.False(e.Before!["present"]!.GetValue<bool>());
        Assert.Equal(Root, e.Before!["path"]!.GetValue<string>());
        // Повторно — «уже», без вызова и без второй записи.
        Assert.Equal(AvOutcomes.Already, Av(d, j).Add().Outcome);
        Assert.Single(d.Calls);
        Assert.Single(j.Items);
    }

    [Fact]
    public void Add_wmi_error_is_refused_with_manual_text_and_aborted()
    {
        var d = new FakeDefender { Refuse = true };
        var j = new MemoryRecorder();
        var r = Av(d, j).Add();
        Assert.False(r.Ok);
        Assert.Equal(AvOutcomes.Refused, r.Outcome);
        Assert.Equal(L.T("core.av.refused"), r.Message);
        Assert.Equal(ChangeState.Aborted, Assert.Single(j.Items).State);
        Assert.Empty(j.Active(ChangeKinds.AvExclusion));
    }

    [Fact]
    public void Add_silently_dropped_is_refused()
    {
        var d = new FakeDefender { SilentDrop = true };
        var j = new MemoryRecorder();
        var r = Av(d, j).Add();
        Assert.Equal(AvOutcomes.Refused, r.Outcome);
        Assert.Equal(AvStates.Missing, r.Status.State);
        Assert.Empty(j.Active(ChangeKinds.AvExclusion));
    }

    [Fact]
    public void Third_party_antivirus_is_never_written_to_defender()
    {
        var d = FakeDefender.ThirdParty("ESET Security");
        var r = Av(d).Add();
        Assert.False(r.Ok);
        Assert.Equal(AvOutcomes.ThirdParty, r.Outcome);
        Assert.Contains("ESET Security", r.Message);
        Assert.Empty(d.Calls);
    }

    [Fact]
    public void Defender_off_needs_nothing()
    {
        var d = FakeDefender.Off();
        var r = Av(d).Add();
        Assert.True(r.Ok);
        Assert.Equal(AvOutcomes.NotNeeded, r.Outcome);
        Assert.Empty(d.Calls);
    }

    [Fact]
    public async Task Hidden_list_journals_unknown_before_and_undo_leaves_it()
    {
        var d = new FakeDefender { Hidden = true };
        var j = new MemoryRecorder();
        var r = Av(d, j).Add();
        Assert.True(r.Ok);   // проверить чтением нельзя — считаем добавленным
        var e = Assert.Single(j.Active(ChangeKinds.AvExclusion));
        Assert.Null(e.Before!["present"]);
        var o = (await new AvExclusionUndoer(d, Root).UndoAsync(e, default));
        Assert.Equal(UndoStatus.ChangedByOthers, o.Status);
        Assert.DoesNotContain(d.Calls, c => c.StartsWith("remove"));
    }

    static ChangeEntry Entry(bool? present, string path = Root) =>
        new("0123456789ab", DateTime.UtcNow, ChangeKinds.AvExclusion, "t", new JsonObject { ["path"] = path, ["present"] = present }, null, ChangeState.Active, true);

    [Fact]
    public async Task Undo_removes_only_ours_and_only_exact_folder()
    {
        var d = FakeDefender.Active(Root, @"C:\ProgramData");
        Assert.Equal(UndoStatus.Restored, (await new AvExclusionUndoer(d, Root).UndoAsync(Entry(false), default)).Status);
        Assert.Equal([@"C:\ProgramData"], d.Paths);   // папка выше (чужая) осталась
        // Было до нас — не трогаем.
        var before = FakeDefender.Active(Root);
        Assert.Equal(UndoStatus.ChangedByOthers, (await new AvExclusionUndoer(before, Root).UndoAsync(Entry(true), default)).Status);
        Assert.Equal([Root], before.Paths);
        // Запись про другую папку (не наша сейчас) — не снимаем.
        var other = FakeDefender.Active(Root);
        Assert.Equal(UndoStatus.ChangedByOthers, (await new AvExclusionUndoer(other, Root).UndoAsync(Entry(false, @"C:\Users\me"), default)).Status);
        Assert.Empty(other.Calls);
        // Уже сняли руками — «уже как было».
        var gone = FakeDefender.Active();
        Assert.Equal(UndoStatus.AlreadyBack, (await new AvExclusionUndoer(gone, Root).UndoAsync(Entry(false), default)).Status);
        // Защитник не дал — сбой, запись остаётся для повтора.
        var refuse = new FakeDefender { Refuse = true, Paths = { Root } };
        var f = (await new AvExclusionUndoer(refuse, Root).UndoAsync(Entry(false), default));
        Assert.Equal(UndoStatus.Failed, f.Status);
        Assert.Equal(L.T("core.undo.av_refused"), f.Note);
    }

    [Fact]
    public void UndoOwn_by_journal_file_removes_ours_and_keeps_users_own()
    {
        using var t = new TempDir();
        var journal = new ChangeJournal(Path.Combine(t.Path, "changes.jsonl"));
        var d = FakeDefender.Active();
        Assert.Equal(AvOutcomes.Added, new AvExclusions(d, Root, journal).Add().Outcome);
        var report = AvExclusionUndoer.UndoOwn(new ChangeJournal(journal.File), d, Root);
        Assert.Equal(UndoStatus.Restored, Assert.Single(report.Items).Status);
        Assert.Empty(d.Paths);
        Assert.Empty(journal.Active(ChangeKinds.AvExclusion));
        // Исключение поставил человек сам: «уже стоит», записи нет — удаление DpiShell его не снимает.
        var mine = FakeDefender.Active(Root);
        var j2 = new ChangeJournal(Path.Combine(t.Path, "c2.jsonl"));
        Assert.Equal(AvOutcomes.Already, new AvExclusions(mine, Root, j2).Add().Outcome);
        Assert.Empty(AvExclusionUndoer.UndoOwn(j2, mine, Root).Items);
        Assert.Equal([Root], mine.Paths);
    }

    [Theory]
    [InlineData("Normal", true, true, true, DefenderMode.Active)]
    [InlineData("Passive Mode", true, true, false, DefenderMode.Passive)]
    [InlineData("SxS Passive Mode", true, true, true, DefenderMode.Passive)]
    [InlineData("EDR Block Mode", true, false, false, DefenderMode.Passive)]
    [InlineData("Not running", false, false, false, DefenderMode.Off)]
    [InlineData(null, true, true, false, DefenderMode.Off)]
    [InlineData(null, true, true, true, DefenderMode.Active)]
    public void Defender_mode_from_status(string? running, bool service, bool av, bool rtp, DefenderMode mode) =>
        Assert.Equal(mode, WmiDefender.ParseMode(running, service, av, rtp));

    [Theory]
    [InlineData(397568u, true)]    // 0x061100 — включён, сигнатуры свежие
    [InlineData(266240u, true)]    // 0x041000
    [InlineData(393472u, false)]   // 0x060100 — выключен
    [InlineData(262144u, false)]   // 0x040000
    public void Product_state_enabled(uint state, bool enabled) => Assert.Equal(enabled, WmiDefender.ProductEnabled(state));

    [Fact]
    public void Defender_product_and_hidden_list_are_recognised()
    {
        Assert.True(WmiDefender.IsDefenderProduct("Windows Defender", "windowsdefender://"));
        Assert.True(WmiDefender.IsDefenderProduct("Microsoft Defender Antivirus", ""));
        Assert.False(WmiDefender.IsDefenderProduct("Kaspersky", @"C:\Program Files\Kaspersky\wmiav.exe"));
        Assert.True(WmiDefender.Parse(["N/A: Must be an administrator to view exclusions"], false).Hidden);
        Assert.True(WmiDefender.Parse([], true).Hidden);
        Assert.False(WmiDefender.Parse([], false).Hidden);
        Assert.Equal([Root], WmiDefender.Parse([Root], true).Paths);
    }

    // ---------- канал: путь не принимается, действие — на весь компьютер ----------

    [Theory]
    [InlineData("av_exclusion_status")]
    [InlineData("av_exclusion_add")]
    [InlineData("av_exclusion_remove")]
    public async Task Commands_reject_any_path_argument(string cmd)
    {
        await using var f = new HelperFixture();
        using var raw = await f.RawAsync();
        foreach (var args in new[] { """{"path":"C:\\"}""", """{"folder":"C:\\Windows"}""", """{"path":""}""", """{"exclusionPath":["C:\\"]}""" })
        {
            var answer = await raw.AskAsync($$"""{"id":7,"cmd":"{{cmd}}","args":{{args}}}""");
            Assert.Contains("\"code\":\"invalid\"", answer);
        }
        Assert.Contains("\"code\":\"invalid\"", await raw.AskAsync($$"""{"id":8,"cmd":"{{cmd}}","args":"C:\\"}"""));
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("av_exclusion"));
        // Без аргументов — доходит.
        Assert.Contains("\"ok\":true", await raw.AskAsync($$"""{"id":9,"cmd":"{{cmd}}"}"""));
    }

    [Fact]
    public async Task Add_is_machine_action_remove_and_status_are_not()
    {
        await using var f = new HelperFixture(o => new HelperServerOptions
            { PipeName = o.PipeName, AllowedUser = o.AllowedUser, AllowAdministrators = false, Log = o.Log, CheckClient = _ => ClientBuild.Other });
        await using var c = await f.ConnectAsync();
        var e = await Assert.ThrowsAsync<HelperException>(() => c.AvExclusionAddAsync());
        Assert.Equal(HelperErrors.Denied, e.Code);
        Assert.DoesNotContain("av_exclusion_add", f.Backend.Calls);
        await c.AvExclusionStatusAsync();
        await c.AvExclusionRemoveAsync();
        Assert.Contains("av_exclusion_status", f.Backend.Calls);
        Assert.Contains("av_exclusion_remove", f.Backend.Calls);
    }

    /// <summary>Настоящий помощник над временной папкой: добавить → в «Изменениях DpiShell» → «Отменить всё» снимает; снять своё — команда.</summary>
    [Fact]
    public async Task Helper_adds_lists_and_undoes_through_the_channel()
    {
        using var t = new TempDir();
        var shell = new Shell(new AppPaths(t.Sub("root"), Repo.Data));
        var d = FakeDefender.Active();
        var backend = new ShellHelperBackend(shell)
        {
            InspectService = () => new ServiceInfo(SvcState.NotInstalled, null, null, false), IsActive = () => false, FindForeign = () => [],
            DnsSystem = new FakeDnsSystem(), Defender = d,
        };
        await using var f = new HelperFixture(backend: backend);
        await using var c = await f.ConnectAsync();
        Assert.Equal(AvStates.Missing, (await c.AvExclusionStatusAsync()).State);
        var r = await c.AvExclusionAddAsync();
        Assert.Equal(AvOutcomes.Added, r.Outcome);
        Assert.Equal(shell.Paths.Root, r.Status.Path);
        Assert.Equal([shell.Paths.Root], d.Paths);   // своя папка помощника, не из запроса
        var item = Assert.Single((await c.ChangesListAsync()).Items);
        Assert.Equal(ChangeKinds.AvExclusion, item.Kind);
        Assert.True(item.Undoable);
        var report = await c.ChangesUndoAllAsync();
        Assert.Equal(UndoStatus.Restored, Assert.Single(report.Items).Status);
        Assert.Empty(d.Paths);

        // Снова добавить — и снять командой; чужое (уже стояло) — «не наше».
        await c.AvExclusionAddAsync();
        Assert.Equal(AvOutcomes.Removed, (await c.AvExclusionRemoveAsync()).Outcome);
        Assert.Empty(d.Paths);
        d.Paths.Add(shell.Paths.Root);
        Assert.Equal(AvOutcomes.Already, (await c.AvExclusionAddAsync()).Outcome);
        Assert.Equal(AvOutcomes.NotOurs, (await c.AvExclusionRemoveAsync()).Outcome);
        Assert.Equal([shell.Paths.Root], d.Paths);
    }

    /// <summary>Установка с ключом --av-exclusion и удаление с --purge — через тот же журнал папки программы (сами sc и UAC тут не запускаются).</summary>
    [Fact]
    public void Installer_adds_before_service_and_purge_removes_only_ours()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Sub("root"), Repo.Data);
        Directory.CreateDirectory(paths.Root);
        var d = FakeDefender.Active();
        var inst = new HelperInstaller(paths) { Defender = d };
        inst.AddExclusion(null);
        Assert.Equal([paths.Root], d.Paths);
        Assert.Single(new ChangeJournal(paths.ChangesFile).Active(ChangeKinds.AvExclusion));
        inst.RemoveOwnExclusion(null);
        Assert.Empty(d.Paths);
        // Отказ Защитника установку не роняет.
        new HelperInstaller(paths) { Defender = new FakeDefender { Refuse = true } }.AddExclusion(null);
    }

    [Fact]
    public void Install_argument_is_a_flag_without_value()
    {
        Assert.Equal("--av-exclusion", HelperInstaller.AvExclusionArg);
        Assert.DoesNotContain("=", HelperInstaller.AvExclusionArg);
    }
}
