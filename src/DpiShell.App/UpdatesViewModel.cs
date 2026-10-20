using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// Обновления (architecture.md «Обновления данных»): раз в день при запуске окна тихо проверяет data.json у источников (окно качает манифест само —
/// права не нужны), показывает «Доступно обновление списков» и кнопку. Ставит помощник командой data_update: адреса,
/// хеши и файлы он берёт сам.
/// Программа (architecture.md «Обновление программы»): окно само проверяет подписанный release.json, показывает «Что нового»,
/// по кнопке «Обновить до X» качает архив, сверяет sha256, распаковывает рядом (<c>%LocalAppData%\DpiShell\versions\X.Y.Z</c>)
/// и перезапускается в новую копию; прежняя остаётся для «Вернуть прежнюю версию». Права администратора и помощник не нужны:
/// папка программы не меняется. Не удалось получить подписанное описание — остаётся ссылка на страницу релизов.
/// </summary>
public sealed class UpdatesViewModel : Observable
{
    readonly HelperLink _helper;
    readonly Shell _shell;
    readonly WindowSettings _s;
    readonly string? _packageVersion;
    bool _checking;
    AppUpdateCheck? _appCheck;
    bool _appBusy, _appCheckedThisSession, _canRollback;
    string _appProgress = "", _appError = "", _appErrorDetails = "", _appNotice = "", _previousVersion = "";

    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    /// <summary>HTTP для проверки; null — проверка выключена (тесты окна без сети). App ставит настоящий.</summary>
    public Func<HttpClient>? Http { get; set; }
    public Func<string, Func<Task>, Task<bool>> Guard { get; set; } = async (_, w) => { await w(); return true; };
    public Func<Task<HelperClient?>> Need { get; set; }
    public Action<string> Say { get; set; } = _ => { };
    public Func<string, bool> Confirm { get; set; } = _ => false;
    /// <summary>После установки — перечитать у помощника (каталог, списки).</summary>
    public Func<Task> AfterInstall { get; set; } = () => Task.CompletedTask;
    public Func<DateTime> Now { get; set; } = () => DateTime.Now;
    public string AppVersion { get; set; } = HelperProtocol.AppVersion;
    /// <summary>Открыть страницу релизов (в тестах — подмена).</summary>
    public Action<string> OpenUrl { get; set; } = url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    /// <summary>Запустить новую копию и выйти из этой (окно подключает свой выход; в тестах — подмена).</summary>
    public Func<RestartPlan, Task> Restart { get; set; } = _ => throw new InvalidOperationException("Перезапуск не подключён.");
    /// <summary>Идёт другая операция окна (подбор, включение) — обновляться сейчас нельзя: выход оборвал бы её.</summary>
    public Func<bool> IsBusy { get; set; } = () => false;
    /// <summary>Сборка обновляющего по HTTP; null — по умолчанию (источники из поставки, ключ выпусков, папка versions рядом с настройками окна).</summary>
    public Func<HttpClient, AppUpdater>? CreateUpdater { get; set; }

    public UpdatesViewModel(HelperLink helper, Shell shell, WindowSettings settings)
    {
        _helper = helper;
        _shell = shell;
        _s = settings;
        _packageVersion = shell.Paths.SeedManifest?.Version;
        Need = () => Task.FromResult(helper.Client);
        CheckCmd = RelayCommand.Async(() => CheckAsync(quiet: false), () => !_checking && !_appBusy);
        InstallCmd = RelayCommand.Async(InstallAsync, () => !_checking && DataAvailable);
        OpenReleaseCmd = new RelayCommand(OpenRelease);
        UpdateAppCmd = RelayCommand.Async(UpdateAppAsync, () => AppReleaseReady && !_appBusy && !_checking);
        RollbackCmd = RelayCommand.Async(RollbackAsync, () => _canRollback && !_appBusy);
        RetryCmd = RelayCommand.Async(() => AppReleaseReady ? UpdateAppAsync() : CheckAsync(quiet: false), () => !_appBusy && !_checking);
        RefreshRollback();
    }

    public ICommand CheckCmd { get; }
    public ICommand InstallCmd { get; }
    public ICommand OpenReleaseCmd { get; }
    public ICommand UpdateAppCmd { get; }
    public ICommand RollbackCmd { get; }
    /// <summary>«Повторить» под ошибкой: выпуск проверен — снова обновить, иначе снова проверить.</summary>
    public ICommand RetryCmd { get; }

    /// <summary>Версия данных в работе: у помощника — его, без помощника — поставки окна.</summary>
    public string? CurrentVersion => _helper.Reader is not null && _helper.Status?.DataVersion is { } v ? v : _packageVersion;

    public bool DataAvailable => _s.UpdatesData is { } v && DataManifest.CompareVersions(v, CurrentVersion) > 0;
    public string AvailableText => DataAvailable ? L.T("about.lists.available", new { date = Day(_s.UpdatesData) }) : "";

    /// <summary>Известно, что вышла версия новее (из подписанного описания выпуска или подсказки манифеста данных).</summary>
    public bool AppAvailable => _s.UpdatesApp is { } a && DataUpdateCheck.IsOlder(AppVersion, a);
    /// <summary>Подсказка без проверенного описания выпуска (его не получили): обновить из окна нельзя — только ссылка на страницу релизов.</summary>
    public bool AppHintOnly => AppAvailable && !AppReleaseReady;
    public string AppText => AppHintOnly ? L.T("about.app.hint_only", new { version = _s.UpdatesApp }) : "";
    /// <summary>Одна главная кнопка по состоянию: выпуск проверен — «Обновить до X», иначе «Проверить обновления».</summary>
    public bool ShowCheckButton => !AppReleaseReady;
    /// <summary>«Скачать программу 99.0» — рядом с «Обновить списки» видно, что это разные вещи (UX-аудит 2, п. 13).</summary>
    public string AppButtonText => L.T("about.app.download_button", new { version = _s.UpdatesApp });

    // ---------- обновление программы кнопкой ----------

    /// <summary>Есть выпуск новее с верной подписью, подходящий этому компьютеру: можно обновить из окна.</summary>
    public bool AppReleaseReady => _appCheck is { Available: true } c && c.Version is { } v && AppRelease.IsOlder(Three(AppVersion), v);
    public string AppReleaseTitle => AppReleaseReady ? L.T("about.app.release_title", new { version = _appCheck!.Version, current = Three(AppVersion) }) : "";
    public string AppUpdateButtonText => L.T("about.app.update_button", new { version = _appCheck?.Version });
    /// <summary>«Что нового» — из подписанного описания выпуска, показывается до обновления.</summary>
    public IReadOnlyList<string> AppNotes => AppReleaseReady ? _appCheck!.Changes : [];
    public bool HasAppNotes => AppNotes.Count > 0;
    public bool AppBusy => _appBusy;
    public string AppProgressText => _appProgress;
    public string AppError => _appError;
    public bool HasAppError => _appError.Length > 0;
    public string AppErrorDetails => _appErrorDetails;
    /// <summary>В красной строке — только первая фраза; остальное и технический хвост — под «Подробнее».</summary>
    public string AppErrorShort => FirstSentence(_appError);
    public string AppErrorMore
    {
        get
        {
            var rest = _appError.Length > AppErrorShort.Length ? _appError[AppErrorShort.Length..].TrimStart('.', ' ') : "";
            return string.Join(Environment.NewLine, new[] { rest, _appErrorDetails }.Where(x => x.Length > 0));
        }
    }
    public bool HasAppErrorMore => AppErrorMore.Length > 0;

    /// <summary>До первой точки с пробелом (имена файлов и версии с точками не режутся) или двоеточия, если фраза длинная.</summary>
    internal static string FirstSentence(string text)
    {
        var i = text.IndexOf(". ", StringComparison.Ordinal);
        var first = i < 0 ? text : text[..(i + 1)];
        if (first.Length > 110 && first.IndexOf(": ", StringComparison.Ordinal) is > 0 and var c) first = first[..c];
        return first.TrimEnd();
    }
    public bool HasAppErrorDetails => _appErrorDetails.Length > 0;
    /// <summary>Итог прошлого запуска: «Программа обновлена до …», «Возвращена версия …».</summary>
    public string AppNotice => _appNotice;
    public bool HasAppNotice => _appNotice.Length > 0;
    /// <summary>Новая версия есть, но требует более новую Windows.</summary>
    public string AppOsNote => _appCheck is { Newer: true, OsTooOld: true, Version: { } v } ? L.T("about.app.os_too_old", new { version = v }) : "";
    public bool HasAppOsNote => AppOsNote.Length > 0;
    public bool CanRollback => _canRollback;
    public string RollbackText => L.T("about.app.rollback_button", new { version = _previousVersion });

    /// <summary>«0.9.2.0» → «0.9.2»: версии выпусков — X.Y.Z.</summary>
    static string Three(string v) => System.Version.TryParse(v, out var x) ? $"{x.Major}.{x.Minor}.{Math.Max(x.Build, 0)}" : v;

    static readonly Lazy<HttpClient> StateOnlyHttp = new(() => new HttpClient());

    AppUpdater BuildUpdater(HttpClient http) =>
        CreateUpdater?.Invoke(http) ?? new AppUpdater(
            new AppUpdateClient(http, _shell.UpdateSources.AppSources) { SigningKey = _shell.DataSigningKey },
            new AppVersionStore(Path.Combine(Path.GetDirectoryName(_s.File) ?? WindowSettings.DefaultDir, "versions")))
        { CurrentVersion = Three(AppVersion) };

    void RefreshRollback()
    {
        try
        {
            var up = BuildUpdater(StateOnlyHttp.Value);
            _canRollback = up.CanRollback;
            _previousVersion = up.PreviousVersion ?? "";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { _canRollback = false; _previousVersion = ""; }
    }

    void SetAppBusy(bool busy)
    {
        _appBusy = busy;
        Raise(nameof(AppBusy));
        Refresh();
    }

    void SetAppError(string text, string? details = null)
    {
        _appError = text;
        _appErrorDetails = details ?? "";
        Refresh();
    }

    public string StatusText
    {
        get
        {
            var lists = CurrentVersion is { } v ? L.T("about.status.lists_dated", new { date = Day(v) }) : L.T("about.status.lists_bundled");
            if (_checking) return L.T("about.status.checking", new { lists });
            if (_s.UpdatesCheckedAt is not { } at) return L.T("about.status.daily", new { lists });
            if (_s.UpdatesOkAt is { } ok && ok >= at) return L.T("about.status.ok", new { lists, when = When(ok) });
            return L.T("about.status.failed", new { lists, when = When(at) });
        }
    }

    /// <summary>Пересчитать строки (помощник ответил, версия данных у него поменялась).</summary>
    public void Refresh()
    {
        foreach (var p in new[] { nameof(CurrentVersion), nameof(DataAvailable), nameof(AvailableText), nameof(AppAvailable), nameof(AppHintOnly), nameof(AppText), nameof(AppButtonText), nameof(StatusText),
                                  nameof(AppReleaseReady), nameof(AppReleaseTitle), nameof(AppUpdateButtonText), nameof(AppNotes), nameof(HasAppNotes), nameof(AppProgressText),
                                  nameof(AppError), nameof(HasAppError), nameof(AppErrorDetails), nameof(HasAppErrorDetails), nameof(AppErrorShort), nameof(AppErrorMore), nameof(HasAppErrorMore), nameof(ShowCheckButton), nameof(AppNotice), nameof(HasAppNotice),
                                  nameof(AppOsNote), nameof(HasAppOsNote), nameof(CanRollback), nameof(RollbackText) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>При запуске окна: с последней попытки прошли сутки — проверить тихо.</summary>
    public Task CheckIfDueAsync()
    {
        if (Http is null) return Task.CompletedTask;
        if (_s.UpdatesCheckedAt is { } t && Now() - t < Interval && t <= Now())
            // Не пора, но о новой версии уже знаем, а описание выпуска в этом запуске не получали (после перезапуска «Что нового» пропало) — получить.
            return AppAvailable && !_appCheckedThisSession ? CheckAppOnlyAsync() : Task.CompletedTask;
        return CheckAsync(quiet: true);
    }

    async Task CheckAppOnlyAsync()
    {
        if (_checking) return;
        _checking = true;
        Refresh();
        try
        {
            using var http = Http!.Invoke();
            await CheckAppAsync(http);
            try { _s.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    /// <summary>
    /// Проверить подписанное описание выпуска программы. Возвращает фразу для итога проверки (или null).
    /// Подпись не сошлась — выпуск не предлагается; источники молчат — остаётся подсказка манифеста (ссылка на страницу релизов).
    /// </summary>
    async Task<string?> CheckAppAsync(HttpClient http)
    {
        _appCheckedThisSession = true;
        AppUpdateCheck c;
        try { c = await BuildUpdater(http).CheckAsync(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        _appCheck = c;
        if (c.Found is null)
        {
            // Подпись не сошлась — не молчать: человек должен знать, почему обновления нет.
            if (c.SignatureFailed)
                SetAppError(L.T("about.app.signature_failed"), string.Join("; ", c.Errors));
            return null;
        }
        if (!_appBusy) { _appError = ""; _appErrorDetails = ""; }
        _s.UpdatesApp = c.Available ? c.Version : null;
        if (c.Available) return L.T("about.app.found_new", new { version = c.Version });
        return c.OsTooOld ? AppOsNote : null;
    }

    public async Task CheckAsync(bool quiet)
    {
        if (_checking) return;
        _checking = true;
        Refresh();
        try
        {
            using var http = Http?.Invoke() ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new DataUpdateClient(http, _shell.UpdateSources) { SigningKey = _shell.DataSigningKey };
            var r = await client.FetchManifestAsync();
            _s.UpdatesCheckedAt = Now();
            string? say = null;
            if (r.Result is { } got)
            {
                _s.UpdatesOkAt = _s.UpdatesCheckedAt;
                var c = DataUpdateCheck.Compare(got.Manifest, CurrentVersion, AppVersion);
                _s.UpdatesData = c.DataNewer ? c.DataVersion : null;
                _s.UpdatesApp = c.AppNewer ? c.AppVersion : null;
                say = c.DataNewer ? L.T("about.check.lists_new", new { date = Day(c.DataVersion) })
                    : c.NeedsNewApp ? L.T("about.check.needs_new_app")
                    : c.AppNewer ? L.T("about.check.app_newer", new { version = c.AppVersion })
                    : L.T("about.check.no_updates");
            }
            else say = L.T("about.check.sources_failed", new { errors = string.Join("; ", r.Errors) });
            // Программа — отдельно от данных: у неё свой подписанный release.json (подсказка манифеста остаётся запасной).
            var app = await CheckAppAsync(http);
            if (!quiet) Say(app is null ? say : DataAvailable ? say + " " + app : app);
            try { _s.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        finally
        {
            _checking = false;
            Refresh();
        }
    }

    async Task InstallAsync()
    {
        if (await Need() is not { } c) return;
        DataUpdateResult? r = null;
        var progress = new Progress<string>(Say);
        await Guard(L.T("about.lists.busy"), async () =>
        {
            r = await c.UpdateDataAsync(false, progress);
            if (r.NeedsConfirm && Confirm(L.T("about.lists.confirm_ip", new { message = r.Message }))) r = await c.UpdateDataAsync(true, progress);
            await AfterInstall();
        });
        if (r is null) return;
        if (r.Applied || r.UpToDate)
        {
            _s.UpdatesData = null;
            try { _s.Save(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Refresh();
        Say(r.Message);
    }

    /// <summary>
    /// «Обновить до X»: скачать архив → сверить sha256 с подписанным описанием → распаковать рядом → запустить новую копию и выйти.
    /// Работающая программа до перезапуска не меняется; любая неудача — понятная фраза на вкладке и прежняя версия продолжает работать.
    /// </summary>
    async Task UpdateAppAsync()
    {
        if (_appCheck is not { Available: true } check || check.Version is not { } version || _appBusy) return;
        if (IsBusy())
        {
            SetAppError(L.T("about.app.busy_update"));
            return;
        }
        _appNotice = "";
        SetAppError("");
        SetAppBusy(true);
        _appProgress = L.T("about.app.downloading", new { version });
        Refresh();
        bool restarted = false;
        try
        {
            using var http = Http?.Invoke() ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
            var up = BuildUpdater(http);
            long shown = -1;
            var progress = new SyncProgress<long>(bytes =>
            {
                var mb = bytes / (1024 * 1024);
                if (mb == shown) return;
                shown = mb;
                _appProgress = L.T("about.app.downloading_mb", new { version, mb });
                Raise(nameof(AppProgressText));
            });
            var prepared = await up.PrepareAsync(check, progress);
            if (!prepared.Ok)
            {
                _appProgress = "";
                SetAppError(prepared.Message, prepared.Details);
                return;
            }
            _appProgress = L.T("about.app.restarting", new { version });
            Refresh();
            var before = up.Store.State;
            var plan = up.RestartInto(version);
            try
            {
                await Restart(plan);
                restarted = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Новая копия не запустилась — вернуть запись о переключении: прежняя версия продолжает работать.
                up.Store.Save(before);
                _appProgress = "";
                SetAppError(L.T("about.app.start_failed", new { version, reason = StartProblem(e) }), e.ToString());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _appProgress = "";
            SetAppError(L.T("about.app.update_failed", new { error = Texts.Friendly(e) }), e.ToString());
        }
        finally
        {
            RefreshRollback();
            // Удачный перезапуск: окно уже закрывается, кнопки не оживляем.
            if (!restarted) SetAppBusy(false);
        }
    }

    /// <summary>«Вернуть прежнюю версию»: запустить сохранённую рядом копию (запись — в versions\state.json) и выйти.</summary>
    async Task RollbackAsync()
    {
        if (_appBusy) return;
        var up = BuildUpdater(StateOnlyHttp.Value);
        if (!up.CanRollback)
        {
            RefreshRollback();
            SetAppError(L.T("about.app.no_previous"));
            return;
        }
        if (IsBusy())
        {
            SetAppError(L.T("about.app.busy_retry"));
            return;
        }
        if (!Confirm(L.T("about.app.rollback_confirm", new { version = up.PreviousVersion })))
            return;
        SetAppError("");
        _appNotice = "";
        SetAppBusy(true);
        bool restarted = false;
        try
        {
            var before = up.Store.State;
            if (up.Rollback() is not { } plan)
            {
                SetAppError(L.T("about.app.no_previous"));
                return;
            }
            _appProgress = L.T("about.app.rolling_back", new { version = plan.Version });
            Refresh();
            try
            {
                await Restart(plan);
                restarted = true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                up.Store.Save(before);
                _appProgress = "";
                SetAppError(L.T("about.app.rollback_start_failed", new { version = plan.Version, reason = StartProblem(e) }), e.ToString());
            }
        }
        finally
        {
            RefreshRollback();
            if (!restarted) SetAppBusy(false);
        }
    }

    /// <summary>Почему не запустилась другая копия: Win32Exception — словами (антивирус, повреждённый файл), остальное — как есть.</summary>
    static string StartProblem(Exception e)
    {
        var text = e is System.ComponentModel.Win32Exception ? L.T("about.app.start_problem_win32") : e.Message;
        return text.EndsWith('.') ? text : text + ".";
    }

    sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>
    /// Запуск после обновления или отката (App передаёт аргументы <c>--updated-from</c>): сообщить итог один раз.
    /// <paramref name="failedVersion"/> — версия, которая не запустилась, и прежняя вернулась сама (<see cref="AppUpdater.TakeFailedNotice"/>).
    /// </summary>
    public void AnnounceStart(string? from, string? failedVersion)
    {
        var now = Three(AppVersion);
        string? text = failedVersion is not null ? L.T("about.app.start_failed_back", new { version = failedVersion, current = now })
            : from is null ? null
            : AppRelease.IsOlder(from, now) ? L.T("about.app.updated", new { version = now })
            : L.T("about.app.rolled_back", new { version = now });
        _appNotice = text ?? "";
        RefreshRollback();
        Refresh();
        if (text is not null) Say(text);
    }

    void OpenRelease()
    {
        if (_shell.UpdateSources.ReleasePage is not { } url) return;
        try { OpenUrl(url); }
        catch (Exception e) { Say(Texts.Friendly(e)); }
    }

    static string Day(string? version) =>
        version is not null && DateOnly.TryParseExact(version.Split('.')[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? L.Date(d.ToDateTime(TimeOnly.MinValue)) : version ?? "";

    static string When(DateTime t) => L.T("about.when", new { date = L.DayMonth(t), time = L.Time(t) });
}
