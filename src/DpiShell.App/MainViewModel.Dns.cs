using System.IO;
using System.Windows.Input;
using DpiShell.Core;
using DpiShell.Core.Helper;

namespace DpiShell.App;

/// <summary>
/// DNS этого компьютера в один клик (architecture.md «DNS этого компьютера»): выбор варианта на «Диагностике» и кнопка на плашке
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

    static bool Windows11 => Environment.OSVersion.Version.Build >= 22000;

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
        PcDns.SetPresets(DnsPresets.All.Select(p => new DnsOption(p.Id, p.Title, PresetDetail(p), false, p.Ips, p.Doh)).ToList());
        FixDnsCmd = RelayCommand.Async(() => ApplyPcDnsAsync(PcDns.Options.First(o => o.Id == RecommendedDns)), () => !Busy && !IsLoading);
        ChooseDnsCmd = new RelayCommand(() => Page = Pages.Diag);
        // Свои варианты одни на ПК и роутер.
        PcDns.CustomChanged += Router.DnsPick.Reload;
        Router.DnsPick.CustomChanged += PcDns.Reload;
    }

    static string PresetDetail(DnsPreset p)
    {
        if (p.Id == DnsPresets.Auto) return "адреса DNS — от роутера или провайдера";
        var v4 = string.Join(", ", p.Ips.Where(ip => !DnsValues.IsIpv6(ip)));
        return v4 + (p.Doh is null ? "" : Windows11 ? " · защищённый" : " · без шифрования") + (p.Note is { } n ? " · " + n : "");
    }

    public string RecommendedDnsText => "Включить защищённый DNS";
    public string RecommendedDnsHint => DnsPresets.Find(RecommendedDns)!.Title + ", для Ethernet и Wi-Fi. Вернуть — «Диагностика» → «DNS этого компьютера».";

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
            PcDns.CurrentText = "Не удалось прочитать DNS: " + Texts.Friendly(e);
        }
    }

    public void ShowPcDns(DnsStatusInfo s)
    {
        var first = s.Adapters.FirstOrDefault();
        var v4 = first?.Servers.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet() ?? [];
        var own = s.Current is DnsPresets.Custom or "other" ? PcDns.FindCustom(o => o.Ips.ToHashSet().SetEquals(v4)) : null;
        var title = own?.Title ?? s.Current switch
        {
            DnsPresets.Auto => "как у провайдера",
            "other" or DnsPresets.Custom => "свои настройки" + (v4.Count > 0 ? $" ({string.Join(", ", v4)})" : ""),
            var id => DnsPresets.Find(id)?.Title ?? id,
        };
        var how = s.Current == DnsPresets.Auto ? "" : s.Encrypted ? (s.Fallback ? ", защищённый (можно обычный)" : ", защищённый") : ", без шифрования";
        PcDns.CurrentText = s.Adapters.Count == 0 ? "Нет подключения Ethernet или Wi-Fi с выходом в интернет."
            : $"Сейчас: {title}{how} · {string.Join(", ", s.Adapters.Select(a => a.Name))}";
        var notes = new List<string>();
        if (!s.DohSupported) notes.Add("В Windows 10 защищённого DNS нет — только обычный.");
        if (s.Skipped.Count > 0) notes.Add("VPN и виртуальные адаптеры не трогаю: " + string.Join(", ", s.Skipped) + ".");
        PcDns.Note = string.Join(" ", notes);
        PcDns.ShowFallback = s.DohSupported;
        PcDns.CanRestore = s.CanRestore;
        PcDns.CurrentId = own?.Id ?? (s.Current is "other" or DnsPresets.Custom ? null : s.Current);
    }

    /// <summary>«Применить»: помощник меняет DNS адаптеров, сбрасывает кэш, проверяет; затем «Проверить сейчас» и проверка подмены.</summary>
    public async Task ApplyPcDnsAsync(DnsOption o)
    {
        DnsSetResult? r = null;
        await WithHelper("Меняю DNS…", async c =>
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
        await AfterDnsChangeAsync(r);
    }

    public async Task RestorePcDnsAsync()
    {
        DnsSetResult? r = null;
        await WithHelper("Возвращаю DNS…", async c =>
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
        Message = r.Message + " Проверка DNS: " + DnsSummary;
    }
}
