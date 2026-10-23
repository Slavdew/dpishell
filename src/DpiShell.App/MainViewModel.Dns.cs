using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// DNS этого компьютера в один клик (architecture.md «DNS этого компьютера»): выбор варианта на вкладке «DNS» и кнопка на плашке
/// «DNS провайдера подменяет…». Меняет помощник (нужны права); после смены — «Проверить сейчас» и проверка подмены,
/// чтобы было видно, стало ли лучше.
/// </summary>
public sealed partial class MainViewModel
{
    /// <summary>Выбор DNS этого компьютера (тот же компонент, что у роутера).</summary>
    public DnsPicker PcDns { get; private set; } = null!;

    /// <summary>Чтение DNS адаптеров без помощника (права не нужны). Null — не читать (тесты).</summary>
    public IDnsSystem? LocalDns { get; set; }

    /// <summary>Рекомендуемый вариант для кнопки на плашке «DNS провайдера подменяет…».</summary>
    public const string RecommendedDns = "google";

    /// <summary>Плашка на главной: включить рекомендуемый защищённый DNS одной кнопкой.</summary>
    public ICommand FixDnsCmd { get; private set; } = null!;
    public ICommand ChooseDnsCmd { get; private set; } = null!;

    static bool Windows11 => OsInfo.IsWindows11;

    void InitPcDns()
    {
        PcDns = new DnsPicker(Settings)
        {
            ShowShield = true,
            ShowFallback = Windows11,
            Apply = ApplyPcDnsAsync,
            Restore = RestorePcDnsAsync,
            Enabled = () => !Busy && !IsLoading,
        };
        PcDns.PresetSource = PcPresets;
        SetPcPresets();
        FixDnsCmd = RelayCommand.Async(() => ApplyPcDnsAsync(PcDns.Options.First(o => o.Id == RecommendedDns)), () => !Busy && !IsLoading);
        // «Другой DNS» — сразу к выбору, а не в начало вкладки «DNS» (UX-аудит 2, п. 4): окно прокручивает к карточке и раскрывает список.
        ChooseDnsCmd = new RelayCommand(() => { OpenNet(NetBlock.Dns); ShowRequested?.Invoke(Spot.PcDns); });
        // Свои варианты одни на ПК и роутер.
        PcDns.CustomChanged += Router.DnsPick.Reload;
        Router.DnsPick.CustomChanged += PcDns.Reload;
    }

    string? _presetsLang;

    /// <summary>Названия, заметки и описания вариантов зависят от языка: список строится заново, когда язык сменился.</summary>
    void SetPcPresets()
    {
        _presetsLang = L.Current;
        PcDns.SetPresets(PcPresets());
    }

    static IReadOnlyList<DnsOption> PcPresets() => DnsPresets.All.Select(p => new DnsOption(p.Id, p.Title, PresetDetail(p), false, p.Ips, p.Doh,
        Owner: p.Owner, Note: PresetNote(p.Id, p.Note))).ToList();

    static string PresetDetail(DnsPreset p)
    {
        if (p.Id == DnsPresets.Auto) return L.T("dns.preset.auto_detail");
        var v4 = string.Join(", ", p.Ips.Where(ip => !DnsValues.IsIpv6(ip)));
        var head = p.Doh is null ? v4 : L.T(Windows11 ? "dns.preset.detail_secure" : "dns.preset.detail_plain", new { ips = v4 });
        return head + (p.Note is { } n ? " · " + n : "");
    }

    /// <summary>Заметка варианта: «рекомендуем» у рекомендуемого (ПК и роутер), затем своя заметка пресета.</summary>
    public static string? PresetNote(string id, string? note) =>
        id == RecommendedDns ? L.T("dns.preset.note_recommended") + (note is { Length: > 0 } ? " · " + note : "") : note;

    public string RecommendedDnsText => L.T("dns.recommended.text");
    public string RecommendedDnsHint => L.T("dns.recommended.hint", new { name = DnsPresets.Find(RecommendedDns)!.Title });

    /// <summary>Что сейчас на адаптерах: у помощника (там же «есть что вернуть») или прочитать самим.</summary>
    public async Task RefreshPcDnsAsync()
    {
        try
        {
            DnsStatusInfo? s = null;
            if (Helper.Client is { } c) s = await c.DnsStatusAsync();
            else if (LocalDns is { } sys) s = await Task.Run(() => new DnsManager(sys, "").Status());
            if (s is not null) ShowPcDns(s);
        }
        catch (Exception e) when (e is HelperException or HelperUnavailableException or IOException or UnauthorizedAccessException
                                     or InvalidOperationException or System.Security.SecurityException)
        {
            PcDns.CurrentText = L.T("dns.status.read_failed", new { error = Texts.Friendly(e) });
        }
    }

    /// <summary>Название DNS на адаптерах: свой вариант, пресет, «как у провайдера» или адреса.</summary>
    string PcDnsTitle(DnsStatusInfo s, DnsOption? own)
    {
        var first = s.Adapters.FirstOrDefault();
        var v4 = first?.Servers.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet() ?? [];
        return own?.Title ?? s.Current switch
        {
            DnsPresets.Auto => L.T("dns.status.provider"),
            "other" or DnsPresets.Custom => v4.Count > 0 ? L.T("dns.status.custom_ips", new { ips = string.Join(", ", v4) }) : L.T("dns.status.custom"),
            var id => DnsPresets.Find(id)?.Title ?? id,
        };
    }

    /// <summary>Что сейчас на адаптерах одной строкой для заголовка блока «DNS» («Google, защищённый · Ethernet»); пусто — ещё не читали.</summary>
    string PcDnsNow()
    {
        if (_pcDnsStatus is not { } s) return "";
        if (s.Adapters.Count == 0) return L.T("dns.status.no_connection");
        var first = s.Adapters.First();
        var v4 = first.Servers.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet();
        var own = s.Current is DnsPresets.Custom or "other" ? PcDns.FindCustom(o => o.Ips.ToHashSet().SetEquals(v4)) : null;
        var title = PcDnsTitle(s, own);
        var head = s.Current == DnsPresets.Auto ? title : L.T(s.Encrypted ? (s.Fallback ? "net.dns.secure_fallback" : "net.dns.secure") : "net.dns.plain", new { title });
        return head + " · " + string.Join(", ", s.Adapters.Select(a => a.Name));
    }

    DnsStatusInfo? _pcDnsStatus;   // последнее, что прочитали у адаптеров (данные): подписи собираются заново при смене языка

    public void ShowPcDns(DnsStatusInfo s)
    {
        _pcDnsStatus = s;
        if (_presetsLang != L.Current) SetPcPresets();
        var first = s.Adapters.FirstOrDefault();
        var v4 = first?.Servers.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet() ?? [];
        var own = s.Current is DnsPresets.Custom or "other" ? PcDns.FindCustom(o => o.Ips.ToHashSet().SetEquals(v4)) : null;
        var title = PcDnsTitle(s, own);
        var currentKey = s.Current == DnsPresets.Auto ? "dns.current" : s.Encrypted ? (s.Fallback ? "dns.current.secure_fallback" : "dns.current.secure") : "dns.current.plain";
        PcDns.CurrentText = s.Adapters.Count == 0 ? L.T("dns.status.no_connection")
            : L.T(currentKey, new { title, adapters = string.Join(", ", s.Adapters.Select(a => a.Name)) });
        var notes = new List<string>();
        if (!s.DohSupported) notes.Add(L.T("dns.note.no_doh"));
        if (s.Skipped.Count > 0) notes.Add(L.T("dns.note.skipped", new { list = string.Join(", ", s.Skipped) }));
        PcDns.Note = string.Join(" ", notes);
        PcDns.ShowFallback = s.DohSupported;
        PcDns.CanRestore = s.CanRestore;
        var untouched = PcDns.SelectionUntouched;
        PcDns.CurrentId = own?.Id ?? (s.Current is "other" or DnsPresets.Custom ? null : s.Current);
        if (untouched && DnsSpoofed && s.Current == DnsPresets.Auto) PcDns.PreselectRecommended(RecommendedDns);
    }

    /// <summary>«Применить»: помощник меняет DNS адаптеров, сбрасывает кэш, проверяет; затем «Проверить сейчас» и проверка подмены.</summary>
    public async Task ApplyPcDnsAsync(DnsOption o)
    {
        DnsSetResult? r = null;
        await WithHelper(L.T("dns.busy.change"), async c =>
        {
            PcDns.Working = true;
            try
            {
                r = o.Custom
                    ? await c.DnsSetAsync(DnsPresets.Custom, o.Ips, o.Doh, PcDns.Fallback)
                    : await c.DnsSetAsync(o.Id, null, null, PcDns.Fallback);
                ShowPcDns(r.Status);
            }
            finally { PcDns.Working = false; }
        });
        if (r is null) return;
        // Без подтверждения, но с быстрым путём назад (UX-аудит, «Опасные действия»). Тост — сразу (UX-аудит 2, п. 14):
        // «Вернуть» доступен, пока идёт проверка; её итог дописывается в тот же тост.
        int? toast = r.Ok && PcDns.CanRestore ? ShowToast(L.T("dns.toast.changed_checking", new { title = o.Title }), L.T("dns.toast.undo"), RestorePcDnsAsync) : null;
        await AfterDnsChangeAsync(r);
        if (toast is { } id) UpdateToast(id, L.T("dns.toast.changed_checked", new { title = o.Title, summary = DnsSummary }));
    }

    public async Task RestorePcDnsAsync()
    {
        DnsSetResult? r = null;
        await WithHelper(L.T("dns.busy.restore"), async c =>
        {
            PcDns.Working = true;
            try { r = await c.DnsRestoreAsync(); ShowPcDns(r.Status); }
            finally { PcDns.Working = false; }
        });
        if (r is null) return;
        await AfterDnsChangeAsync(r);
    }

    /// <summary>Повторная проверка после смены DNS (в тестах — подмена без сети).</summary>
    public Func<Task>? RecheckAfterDns { get; set; }

    /// <summary>Сразу видно, стало ли лучше: проверка сайтов и сравнение системного DNS с защищённым.</summary>
    async Task AfterDnsChangeAsync(DnsSetResult r)
    {
        Message = r.Message;
        if (!r.Ok) return;
        if (RecheckAfterDns is { } recheck) await recheck();
        else if (IsLoading) return;
        else if (CheckedGroups().Count > 0) await CheckNowAsync();
        else await CheckDnsAsync();
        Message = L.T("dns.after_change", new { message = r.Message, summary = DnsSummary });
    }
}
