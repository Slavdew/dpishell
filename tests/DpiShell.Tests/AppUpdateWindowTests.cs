using DpiShell.App;
using DpiShell.Core;
using static DpiShell.Tests.AppUpdateTests;

namespace DpiShell.Tests;

/// <summary>
/// «Обновить до X» на вкладке «Информация и обновления» (модель окна, без экрана): проверка подписанного описания выпуска,
/// «Что нового», ход и ошибки понятными фразами, подмена архива, неверная подпись, нехватка места, неудачный перезапуск, откат.
/// Настоящий перезапуск и сеть не используются: Restart — запись плана, HTTP — MapHandler.
/// </summary>
public sealed class AppUpdateWindowTests
{
    const string Portable = "portable\\DpiShell.exe";

    sealed class Rig : IDisposable
    {
        public readonly TempDir T = new();
        public readonly MapHandler H;
        public readonly AppVersionStore Store;
        public readonly UpdatesViewModel Vm;
        public readonly List<RestartPlan> Restarts = [];
        public readonly List<string> Said = [];
        public bool RestartFails;
        public long Free = 5L * 1024 * 1024 * 1024;
        public string Exe => T.Sub(Portable);

        public Rig(MapHandler h, string current = "1.0.0")
        {
            H = h;
            var shell = new Shell(new AppPaths(T.Sub("root"), Repo.Data));
            var settings = TestWindow.Settings(shell);
            Store = new AppVersionStore(Path.Combine(Path.GetDirectoryName(settings.File)!, "versions"));
            Vm = new UpdatesViewModel(TestWindow.NoHelper(), shell, settings)
            {
                Http = () => new HttpClient(H, disposeHandler: false),
                AppVersion = current + ".0",
                Say = s => { lock (Said) Said.Add(s); },
                Confirm = _ => true,
                Restart = plan =>
                {
                    if (RestartFails) throw new InvalidOperationException("Не удалось запустить файл.");
                    Restarts.Add(plan);
                    return Task.CompletedTask;
                },
            };
            Vm.CreateUpdater = http => new AppUpdater(
                new AppUpdateClient(http, [A]) { PerSourceTimeout = TimeSpan.FromMilliseconds(300), SigningKey = Key.PublicKey, OsVersion = new Version(10, 0, 26200) }, Store)
            { CurrentVersion = current, CurrentExe = Exe, CurrentPid = 4242, FreeSpace = _ => Free };
            Settings = settings;
        }

        public WindowSettings Settings { get; }

        public void Dispose() => T.Dispose();

        public async Task Wait(Func<bool> done)
        {
            for (int i = 0; i < 200 * 10 && !done(); i++) await Task.Delay(25);
            Assert.True(done(), "не дождались: " + Vm.AppProgressText + " | " + Vm.AppError);
        }

        public async Task UpdateAsync()
        {
            Vm.UpdateAppCmd.Execute(null);
            await Wait(() => !Vm.AppBusy || Restarts.Count > 0);
        }
    }

    static MapHandler Release(string version = "1.2.0") => Serve(new MapHandler(), A, version);

    [Fact]
    public async Task Check_SignedRelease_ShowsWhatsNew_AndButton()
    {
        using var r = new Rig(Release());
        await r.Vm.CheckAsync(quiet: false);
        Assert.True(r.Vm.AppReleaseReady);
        Assert.False(r.Vm.AppHintOnly);
        Assert.Equal("Обновить до 1.2.0", r.Vm.AppUpdateButtonText);
        Assert.Equal(["Быстрее подбор", "Исправлен трей"], r.Vm.AppNotes);   // «Что нового» видно до нажатия
        Assert.Contains("1.2.0", r.Vm.AppReleaseTitle);
        Assert.Equal("1.2.0", r.Settings.UpdatesApp);
        Assert.False(r.Vm.HasAppError);
        Assert.Contains(r.Said, s => s == L.T("about.app.found_new", new { version = "1.2.0" }));
        // Работающая программа и её папка не тронуты, пока человек не нажал.
        Assert.False(r.Store.IsInstalled("1.2.0"));
        Assert.Empty(r.Restarts);
    }

    [Fact]
    public async Task Check_AlreadyNewest_NoButton_AndOldHintCleared()
    {
        using var r = new Rig(Release("1.0.0"), current: "1.0.0");
        r.Settings.UpdatesApp = "9.0";
        await r.Vm.CheckAsync(quiet: true);
        Assert.False(r.Vm.AppReleaseReady);
        Assert.False(r.Vm.AppAvailable);
        Assert.Null(r.Settings.UpdatesApp);
    }

    [Fact]
    public async Task Check_BadSignature_ExplainsAndOffersNothing()
    {
        // Выпуск подписан чужим ключом: обновление не предлагается, человеку сказано почему.
        using var r = new Rig(Serve(new MapHandler(), A, "1.2.0", by: new TestSigner()));
        await r.Vm.CheckAsync(quiet: false);
        Assert.False(r.Vm.AppReleaseReady);
        Assert.True(r.Vm.HasAppError);
        Assert.Contains("не прошло проверку подписи", r.Vm.AppError);
        Assert.Contains("подпись", r.Vm.AppErrorDetails);
        Assert.False(r.Vm.UpdateAppCmd.CanExecute(null));
    }

    /// <summary>Описание выпуска с верной подписью, но старое (заморозка подменённым источником): не «обновлений нет», а «не удалось проверить».</summary>
    [Fact]
    public async Task Check_StaleRelease_SaysCouldNotCheck()
    {
        var rel = System.Text.Json.Nodes.JsonNode.Parse(ReleaseJson("1.0.0", GoodZip("1.0.0")))!.AsObject();
        rel["issued"] = "2020-01-01T00:00:00Z";
        using var r = new Rig(Serve(new MapHandler(), A, "1.0.0", release: System.Text.Encoding.UTF8.GetBytes(rel.ToJsonString())));
        await r.Vm.CheckAsync(quiet: false);
        Assert.False(r.Vm.AppReleaseReady);
        Assert.True(r.Vm.HasAppError);
        Assert.Equal(L.T("about.app.stale", new { days = ReleaseFreshness.MaxAgeDays }), r.Vm.AppError);
        Assert.Contains("старше 120 дней", r.Vm.AppErrorDetails);
    }

    [Fact]
    public async Task Check_NoSourceAnswers_KeepsManifestHint_AsLink()
    {
        using var r = new Rig(new MapHandler());
        r.Settings.UpdatesApp = "1.2.0";
        await r.Vm.CheckAsync(quiet: true);
        Assert.False(r.Vm.AppReleaseReady);
        Assert.True(r.Vm.AppHintOnly);          // запасной путь: ссылка на страницу релизов
        Assert.False(r.Vm.HasAppError);
    }

    [Fact]
    public async Task Update_Succeeds_RestartsIntoNewCopy_AndKeepsOldForRollback()
    {
        using var r = new Rig(Release());
        await r.Vm.CheckAsync(quiet: true);
        await r.UpdateAsync();

        var plan = Assert.Single(r.Restarts);
        Assert.Equal(r.Store.ExeFor("1.2.0"), plan.Exe);
        Assert.Equal(["--wait-pid", "4242", "--updated-from", "1.0.0"], plan.Args);
        Assert.Equal("exe 1.2.0", File.ReadAllText(plan.Exe));
        Assert.Equal(new AppVersionsState("1.2.0", "1.0.0", r.Exe, Pending: "1.2.0"), r.Store.State);
        Assert.True(r.Vm.AppBusy);                                  // окно закрывается — кнопки не оживают
        Assert.Equal(L.T("about.app.restarting", new { version = "1.2.0" }), r.Vm.AppProgressText);
        Assert.False(r.Vm.HasAppError);
        Assert.Empty(Directory.EnumerateFiles(r.Store.Root, "*.part"));
    }

    [Fact]
    public async Task Update_SwappedArchive_Refuses_AndChangesNothing()
    {
        // Описание подписано верно, но архив на сервере подменён.
        var h = Serve(new MapHandler(), A, "1.2.0").Ok(A + AppRelease.PortableName("1.2.0"), GoodZip("evil"));
        using var r = new Rig(h);
        await r.Vm.CheckAsync(quiet: true);
        await r.UpdateAsync();

        Assert.Empty(r.Restarts);
        Assert.True(r.Vm.HasAppError);
        Assert.Contains("не прошёл проверку", r.Vm.AppError);
        Assert.Contains("программа не изменена", r.Vm.AppError);
        Assert.Contains("sha256", r.Vm.AppErrorDetails);
        Assert.False(r.Store.IsInstalled("1.2.0"));
        Assert.Equal(new AppVersionsState(), r.Store.State);
        Assert.False(r.Vm.AppBusy);
        Assert.True(r.Vm.UpdateAppCmd.CanExecute(null));            // можно повторить
    }

    [Fact]
    public async Task Update_NotEnoughSpace_SaysHowMuch()
    {
        using var r = new Rig(Release()) { Free = 80L * 1024 * 1024 };
        await r.Vm.CheckAsync(quiet: true);
        r.H.Requests.Clear();
        await r.UpdateAsync();
        Assert.Contains("Не хватает места", r.Vm.AppError);
        Assert.Contains("80 МБ", r.Vm.AppError);
        Assert.DoesNotContain(r.H.Requests, x => x.EndsWith(".zip"));
        Assert.Empty(r.Restarts);

        // Освободили — то же действие проходит без новой проверки.
        r.Free = 5L * 1024 * 1024 * 1024;
        await r.UpdateAsync();
        Assert.Single(r.Restarts);
    }

    [Fact]
    public async Task Update_NoConnection_FriendlyMessage()
    {
        var h = Release().Fail(A + AppRelease.PortableName("1.2.0"));
        using var r = new Rig(h);
        await r.Vm.CheckAsync(quiet: true);
        await r.UpdateAsync();
        Assert.True(r.Vm.HasAppError);
        Assert.Contains("программа не изменена", r.Vm.AppError);
        Assert.Empty(r.Restarts);
    }

    [Fact]
    public async Task Update_RestartFails_RestoresState_PreviousKeepsRunning()
    {
        using var r = new Rig(Release()) { RestartFails = true };
        await r.Vm.CheckAsync(quiet: true);
        await r.UpdateAsync();
        var parts = L.T("about.app.start_failed", new { version = "1.2.0", reason = "\u0001" }).Split('\u0001');   // до и после причины
        Assert.StartsWith(parts[0], r.Vm.AppError);
        Assert.EndsWith(parts[1].Trim(), r.Vm.AppError);
        Assert.Equal(new AppVersionsState(), r.Store.State);        // запись о переключении откатена
        Assert.False(r.Vm.AppBusy);
    }

    [Fact]
    public async Task Update_WhileAnotherOperation_Waits()
    {
        using var r = new Rig(Release());
        await r.Vm.CheckAsync(quiet: true);
        r.Vm.IsBusy = () => true;
        await r.UpdateAsync();
        Assert.Contains("другая операция", r.Vm.AppError);
        Assert.False(r.Store.IsInstalled("1.2.0"));
        Assert.Empty(r.Restarts);
    }

    [Fact]
    public async Task Rollback_ButtonStartsPreviousCopy_AfterConfirm()
    {
        // Работает 1.2.0 (после обновления), прежняя 1.0.0 лежит в исходной portable-папке.
        using var r = new Rig(new MapHandler(), current: "1.2.0");
        var prevExe = r.T.Sub("old\\DpiShell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(prevExe)!);
        File.WriteAllText(prevExe, "exe 1.0.0");
        r.Store.Save(new("1.2.0", "1.0.0", prevExe));
        r.Vm.AnnounceStart(null, null);                             // перечитать наличие отката
        Assert.True(r.Vm.CanRollback);
        Assert.Equal(L.T("about.app.rollback_button", new { version = "1.0.0" }), r.Vm.RollbackText);

        r.Vm.RollbackCmd.Execute(null);
        await r.Wait(() => r.Restarts.Count > 0);
        Assert.Equal(prevExe, r.Restarts[0].Exe);
        Assert.Equal(["--wait-pid", "4242", "--updated-from", "1.2.0"], r.Restarts[0].Args);
        // Из 1.0.0 можно вернуться обратно на 1.2.0; ожидания подтверждения у уже проверенной прежней нет.
        Assert.Equal(new AppVersionsState("1.0.0", "1.2.0", r.Exe), r.Store.State);
    }

    [Fact]
    public async Task Rollback_Declined_ChangesNothing()
    {
        using var r = new Rig(new MapHandler(), current: "1.2.0");
        var prevExe = r.T.Sub("old\\DpiShell.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(prevExe)!);
        File.WriteAllText(prevExe, "exe 1.0.0");
        r.Store.Save(new("1.2.0", "1.0.0", prevExe));
        r.Vm.AnnounceStart(null, null);
        Assert.True(r.Vm.CanRollback);
        r.Vm.Confirm = _ => false;
        r.Vm.RollbackCmd.Execute(null);
        await Task.Delay(100);
        Assert.Empty(r.Restarts);
        Assert.Equal(new AppVersionsState("1.2.0", "1.0.0", prevExe), r.Store.State);
    }

    [Fact]
    public void Rollback_PreviousGone_ButtonHidden()
    {
        using var r = new Rig(new MapHandler(), current: "1.2.0");
        r.Store.Save(new("1.2.0", "1.0.0", r.T.Sub("nowhere\\DpiShell.exe")));
        r.Vm.AnnounceStart(null, null);
        Assert.False(r.Vm.CanRollback);
        Assert.False(r.Vm.RollbackCmd.CanExecute(null));
    }

    [Fact]
    public void AnnounceStart_Texts_ForUpdateRollbackAndFailedStart()
    {
        using var r = new Rig(new MapHandler(), current: "1.2.0");
        r.Vm.AnnounceStart("1.0.0", null);
        Assert.Equal(L.T("about.app.updated", new { version = "1.2.0" }), r.Vm.AppNotice);
        Assert.Contains(L.T("about.app.updated", new { version = "1.2.0" }), r.Said);

        r.Vm.AnnounceStart("1.3.0", null);
        Assert.Equal(L.T("about.app.rolled_back", new { version = "1.2.0" }), r.Vm.AppNotice);

        r.Vm.AnnounceStart("1.3.0", "1.3.0");
        Assert.Equal(L.T("about.app.start_failed_back", new { version = "1.3.0", current = "1.2.0" }), r.Vm.AppNotice);

        r.Vm.AnnounceStart(null, null);
        Assert.False(r.Vm.HasAppNotice);
    }

    [Fact]
    public async Task Texts_NewStrings_DoNotUseOldWord_AndPromiseNothingToEveryone()
    {
        using var r = new Rig(Release());
        await r.Vm.CheckAsync(quiet: false);
        var all = string.Join("\n", r.Said.Append(r.Vm.AppReleaseTitle).Append(r.Vm.AppUpdateButtonText).Append(r.Vm.RollbackText).Append(r.Vm.AppText));
        Assert.DoesNotContain("обход", all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("у всех", all, StringComparison.OrdinalIgnoreCase);
    }
}
