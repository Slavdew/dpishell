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
    public string AvailableText => DataAvailable ? $"Доступно обновление списков от {Day(_s.UpdatesData)}" : "";

    /// <summary>Известно, что вышла версия новее (из подписанного описания выпуска или подсказки манифеста данных).</summary>
    public bool AppAvailable => _s.UpdatesApp is { } a && DataUpdateCheck.IsOlder(AppVersion, a);
    /// <summary>Подсказка без проверенного описания выпуска (его не получили): обновить из окна нельзя — только ссылка на страницу релизов.</summary>
    public bool AppHintOnly => AppAvailable && !AppReleaseReady;
    public string AppText => AppHintOnly ? $"Вышла версия {_s.UpdatesApp}. Нажмите «Проверить обновления» — после проверки подлинности появится кнопка обновления." : "";
    /// <summary>Одна главная кнопка по состоянию: выпуск проверен — «Обновить до X», иначе «Проверить обновления».</summary>
    public bool ShowCheckButton => !AppReleaseReady;
    /// <summary>«Скачать программу 99.0» — рядом с «Обновить списки» видно, что это разные вещи (UX-аудит 2, п. 13).</summary>
    public string AppButtonText => $"Скачать программу {_s.UpdatesApp}";

    // ---------- обновление программы кнопкой ----------

    /// <summary>Есть выпуск новее с верной подписью, подходящий этому компьютеру: можно обновить из окна.</summary>
    public bool AppReleaseReady => _appCheck is { Available: true } c && c.Version is { } v && AppRelease.IsOlder(Three(AppVersion), v);
    public string AppReleaseTitle => AppReleaseReady ? $"Вышла версия {_appCheck!.Version} (сейчас {Three(AppVersion)})" : "";
    public string AppUpdateButtonText => $"Обновить до {_appCheck?.Version}";
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
    public string AppOsNote => _appCheck is { Newer: true, OsTooOld: true, Version: { } v } ? $"Версия {v} требует более новую Windows — на этом компьютере её поставить нельзя." : "";
    public bool HasAppOsNote => AppOsNote.Length > 0;
    public bool CanRollback => _canRollback;
    public string RollbackText => $"Вернуть прежнюю версию ({_previousVersion})";

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
            var data = CurrentVersion is { } v ? $"Списки от {Day(v)}" : "Списки из поставки";
            if (_checking) return data + " · проверяю…";
            if (_s.UpdatesCheckedAt is not { } at) return data + " · проверка раз в день при запуске";
            if (_s.UpdatesOkAt is { } ok && ok >= at) return data + $" · проверено {When(ok)}";
            return data + $" · последняя попытка {When(at)} — источники не ответили";
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
            if (c.Errors.Any(x => x.Contains("подпись", StringComparison.Ordinal)))
                SetAppError("Описание выпуска программы не прошло проверку подписи — обновление не предлагается. Скачать программу можно со страницы релизов.", string.Join("; ", c.Errors));
            return null;
        }
        if (!_appBusy) { _appError = ""; _appErrorDetails = ""; }
        _s.UpdatesApp = c.Available ? c.Version : null;
        if (c.Available) return $"Вышла версия {c.Version} — кнопка «Обновить до {c.Version}» на вкладке «Информация и обновления».";
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
                say = c.DataNewer ? $"Доступно обновление списков от {Day(c.DataVersion)}."
                    : c.NeedsNewApp ? "Новые списки требуют новую версию программы — скачайте её со страницы релизов."
                    : c.AppNewer ? $"Списки последние. Вышла версия программы {c.AppVersion}."
                    : "Обновлений нет.";
            }
            else say = "Источники обновлений не ответили — проверю при следующем запуске. " + string.Join("; ", r.Errors);
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
        await Guard("Обновляю списки…", async () =>
        {
            r = await c.UpdateDataAsync(false, progress);
            if (r.NeedsConfirm && Confirm(r.Message + "\n\nПрименить новый IP-список?")) r = await c.UpdateDataAsync(true, progress);
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
            SetAppError("Сейчас идёт другая операция — дождитесь её конца и нажмите «Обновить» снова: после перезапуска она бы оборвалась.");
            return;
        }
        _appNotice = "";
        SetAppError("");
        SetAppBusy(true);
        _appProgress = $"Скачиваю версию {version}…";
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
                _appProgress = $"Скачиваю версию {version}: {mb} МБ";
                Raise(nameof(AppProgressText));
            });
            var prepared = await up.PrepareAsync(check, progress);
            if (!prepared.Ok)
            {
                _appProgress = "";
                SetAppError(prepared.Message, prepared.Details);
                return;
            }
            _appProgress = $"Версия {version} проверена и распакована. Перезапускаю программу…";
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
                SetAppError($"Версия {version} готова, но запустить её не вышло: {StartProblem(e)} Прежняя версия продолжает работать.", e.ToString());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _appProgress = "";
            SetAppError("Обновление не вышло: " + Texts.Friendly(e) + " Программа не изменена.", e.ToString());
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
            SetAppError("Прежней версии не нашлось — её файлы удалены или перенесены.");
            return;
        }
        if (IsBusy())
        {
            SetAppError("Сейчас идёт другая операция — дождитесь её конца и повторите.");
            return;
        }
        if (!Confirm($"Вернуться к версии {up.PreviousVersion}? Программа перезапустится; настройки и списки сохранятся."))
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
                SetAppError("Прежней версии не нашлось — её файлы удалены или перенесены.");
                return;
            }
            _appProgress = $"Возвращаю версию {plan.Version}…";
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
                SetAppError($"Не удалось запустить версию {plan.Version}: {StartProblem(e)} Текущая версия продолжает работать.", e.ToString());
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
        var text = e is System.ComponentModel.Win32Exception ? "Windows не запустила файл — возможно, его заблокировал антивирус." : e.Message;
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
        string? text = failedVersion is not null ? $"Версия {failedVersion} не запустилась, поэтому открыта прежняя версия {now}. Повторить обновление можно кнопкой на этой вкладке."
            : from is null ? null
            : AppRelease.IsOlder(from, now) ? $"Программа обновлена до версии {now}."
            : $"Возвращена версия {now}.";
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
            ? d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : version ?? "";

    static string When(DateTime t) => t.ToString("dd.MM 'в' HH:mm", CultureInfo.InvariantCulture);
}
