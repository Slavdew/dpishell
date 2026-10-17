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
/// хеши и файлы он берёт сам. Новая версия программы — только ссылка на страницу релизов.
/// </summary>
public sealed class UpdatesViewModel : Observable
{
    readonly HelperLink _helper;
    readonly Shell _shell;
    readonly WindowSettings _s;
    readonly string? _packageVersion;
    bool _checking;

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

    public UpdatesViewModel(HelperLink helper, Shell shell, WindowSettings settings)
    {
        _helper = helper;
        _shell = shell;
        _s = settings;
        _packageVersion = shell.Paths.SeedManifest?.Version;
        Need = () => Task.FromResult(helper.Client);
        CheckCmd = RelayCommand.Async(() => CheckAsync(quiet: false), () => !_checking);
        InstallCmd = RelayCommand.Async(InstallAsync, () => !_checking && DataAvailable);
        OpenReleaseCmd = new RelayCommand(OpenRelease);
    }

    public ICommand CheckCmd { get; }
    public ICommand InstallCmd { get; }
    public ICommand OpenReleaseCmd { get; }

    /// <summary>Версия данных в работе: у помощника — его, без помощника — поставки окна.</summary>
    public string? CurrentVersion => _helper.Reader is not null && _helper.Status?.DataVersion is { } v ? v : _packageVersion;

    public bool DataAvailable => _s.UpdatesData is { } v && DataManifest.CompareVersions(v, CurrentVersion) > 0;
    public string AvailableText => DataAvailable ? $"Доступно обновление списков от {Day(_s.UpdatesData)}" : "";

    public bool AppAvailable => _s.UpdatesApp is { } a && DataUpdateCheck.IsOlder(AppVersion, a);
    public string AppText => AppAvailable ? $"Вышла версия программы {_s.UpdatesApp} — скачайте её со страницы релизов." : "";
    /// <summary>«Скачать программу 99.0» — рядом с «Обновить списки» видно, что это разные вещи (UX-аудит 2, п. 13).</summary>
    public string AppButtonText => $"Скачать программу {_s.UpdatesApp}";

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
        foreach (var p in new[] { nameof(CurrentVersion), nameof(DataAvailable), nameof(AvailableText), nameof(AppAvailable), nameof(AppText), nameof(AppButtonText), nameof(StatusText) })
            Raise(p);
        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>При запуске окна: с последней попытки прошли сутки — проверить тихо.</summary>
    public Task CheckIfDueAsync()
    {
        if (Http is null) return Task.CompletedTask;
        if (_s.UpdatesCheckedAt is { } t && Now() - t < Interval && t <= Now()) return Task.CompletedTask;
        return CheckAsync(quiet: true);
    }

    public async Task CheckAsync(bool quiet)
    {
        if (_checking) return;
        _checking = true;
        Refresh();
        try
        {
            using var http = Http?.Invoke() ?? new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
            var client = new DataUpdateClient(http, _shell.UpdateSources);
            var r = await client.FetchManifestAsync();
            _s.UpdatesCheckedAt = Now();
            if (r.Result is { } got)
            {
                _s.UpdatesOkAt = _s.UpdatesCheckedAt;
                var c = DataUpdateCheck.Compare(got.Manifest, CurrentVersion, AppVersion);
                _s.UpdatesData = c.DataNewer ? c.DataVersion : null;
                _s.UpdatesApp = c.AppNewer ? c.AppVersion : null;
                if (!quiet)
                    Say(c.DataNewer ? $"Доступно обновление списков от {Day(c.DataVersion)}."
                        : c.NeedsNewApp ? "Новые списки требуют новую версию программы — скачайте её со страницы релизов."
                        : c.AppNewer ? $"Списки последние. Вышла версия программы {c.AppVersion}."
                        : "Обновлений нет.");
            }
            else if (!quiet) Say("Источники обновлений не ответили — проверю при следующем запуске. " + string.Join("; ", r.Errors));
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
