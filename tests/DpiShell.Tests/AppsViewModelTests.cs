using DpiShell.App;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.Tests;

/// <summary>Модель вкладки «Игры и программы»: простой режим «Начать → Готово», варианты, откат; «Отладка подключений»; настройка показа.</summary>
public sealed class AppsViewModelTests
{
    static async Task<(HelperFixture, AppsViewModel, System.Collections.Concurrent.ConcurrentBag<string>)> Make_()
    {
        var f = new HelperFixture();
        var link = await f.LinkAsync();
        var said = new System.Collections.Concurrent.ConcurrentBag<string>();   // пишет поток модели, читает тест
        var vm = new AppsViewModel(link) { Say = said.Add };
        return (f, vm, said);
    }

    static bool Can(System.Windows.Input.ICommand c) => c.CanExecute(null);
    static Task Run(System.Windows.Input.ICommand c) { c.Execute(null); return Task.CompletedTask; }

    /// <summary>Асинхронные команды запускаются «выстрелил и забыл» — ждём, пока фейк увидит вызов или шаг сменится.</summary>
    static async Task Until(Func<bool> cond, string what)
    {
        for (int i = 0; i < 200 * 10 && !cond(); i++) await Task.Delay(25);
        Assert.True(cond(), what);
    }

    [Fact]
    public async Task Simple_StartFinish_AppliesFirstVariant_ThenYes_KeepsProfile()
    {
        var (f, vm, said) = await Make_();
        await using var _ = f;
        Assert.True(vm.IsIdle);
        Assert.True(vm.ShowStart);
        Assert.Contains("Начать", vm.Intro);
        Assert.Contains("Vanguard", vm.Anti);

        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись не началась");
        Assert.Contains("rec_start NewProcesses 0 False", f.Backend.Calls);  // игровые процессы — новые; по умолчанию без драйвера (античиты)
        Assert.True(Can(vm.FinishCmd));
        Assert.False(Can(vm.StartCmd));

        await Run(vm.FinishCmd);
        await Until(() => vm.IsCheck, "после «Готово» не дошли до вопроса");
        // Один профиль: порты и адреса игры, шум (chrome, svchost, локальная сеть) отсеян, вариант 0.
        var call = f.Backend.Calls.Single(c => c.StartsWith("app_profile_set"));
        Assert.EndsWith("game 443 27015-27016 3 0", call.Replace("app_profile_set  ", "app_profile_set "));
        Assert.Contains("Заработало?", vm.Status);
        Assert.Contains("Попытка 1 из 5", vm.Detail);
        Assert.Single(vm.Profiles);

        await Run(vm.YesCmd);
        Assert.True(vm.IsDone);
        Assert.Contains("Профиль «game» сохранён.", vm.Status);
        Assert.Single(vm.Profiles);
        Assert.Contains(said, s => s == "Профиль «game» сохранён.");   // строка состояния и карточка говорят одно
    }

    [Fact]
    public async Task Simple_No_TriesNextVariant_SameProfile_UntilExhausted_ThenRollsBack()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await Run(vm.FinishCmd);
        await Until(() => vm.IsCheck, "вопрос");
        var id = vm.Profiles.Single().Id;

        // «Нет» → следующий вариант того же профиля (не второй профиль).
        await Run(vm.NoCmd);
        await Until(() => f.Backend.Calls.Count(c => c.StartsWith("app_profile_set")) == 2 && vm.IsCheck, "второй вариант");
        Assert.Contains("Попытка 2 из 5", vm.Detail);
        Assert.Single(vm.Profiles);
        Assert.Equal(id, vm.Profiles.Single().Id);
        Assert.Equal(1, vm.Profiles.Single().Info.Variant);

        await Run(vm.NoCmd);   // «Стало хуже» — та же команда
        await Until(() => f.Backend.Calls.Count(c => c.StartsWith("app_profile_set")) == 3 && vm.IsCheck, "третий вариант");

        // Варианты кончились (в фейке три) — профиль снят, «как было».
        await Run(vm.NoCmd);
        await Until(() => vm.IsFailed, "после последнего «Нет» не отказались");
        Assert.Equal(L.Plural("apps.giveup", 3), vm.Status);
        Assert.Contains($"app_profile_remove {id}", f.Backend.Calls);
        Assert.Empty(vm.Profiles);
        Assert.Empty(f.Backend.ProfilesValue);
        // «Подробнее» ведёт к уже собранной записи: список с галочками готов.
        Assert.True(vm.HasRows);
        Assert.True(Can(vm.MoreCmd));
        Assert.False(vm.DebugOpen);
        await Run(vm.MoreCmd);
        Assert.True(vm.DebugOpen);   // «Отладка подключений →» раскрывает раздел внизу вкладки
    }

    [Fact]
    public async Task Simple_FiveAttemptsMax()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        f.Backend.VariantsValue = [.. Enumerable.Range(0, 8).Select(i => new ProfileVariant(i, "в" + i, "--dpi-desync=fake --dpi-desync-any-protocol=1 --dpi-desync-cutoff=n3", WinwsArgs.GameUdpDefault))];
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await Run(vm.FinishCmd);
        await Until(() => vm.IsCheck, "вопрос");
        for (int i = 1; i < AppsViewModel.MaxAttempts; i++)
        {
            await Run(vm.NoCmd);
            int n = i + 1;
            await Until(() => f.Backend.Calls.Count(c => c.StartsWith("app_profile_set")) == n && vm.IsCheck, $"попытка {n}");
        }
        Assert.Contains("Попытка 5 из 5", vm.Detail);
        await Run(vm.NoCmd);
        await Until(() => vm.IsFailed, "пять попыток — конец");
        Assert.Contains("за 5 попыток", vm.Status);
        Assert.Equal(AppsViewModel.MaxAttempts, f.Backend.Calls.Count(c => c.StartsWith("app_profile_set")));
    }

    [Fact]
    public async Task Simple_NothingSuitable_SaysSo_AppliesNothing()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        f.Backend.RecValue = FakeHelperBackend.SampleRecording() with
        {
            Flows = [new("udp", "192.168.1.1", 53, 1, "game.exe", true, 3, 1, ""), new("tcp", "198.51.100.9", 443, 2, "msedge.exe", true, 9, 8, "works")],
        };
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await Run(vm.FinishCmd);
        await Until(() => vm.IsFailed, "пустая запись");
        Assert.Contains("Не нашлось подходящих подключений", vm.Status);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("app_profile_set"));
        Assert.Empty(vm.Profiles);
        Assert.True(Can(vm.StartCmd));   // можно повторить
    }

    [Fact]
    public async Task Simple_Cancel_StopsRecording_NothingApplied()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await Run(vm.CancelCmd);
        await Until(() => vm.IsIdle, "отмена");
        Assert.Contains("rec_stop", f.Backend.Calls);
        Assert.False(f.Backend.RecActive);
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("app_profile_set"));
    }

    [Fact]
    public async Task Remove_RestoresAsWas_AndClearsDoneState()
    {
        var (f, vm, said) = await Make_();
        await using var _ = f;
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await Run(vm.FinishCmd);
        await Until(() => vm.IsCheck, "вопрос");
        await Run(vm.YesCmd);
        var row = vm.Profiles.Single();
        await vm.RemoveAsync(row.Id);
        await Until(() => vm.Profiles.Count == 0, "профиль не снят");
        Assert.Empty(f.Backend.ProfilesValue);
        Assert.True(vm.IsIdle);
        Assert.Contains(said, s => s.Contains("всё как было"));
    }

    [Fact]
    public async Task Tick_ShowsTimeAndCount_AndNoteWhenTimedOut()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        await vm.TickAsync();
        Assert.Contains("подключений: 6", vm.RecText);
        // Помощник перезапустился — записи нет: честно говорим, а не висим на «идёт запись».
        f.Backend.RecStop();
        await vm.TickAsync();
        Assert.True(vm.IsFailed);
        Assert.Contains("Запись прервалась", vm.Status);
    }

    [Theory]
    [InlineData(false, false, false)]   // по умолчанию: без драйвера — игры с защитой от читов не видят лишнего драйвера
    [InlineData(true, false, true)]     // человек сам отметил «и UDP»
    [InlineData(false, true, true)]     // оптимизация уже работает — драйвер и так загружен
    public async Task Simple_Driver_OnlyByChoice_OrWhenAlreadyLoaded(bool withDriver, bool running, bool expected)
    {
        var f = new HelperFixture();
        await using var _ = f;
        var vm = new AppsViewModel(await f.LinkAsync()) { Say = _ => { }, OptimizationRunning = () => running };
        Assert.True(vm.AdvNoDriver);
        Assert.False(vm.WithDriver);
        vm.WithDriver = withDriver;
        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись не началась");
        Assert.Contains($"rec_start NewProcesses 0 {expected}", f.Backend.Calls);
    }

    // ---------- «Отладка подключений» ----------

    [Fact]
    public async Task Advanced_Record_ShowsRows_WithAutoChecks_AndPreview()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        Assert.True(vm.AdvNewProcesses);
        vm.AdvNoDriver = false;
        await Run(vm.AdvStartCmd);
        await Until(() => vm.AdvRecording, "запись");
        Assert.Contains("rec_start NewProcesses 0 True", f.Backend.Calls);
        Assert.False(Can(vm.StartCmd));        // простая запись параллельно не стартует
        await Run(vm.AdvStopCmd);
        await Until(() => vm.HasRows, "список");

        Assert.Equal(6, vm.Rows.Count);
        var kept = vm.Rows.Where(r => r.IsChecked).Select(r => $"{r.Kind} {r.Address}:{r.Port}").Order().ToList();
        Assert.Equal(["TCP 198.51.100.7:443", "UDP 203.0.113.50:27015", "UDP 203.0.113.51:27016"], kept);
        // Убранное объясняет причину, а не молчит.
        Assert.Equal("локальная сеть", vm.Rows.Single(r => r.Address == "192.168.1.1").Reason);
        Assert.Equal("системная служба или браузер", vm.Rows.Single(r => r.Process == "chrome.exe").Reason);
        Assert.Equal("процесс был до записи", vm.Rows.Single(r => r.Process == "svchost.exe").Reason);
        Assert.Equal("работает", vm.Rows.Single(r => r.Address == "198.51.100.7").State);
        Assert.Equal("game", vm.ProfileTitle);
        Assert.Equal("TCP 443 · UDP 27015-27016 · адресов: 3", vm.Preview);
        Assert.True(vm.CanSave);
    }

    [Fact]
    public async Task Advanced_ManualChecks_Denoise_Save()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        vm.LoadRows(FakeHelperBackend.SampleRecording());
        await vm.RefreshAsync();

        vm.Rows.Single(r => r.Address == "203.0.113.51").IsChecked = false;
        vm.Rows.Single(r => r.Process == "chrome.exe").IsChecked = true;   // человек хочет и браузер
        Assert.Equal("TCP 443 · UDP 27015 · адресов: 3", vm.Preview);
        // Локальную сеть даже ручной выбор в профиль не пустит.
        vm.Rows.Single(r => r.Address == "192.168.1.1").IsChecked = true;
        Assert.Equal("TCP 443 · UDP 27015 · адресов: 3", vm.Preview);

        await Run(vm.DenoiseCmd);   // «Отсечь шум» возвращает отметки программы
        Assert.Equal("TCP 443 · UDP 27015-27016 · адресов: 3", vm.Preview);
        Assert.False(vm.Rows.Single(r => r.Process == "chrome.exe").IsChecked);

        await Run(vm.UncheckAllCmd);
        Assert.False(vm.CanSave);
        Assert.StartsWith("Отметьте подключения", vm.Preview);
        await Run(vm.CheckAllCmd);
        Assert.True(vm.Rows.All(r => r.IsChecked));

        await Run(vm.DenoiseCmd);
        vm.ProfileTitle = "Моя игра";
        vm.SelectedVariant = 1;
        await Run(vm.SaveCmd);
        await Until(() => vm.Profiles.Count == 1, "профиль не сохранён");
        Assert.Equal("Моя игра", vm.Profiles[0].Title);
        Assert.Equal(1, vm.Profiles[0].Info.Variant);
        // Название, поправленное человеком, при пересчёте не затирается.
        vm.Rows.Single(r => r.Address == "203.0.113.51").IsChecked = false;
        Assert.Equal("Моя игра", vm.ProfileTitle);
    }

    [Fact]
    public async Task Advanced_ByProcess_NeedsChoice_AndSendsPid()
    {
        var (f, vm, said) = await Make_();
        await using var _ = f;
        vm.ListProcesses = () => [new(777, "game.exe", "Моя игра"), new(5, "chrome.exe", "")];
        vm.RefreshProcesses();
        Assert.Equal(2, vm.Processes.Count);
        Assert.Equal("game.exe — Моя игра", vm.Processes[0].Display);
        vm.AdvByProcess = true;
        await Run(vm.AdvStartCmd);
        await Task.Delay(100);
        Assert.False(vm.AdvRecording);                         // процесс не выбран
        Assert.Contains(said, s => s.Contains("Выберите процесс"));
        Assert.DoesNotContain(f.Backend.Calls, c => c.StartsWith("rec_start"));

        vm.SelectedProcess = vm.Processes[0];
        vm.AdvNoDriver = true;
        await Run(vm.AdvStartCmd);
        await Until(() => vm.AdvRecording, "запись по процессу");
        Assert.Contains("rec_start Process 777 False", f.Backend.Calls);
    }

    // ---------- настройка показа ----------

    [Fact]
    public async Task Detail_Request_OpensDebugSection()
    {
        using var t = new TempDir();
        var shell = new Shell(Make.Paths(t));
        var vm = TestWindow.Model(shell);
        Assert.False(vm.Apps.DebugOpen);                // раздел на месте, но свёрнут (UX-аудит 8, §3 №9)
        vm.Apps.LoadRows(FakeHelperBackend.SampleRecording());
        await Run(vm.Apps.MoreCmd);
        Assert.True(vm.Apps.DebugOpen);
    }

    [Fact]
    public void AntiCheatWarning_IsSharedWithGameMode()
    {
        Assert.Contains(NetViewModel.AntiCheatNote, AppsViewModel.AntiCheat);
        Assert.Equal(NetViewModel.AntiCheatNote, AppsViewModel.AntiCheat);
        Assert.Contains("Vanguard", NetViewModel.AntiCheatNote);
        Assert.Contains("FACEIT", NetViewModel.AntiCheatNote);
        Assert.Contains("Easy Anti-Cheat", NetViewModel.AntiCheatNote);
    }

    [Fact]
    public void Texts_AvoidTheWordBypass()
    {
        // Слово «обход» в текстах окна не используется (решение 16): «оптимизация».
        foreach (var s in new[] { AppsViewModel.AntiCheat, InfoTexts.Get(InfoTexts.Apps), InfoTexts.Get(InfoTexts.Debug) })
            Assert.DoesNotContain("обход", s, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>«Добавить профиль…»: окно открывается с пустыми полями; отказ остаётся в окне (оно не закрывается), отмена забывает введённое,
    /// успех закрывает окно, добавляет профиль и даёт итог под списком и в строке состояния.</summary>
    [Fact]
    public async Task AddProfile_Dialog_ErrorStays_CancelForgets_SuccessCloses()
    {
        var (f, vm, said) = await Make_();
        await using var _ = f;
        await vm.RefreshAsync();
        int closes = 0;
        vm.AddClosed += () => closes++;
        vm.ImportFilter = "старое";   // от прошлого раза ничего не остаётся

        vm.AddProfileDialog = a =>
        {
            Assert.Equal("", a.ImportFilter);
            Assert.False(Can(a.ImportCmd));
            a.ImportTitle = "Сломанный";
            a.ImportFilter = "outbound and tcp.DstPort = 1 and tcp.Syn = 1";
            a.ImportParams = "--filter-tcp=1 --dpi-desync=fake";
            a.ImportAsync().GetAwaiter().GetResult();
            Assert.True(a.ShowImportError);
            Assert.False(a.ShowImportResult);
            Assert.Equal(0, closes);
            a.CancelAddCmd.Execute(null);
            Assert.Equal(1, closes);
        };
        vm.OpenAddProfile();
        Assert.Equal("", vm.ImportFilter);
        Assert.Equal("", vm.ImportTitle);
        Assert.False(vm.HasImportMessage);
        Assert.Empty(vm.Profiles);

        vm.AddProfileDialog = a =>
        {
            a.ImportTitle = "Моя игра";
            a.ImportParams = "--filter-udp=27015 --dpi-desync=fake";
            a.ImportSubnets = "203.0.113.0/24";
            a.ImportAsync().GetAwaiter().GetResult();
        };
        vm.OpenAddProfile();
        Assert.Equal(2, closes);
        Assert.Equal("Моя игра", Assert.Single(vm.Profiles).Title);
        Assert.True(vm.ShowImportResult);
        Assert.StartsWith("Профиль «Моя игра» добавлен: свои параметры", vm.ImportMessage);
        Assert.Contains("Профиль «Моя игра» сохранён.", said);
        Assert.Equal("", vm.ImportParams);
    }

    /// <summary>«Записать игру…» открывает карточку записи; во время записи она открыта; «Закрыть» после итога — снова «Начать».</summary>
    [Fact]
    public async Task RecordCard_OpensOnRequest_StaysOpenWhileRecording_CloseResets()
    {
        var (f, vm, _) = await Make_();
        await using var _ = f;
        int requested = 0;
        vm.RecordRequested += () => requested++;
        Assert.False(vm.RecordOpen);
        vm.RecordCmd.Execute(null);
        Assert.True(vm.RecordOpen);
        Assert.Equal(1, requested);
        Assert.True(Can(vm.CloseRecordCmd));
        vm.CloseRecordCmd.Execute(null);
        Assert.False(vm.RecordOpen);

        await Run(vm.StartCmd);
        await Until(() => vm.IsRecording, "запись");
        Assert.True(vm.RecordOpen);
        Assert.False(Can(vm.CloseRecordCmd));
        await Run(vm.FinishCmd);
        await Until(() => vm.IsCheck, "вопрос");
        await Run(vm.YesCmd);
        Assert.True(vm.IsDone);
        vm.CloseRecordCmd.Execute(null);
        Assert.False(vm.RecordOpen);
        Assert.True(vm.IsIdle);
        Assert.False(vm.HasStatus);
    }
}
