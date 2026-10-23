using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// «Диагностика» → «Сохранить отчёт»: один текстовый файл для обращения за помощью (<see cref="SupportReport"/>). Данные — те, что окно
/// уже держит (без новых запросов к системе), плюс хвосты журналов помощника (команда log_tail) и окна. Личное вычищается при сборке.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Выбор файла для записи (заголовок, фильтр, предлагаемое имя) → путь или null. В тестах подменяется.</summary>
    public Func<string, string, string, string?> ChooseSaveFile { get; set; } = (title, filter, name) =>
    {
        UiGuard.Check(title);
        var d = new Microsoft.Win32.SaveFileDialog { Title = title, Filter = filter, FileName = name, DefaultExt = ".txt", AddExtension = true, OverwritePrompt = true };
        return d.ShowDialog() == true ? d.FileName : null;
    };

    /// <summary>Папка журнала окна (в тестах — временная).</summary>
    public Func<string> WindowLogDir { get; set; } = () => Path.Combine(WindowSettings.DefaultDir, "logs");

    public ICommand SaveReportCmd => _saveReportCmd ??= RelayCommand.Async(SaveReportAsync, () => !IsLoading);
    ICommand? _saveReportCmd;

    public async Task SaveReportAsync()
    {
        var path = ChooseSaveFile(L.T("diag.report.pick"), L.T("diag.report.filter"), $"dpishell-report-{DateTime.Now:yyyy-MM-dd}.txt");
        if (path is null) return;
        Message = L.T("diag.report.busy");
        try
        {
            var text = await BuildReportAsync();
            await File.WriteAllTextAsync(path, text, new UTF8Encoding(true));
            Message = L.T("diag.report.saved", new { path });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Fail(L.T("diag.report.failed", new { error = Texts.Friendly(e) })); }
    }

    /// <summary>Текст отчёта (для записи в файл и для теста).</summary>
    public async Task<string> BuildReportAsync()
    {
        string T(string key, string v) => L.T(key, new { v });
        string Unknown() => L.T("diag.report.unknown");
        string Or(string? s) => string.IsNullOrWhiteSpace(s) ? Unknown() : s.Trim();
        string One(string? s) => string.Join(" ", (s ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
        var hide = new List<string>();
        var sections = new List<ReportSection>();
        var st = Live;

        // Версии: «0.10.0» — не адрес, поэтому раздел не чистится.
        sections.Add(new(L.T("diag.report.sec.versions"),
        [
            T("diag.report.v.app", AppVersionText),
            T("diag.report.v.helper", Or(Helper.Reader?.HelperVersion ?? (Helper.State == HelperState.NotInstalled ? "" : null))),
            T("diag.report.v.core", Or(string.IsNullOrEmpty(_coreVersion) ? _shell.Installer.Manifest.Version : _coreVersion)),
            T("diag.report.v.data", Or(st?.DataVersion ?? _shell.DataVersion)),
            T("diag.report.v.windows", RuntimeInformation.OSDescription),
        ], Raw: true));

        // Служба, оптимизация, стратегия.
        var svc = new List<string>();
        if (st is not null) svc.Add(T("diag.report.svc.mode", L.T(st.RunMode == "process" ? "diag.report.svc.mode_process" : "diag.report.svc.mode_service")));
        if (ServiceText.Length > 0) svc.Add(T("diag.report.svc.service", One(ServiceText)));
        svc.Add(T("diag.report.svc.opt", L.T(st?.Active == true || _active ? "diag.report.svc.on" : "diag.report.svc.off")));
        var strategyId = st?.StrategyId ?? _localStrategy;
        if (strategyId is not null) svc.Add(T("diag.report.svc.strategy", StrategyTitle(strategyId)));
        if (DriverText.Length > 0) svc.Add(T("diag.report.svc.driver", One(DriverText)));
        if (CoreText.Length > 0) svc.Add(T("diag.report.svc.core", One(CoreText)));
        sections.Add(new(L.T("diag.report.sec.service"), svc));

        // Итоги последних проверки и подбора.
        var last = new List<string>();
        if (CheckSummary.Length > 0) last.Add(T("diag.report.last.check", One(CheckSummary)));
        if (CheckedAt.Length > 0) last.Add(T("diag.report.last.check_at", One(CheckedAt)));
        if (DnsSummary.Length > 0) last.Add(T("diag.report.last.dns", One(DnsSummary)));
        if (ResultText.Length > 0) last.Add(T("diag.report.last.select", One(ResultText)));
        if (ResultSummary.Length > 0) last.Add(T("diag.report.last.select", One(ResultSummary)));
        if (ResultHint.Length > 0) last.Add(T("diag.report.last.select_hint", One(ResultHint)));
        sections.Add(new(L.T("diag.report.sec.last"), last));

        // Помехи: VPN, похожие программы, находки окружения, подмена DNS.
        var inter = new List<string>
        {
            T("diag.report.if.vpn", VpnList.Count == 0 ? L.T("diag.report.if.none") : string.Join("; ", VpnList.Select(One))),
            T("diag.report.if.similar", ConflictList.Count == 0 ? L.T("diag.report.if.none") : string.Join("; ", ConflictList.Select(One))),
        };
        if (DnsSpoofed) inter.Add(L.T("diag.report.if.dns_spoofed"));
        inter.AddRange(Findings.Select(f => $"{f.Mark} {One(f.Title)}: {One(f.Text)}"));
        sections.Add(new(L.T("diag.report.sec.interference"), inter));

        // DNS и прокси: только включён/выключен и тип; адресов нет и в скрытом виде.
        var net = new List<string>();
        if (_pcDnsStatus is { } d)
            net.Add(T("diag.report.net.dns", d.Current == DnsPresets.Auto ? L.T("diag.report.net.dns_auto")
                : (DnsPresets.Find(d.Current)?.Title ?? L.T("diag.report.net.dns_plain")) + ", " + L.T(d.Encrypted ? "diag.report.net.dns_secure" : "diag.report.net.dns_plain")));
        if (Proxy.Current is { } p)
        {
            string Kind(ProxySettings s, bool autoDetect) =>
                s.Enabled && s.Server is not null ? L.T("diag.report.net.proxy_server")
                : s.Pac is not null ? L.T("diag.report.net.proxy_pac")
                : autoDetect ? L.T("diag.report.net.proxy_autodetect") : L.T("diag.report.net.proxy_off");
            net.Add(T("diag.report.net.proxy_user", Kind(p.User, p.UserAutoDetect)));
            net.Add(T("diag.report.net.proxy_machine", Kind(p.Machine, p.MachineAutoDetect)));
            net.Add(T("diag.report.net.proxy_winhttp", L.T(p.WinHttp.IsDirect ? "diag.report.net.winhttp_direct" : "diag.report.net.winhttp_proxy")));
        }
        else net.Add(T("diag.report.net.proxy_user", L.T("diag.report.net.proxy_unknown")));
        sections.Add(new(L.T("diag.report.sec.net"), net));

        // Роутер: найден ли, версия, состояние. Адрес роутера — только в список скрытого.
        var rt = Router;
        var router = new List<string> { T("diag.report.router.found", L.T(rt.Found ? "diag.report.router.yes" : "diag.report.router.no")) };
        if (rt.Found)
        {
            router.Add(T("diag.report.router.version", One(rt.PackageVersionText)));
            router.Add(T("diag.report.router.state", L.T(!rt.Connected ? "diag.report.router.state_nologin" : rt.RouterEnabled ? "diag.report.router.state_on" : "diag.report.router.state_off")));
        }
        sections.Add(new(L.T("diag.report.sec.router"), router));

        // Свои сайты и исключения — только число; сами адреса идут в список скрытого (они попадают в чужие тексты: подбор, журнал).
        var own = Lines(TargetsText);
        var excluded = Lines(ExcludeText);
        hide.AddRange(PrivateNames());
        sections.Add(new(L.T("diag.report.sec.sites"),
        [
            L.T("diag.report.sites.own", new { n = own.Count }),
            L.T("diag.report.sites.exclude", new { n = excluded.Count }),
        ]));

        // Журналы: помощника (его папка закрыта — отдаёт он сам) и окна.
        var helperLog = new List<string>();
        if (Helper.Reader is { } c && c.Compat.Supports("log_tail"))
        {
            try { helperLog.AddRange(await c.LogTailAsync()); if (helperLog.Count == 0) helperLog.Add(L.T("diag.report.log.empty")); }
            catch (HelperUnavailableException e) { Helper.MarkLost(e); helperLog.Add(L.T("diag.report.log.unavailable")); }
            catch (HelperException) { helperLog.Add(L.T("diag.report.log.unavailable")); }
        }
        else helperLog.Add(L.T("diag.report.log.unavailable"));
        sections.Add(new(L.T("diag.report.sec.helper_log"), helperLog));
        var windowLog = await Task.Run(() => WindowLogTail(WindowLogDir()));
        sections.Add(new(L.T("diag.report.sec.window_log"), windowLog.Count == 0 ? [L.T("diag.report.log.empty")] : windowLog));

        return SupportReport.Build(DateTime.Now, sections, hide);
    }

    /// <summary>
    /// Что окно знает как личное и чего не должно быть в отчёте и в описании ошибки: адреса прокси (текущие и из списка), адрес роутера,
    /// свои сайты и исключения (они попадают в чужие тексты: подбор, журнал), свои строки hosts и свои DNS. Одно место для «Сохранить отчёт» и «Скопировать» у ошибки.
    /// </summary>
    List<string> PrivateNames()
    {
        var hide = new List<string>();
        if (Proxy.Current is { } p)
        {
            foreach (var s in new[] { p.User, p.Machine })
                foreach (var v in new[] { s.Server, s.Pac, s.Bypass }) hide.AddRange(SupportReport.Variants(v));
            hide.AddRange(SupportReport.Variants(p.WinHttp.Proxy));
        }
        foreach (var row in Proxy.Items) { hide.AddRange(SupportReport.Variants(row.Entry.Server)); hide.AddRange(SupportReport.Variants(row.Entry.Pac)); }
        if (Router.Found) hide.AddRange(SupportReport.Variants(Router.Detection?.Address));
        hide.AddRange(Lines(TargetsText)); hide.AddRange(Lines(ExcludeText));
        if (Live is { } st) { hide.AddRange(st.UserTargets); hide.AddRange(st.UserExclude ?? []); }
        // Свои строки hosts (имена и адреса) и свои DNS (адреса, DoH): попадают в тексты ошибок помощника («строка … отвергнута»).
        foreach (var row in Hosts.UserRows) { hide.Add(row.Host); hide.Add(row.Ip); }
        foreach (var d in Settings.DnsCustom)
        {
            hide.AddRange(d.Ips);
            hide.AddRange(SupportReport.Variants(d.Doh));
        }
        return hide;
    }

    static List<string> Lines(string text) =>
        [.. (text ?? "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0 && !x.StartsWith('#'))];

    /// <summary>Последние строки журналов окна (*.log): сначала самый свежий файл, пока не наберётся нужное число строк.</summary>
    static List<string> WindowLogTail(string dir)
    {
        var result = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return result;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.log").OrderByDescending(x => x.LastWriteTimeUtc))
            {
                var take = SupportReport.LogLines - result.Count;
                if (take <= 0) break;
                using var fs = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                var q = new Queue<string>();
                while (sr.ReadLine() is { } line) { q.Enqueue(line); if (q.Count > take) q.Dequeue(); }
                result.InsertRange(0, q);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return result;
    }
}
