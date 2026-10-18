using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Готовый вариант DNS этого компьютера (architecture.md «DNS этого компьютера»).</summary>
/// <param name="Ips">Адреса серверов: IPv4, затем IPv6 (до 4, уже в нормальной записи).</param>
/// <param name="Doh">Шаблон защищённого DNS (DoH); null — только обычный DNS.</param>
/// <param name="Note">Чем отличается (фильтрация, реестр блокировок) — показывается у варианта.</param>
/// <param name="Owner">Кто держит сервер — для описания варианта.</param>
public sealed record DnsPreset(string Id, string Title, IReadOnlyList<string> Ips, string? Doh, string? Note = null, string? Owner = null);

/// <summary>Готовые варианты DNS. Адреса и шаблоны проверены запросом RFC 8484 (2026-10-17).</summary>
public static class DnsPresets
{
    public const string Auto = "auto";
    public const string Custom = "custom";

    public static IReadOnlyList<DnsPreset> All { get; } =
    [
        new(Auto, "Как у провайдера (автоматически)", [], null),
        new("google", "Google", ["8.8.8.8", "8.8.4.4", "2001:4860:4860::8888", "2001:4860:4860::8844"], "https://dns.google/dns-query",
            Owner: "Google (США)"),
        new("cloudflare", "Cloudflare", ["1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001"], "https://cloudflare-dns.com/dns-query",
            Owner: "Cloudflare (США)"),
        // 9.9.9.9 — основной режим Quad9: отвечает ошибкой на известные вредоносные домены (quad9.net/service/service-addresses-and-features).
        new("quad9", "Quad9", ["9.9.9.9", "149.112.112.112", "2620:fe::fe", "2620:fe::9"], "https://dns.quad9.net/dns-query",
            "отсеивает известные вредоносные сайты", "фонд Quad9 (Швейцария)"),
        // DoH Яндекса — по 77.88.8.8 и 77.88.8.1, только HTTP/2 (проверено); адреса — yandex.ru/support/dns, базовый режим.
        new("yandex", "Яндекс", ["77.88.8.8", "77.88.8.1", "2a02:6b8::feed:ff", "2a02:6b8:0:1::feed:ff"],
            "https://common.dot.dns.yandex.net/dns-query", "российский: применяет реестр блокировок", "Яндекс (Россия)"),
    ];

    /// <summary>Что принимает команда dns_set: готовые и «свой».</summary>
    public static string[] Choices { get; } = [.. All.Select(p => p.Id), Custom];

    public static DnsPreset? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>Строгая проверка адресов и шаблонов DNS — одна для окна и помощника.</summary>
public static partial class DnsValues
{
    public const int MaxServers = 4;
    public const int MaxTemplate = 200;
    public const int MaxTitle = 40;

    /// <summary>Настоящий IPv4 (четыре числа) или IPv6 (без зоны) → нормальная запись; иначе null. Пустой адрес и широковещательный — нет.</summary>
    public static string? NormalizeIp(string? s)
    {
        if (s is null) return null;
        s = s.Trim();
        if (s.Length is 0 or > 45) return null;
        if (Ipv4Rx().IsMatch(s))
        {
            if (!IPAddress.TryParse(s, out var a) || a.AddressFamily != AddressFamily.InterNetwork) return null;
            var n = a.ToString();
            if (n is "0.0.0.0" or "255.255.255.255" || n != s) return null;   // 010.1.1.1 и т. п. — не наш случай
            return n;
        }
        if (Ipv6Rx().IsMatch(s) && IPAddress.TryParse(s, out var b) && b.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (b.Equals(IPAddress.IPv6None) || b.IsIPv6Multicast) return null;
            return b.ToString();
        }
        return null;
    }

    public static bool IsIpv6(string ip) => ip.Contains(':');

    /// <summary>Шаблон DoH: только https://сервер[:порт]/путь, без пробелов, кавычек, обратных слешей и параметров.</summary>
    public static bool IsDohTemplate(string? s) =>
        s is { Length: > 8 and <= MaxTemplate } && DohRx().IsMatch(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && u.UserInfo.Length == 0 && u.Host.Length > 0;

    /// <summary>Имя своего варианта: до 40 символов, без кавычек и управляющих.</summary>
    public static bool IsTitle(string? s) => s is { Length: > 0 and <= MaxTitle } && !TitleBadRx().IsMatch(s) && s.Trim().Length > 0;

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$")]
    private static partial Regex Ipv4Rx();

    [GeneratedRegex(@"^[0-9A-Fa-f:]{2,39}$")]
    private static partial Regex Ipv6Rx();

    [GeneratedRegex(@"^https://[A-Za-z0-9.\-]+(:\d{1,5})?(/[A-Za-z0-9\-._~/]*)?$")]
    private static partial Regex DohRx();

    [GeneratedRegex(@"[""'\\<>\x00-\x1F\x7F]")]
    private static partial Regex TitleBadRx();
}

public enum NetAdapterKind { Ethernet, Wifi, Other }

/// <summary>Сетевой адаптер глазами DNS. Id — GUID адаптера ({…}).</summary>
/// <param name="Ipv6Index">Номер интерфейса IPv6 (для netsh); 0 — IPv6 на адаптере нет.</param>
public sealed record NetAdapter(string Id, string Name, string Description, NetAdapterKind Kind, bool Up, bool HasGateway, int Ipv6Index = 0);

/// <summary>Защищённый DNS для одного адреса на адаптере (ключ DohInterfaceSettings\Doh|Doh6\&lt;адрес&gt;).</summary>
public sealed record DohSetting(ulong Flags, string? Template);

/// <summary>DNS одного адаптера: адреса IPv4/IPv6 (пусто и Auto — от DHCP/провайдера) и защищённый DNS по адресам.</summary>
public sealed record AdapterDns(bool Ipv4Auto, IReadOnlyList<string> Ipv4, bool Ipv6Auto, IReadOnlyList<string> Ipv6, IReadOnlyDictionary<string, DohSetting> Doh)
{
    public static AdapterDns Automatic { get; } = new(true, [], true, [], new Dictionary<string, DohSetting>());
}

/// <summary>
/// Что делает с системой смена DNS. Настоящая — <see cref="WindowsDnsSystem"/> (WMI, реестр, netsh с проверенными аргументами);
/// в тестах — подмена в памяти: настоящий DNS, реестр и сеть тесты не трогают.
/// </summary>
public interface IDnsSystem
{
    /// <summary>Есть ли в Windows встроенный защищённый DNS (Windows 11).</summary>
    bool DohSupported { get; }
    IReadOnlyList<NetAdapter> Adapters();
    AdapterDns Read(NetAdapter adapter);
    /// <summary>Записать DNS адаптера целиком: адреса и защищённый DNS по адресам (лишние записи DoH удаляются).</summary>
    void Write(NetAdapter adapter, AdapterDns dns);
    /// <summary>Известные Windows шаблоны DoH (адрес → шаблон): встроенные Google, Cloudflare, Quad9.</summary>
    IReadOnlyDictionary<string, string> KnownDoh();
    /// <summary>Зарегистрировать шаблон для адреса. true — запись новая (её уберёт «Вернуть как было»), false — была и обновлена.</summary>
    bool RegisterDoh(string ip, string template, bool fallback);
    void UnregisterDoh(string ip);
    void FlushCache();
    /// <summary>Отвечает ли DNS системы сейчас (одна попытка).</summary>
    Task<bool> ResolvesAsync(CancellationToken ct);
}

public sealed record DnsAdapterInfo(string Name, string Kind, bool Auto, IReadOnlyList<string> Servers, bool Encrypted);

/// <param name="Current">Вариант на адаптерах: id готового, custom, auto или other (выставлено не нами / по-разному).</param>
/// <param name="Encrypted">Защищённый DNS включён на всех адаптерах для всех адресов.</param>
/// <param name="CanRestore">Есть сохранённые прежние настройки — «Вернуть как было».</param>
/// <param name="Skipped">VPN и виртуальные адаптеры, которые не трогаем.</param>
public sealed record DnsStatusInfo(string Current, bool Encrypted, bool Fallback, bool DohSupported, bool CanRestore,
    IReadOnlyList<DnsAdapterInfo> Adapters, IReadOnlyList<string> Skipped);

public sealed record DnsSetResult(bool Ok, string Message, DnsStatusInfo Status);

/// <summary>
/// DNS этого компьютера в один клик (architecture.md «DNS этого компьютера»). Меняет только активные физические адаптеры с основным шлюзом
/// (Ethernet, Wi-Fi); VPN и виртуальные не трогает. Перед первым изменением сохраняет прежние настройки каждого адаптера
/// в закрытой папке помощника — «Вернуть как было» возвращает их дословно. После переключения проверяет, что DNS отвечает;
/// нет — возвращает то, что было до переключения.
/// </summary>
public sealed class DnsManager(IDnsSystem sys, string backupFile)
{
    /// <summary>
    /// DohFlags адаптера так, как их пишет «Параметры» Windows 11 для «Вкл. (автоматический шаблон)», без возврата к обычному
    /// тексту (прочитано из реестра после настройки руками: 0x11 + DohTemplate). Документации у значения нет.
    /// </summary>
    public const ulong DohOn = 0x11;
    /// <summary>«Разрешить обычный DNS, если защищённый не отвечает» — бит 0x4 (не задокументирован, см. architecture.md «DNS этого компьютера»).</summary>
    public const ulong DohFallbackBit = 0x4;

    /// <summary>Сколько раз и с какой паузой проверять, что DNS отвечает после переключения (DoH поднимается не сразу).</summary>
    public int VerifyAttempts { get; init; } = 4;
    public TimeSpan VerifyDelay { get; init; } = TimeSpan.FromSeconds(1.5);

    static readonly Regex VirtualRx = new(
        @"hyper-v|vethernet|virtual|vmware|virtualbox|radmin|wireguard|wintun|tap-windows|openvpn|zerotier|tailscale|amnezia|nordlynx|proton|outline|vpn|loopback|npcap|wan miniport|docker|wsl|hamachi",
        RegexOptions.IgnoreCase);

    /// <summary>Какие адаптеры меняем: включён, Ethernet или Wi-Fi, есть основной шлюз, не VPN и не виртуальный.</summary>
    public static (List<NetAdapter> Picked, List<NetAdapter> Skipped) Pick(IEnumerable<NetAdapter> all)
    {
        var picked = new List<NetAdapter>();
        var skipped = new List<NetAdapter>();
        foreach (var a in all)
        {
            if (!a.Up || !a.HasGateway) continue;   // выключенные и без выхода в сеть — не упоминаем
            if (a.Kind == NetAdapterKind.Other || VirtualRx.IsMatch(a.Description) || VirtualRx.IsMatch(a.Name)) skipped.Add(a);
            else picked.Add(a);
        }
        return (picked, skipped);
    }

    sealed class Backup
    {
        public Dictionary<string, SavedAdapter> Adapters { get; set; } = [];
        /// <summary>Шаблоны DoH, которые зарегистрировали мы (их не было) — «Вернуть как было» удаляет.</summary>
        public List<string> AddedDoh { get; set; } = [];
        /// <summary>Последний включённый вариант и свой — чтобы узнавать его в состоянии.</summary>
        public string? Choice { get; set; }
        public List<string>? CustomIps { get; set; }
    }

    sealed record SavedAdapter(string Name, AdapterDns Dns);

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    Backup? LoadBackup()
    {
        try { return File.Exists(backupFile) ? JsonSerializer.Deserialize<Backup>(File.ReadAllText(backupFile), Json) : null; }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    void SaveBackup(Backup b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(backupFile)!);
        var tmp = backupFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(b, Json));
        File.Move(tmp, backupFile, overwrite: true);
    }

    public DnsStatusInfo Status() => Status(LoadBackup());

    DnsStatusInfo Status(Backup? backup)
    {
        var (picked, skipped) = Pick(sys.Adapters());
        var states = picked.Select(a => (a, d: sys.Read(a))).ToList();
        var infos = states.Select(x => new DnsAdapterInfo(x.a.Name, x.a.Kind == NetAdapterKind.Wifi ? "wifi" : "ethernet",
            x.d.Ipv4Auto && x.d.Ipv4.Count == 0, [.. x.d.Ipv4, .. x.d.Ipv6], Encrypted(x.d))).ToList();
        bool encrypted = sys.DohSupported && states.Count > 0 && states.All(x => Encrypted(x.d));
        bool fallback = encrypted && states.Any(x => x.d.Doh.Values.Any(v => (v.Flags & DohFallbackBit) != 0));
        return new(Recognize(states.Select(x => x.d).ToList(), backup), encrypted, fallback, sys.DohSupported, backup is not null,
            infos, skipped.Select(a => a.Name).ToList());
    }

    static bool Encrypted(AdapterDns d) =>
        d.Ipv4.Count + d.Ipv6.Count > 0 && d.Ipv4.Concat(d.Ipv6).All(ip => d.Doh.TryGetValue(ip, out var s) && (s.Flags & 0x1) != 0);

    static string Recognize(IReadOnlyList<AdapterDns> all, Backup? backup)
    {
        if (all.Count == 0) return "other";
        string Of(AdapterDns d)
        {
            if (d.Ipv4.Count == 0 && d.Ipv4Auto) return DnsPresets.Auto;
            var v4 = d.Ipv4.ToHashSet();
            if (backup?.Choice == DnsPresets.Custom && backup.CustomIps is { } c && c.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet().SetEquals(v4))
                return DnsPresets.Custom;
            return DnsPresets.All.FirstOrDefault(p => p.Ips.Count > 0 && p.Ips.Where(ip => !DnsValues.IsIpv6(ip)).ToHashSet().SetEquals(v4))?.Id ?? "other";
        }
        var ids = all.Select(Of).Distinct().ToList();
        return ids.Count == 1 ? ids[0] : "other";
    }

    /// <summary>
    /// Включить вариант. <paramref name="ips"/> и <paramref name="doh"/> — только для «своего» (уже проверены <see cref="DnsValues"/>).
    /// <paramref name="fallback"/> — разрешить обычный DNS, если защищённый не отвечает.
    /// </summary>
    public async Task<DnsSetResult> SetAsync(string choice, IReadOnlyList<string>? ips, string? doh, bool fallback, CancellationToken ct)
    {
        IReadOnlyList<string> servers;
        string? template;
        string title;
        if (choice == DnsPresets.Custom)
        {
            servers = ips is { Count: > 0 } ? ips : throw new ArgumentException("Свой вариант без адресов.");
            template = doh;
            title = "свой DNS";
        }
        else
        {
            var p = DnsPresets.Find(choice) ?? throw new ArgumentException("Нет такого варианта DNS.");
            servers = p.Ips;
            template = p.Doh;
            title = p.Title;
        }

        var (picked, _) = Pick(sys.Adapters());
        if (picked.Count == 0) throw new InvalidOperationException("Не нашёл подключение Ethernet или Wi-Fi с выходом в интернет — менять нечего.");

        var backup = LoadBackup() ?? new Backup();
        var before = picked.ToDictionary(a => a.Id, sys.Read);
        foreach (var a in picked) backup.Adapters.TryAdd(a.Id, new SavedAdapter(a.Name, before[a.Id]));

        // Шаблоны: встроенные (Google, Cloudflare, Quad9) уже известны Windows; остальные регистрируем.
        var addedNow = new List<string>();
        bool encrypt = template is not null && sys.DohSupported;
        if (encrypt)
        {
            var known = sys.KnownDoh();
            foreach (var ip in servers)
            {
                if (known.TryGetValue(ip, out var t) && string.Equals(t, template, StringComparison.OrdinalIgnoreCase)) continue;
                if (sys.RegisterDoh(ip, template!, fallback) && !backup.AddedDoh.Contains(ip)) { backup.AddedDoh.Add(ip); addedNow.Add(ip); }
            }
        }
        // Прежние настройки — на диск до первого изменения: оборвётся посередине — «Вернуть как было» всё равно сработает.
        SaveBackup(backup);

        var flags = DohOn | (fallback ? DohFallbackBit : 0);
        var target = choice == DnsPresets.Auto ? AdapterDns.Automatic : new AdapterDns(false,
            servers.Where(ip => !DnsValues.IsIpv6(ip)).ToList(), false, servers.Where(DnsValues.IsIpv6).ToList(),
            encrypt ? servers.ToDictionary(ip => ip, _ => new DohSetting(flags, template)) : new Dictionary<string, DohSetting>());
        try
        {
            foreach (var a in picked) sys.Write(a, Fit(a, target));
            sys.FlushCache();
        }
        catch
        {
            Rollback(picked, before, addedNow, backup);
            throw;
        }

        if (choice != DnsPresets.Auto && !await VerifyAsync(ct))
        {
            Rollback(picked, before, addedNow, backup);
            var why = encrypt ? $"Защищённый DNS {title} не ответил" : $"DNS {title} не ответил";
            return new(false, why + " — вернул прежние настройки. Попробуйте другой вариант" + (encrypt && !fallback ? " или разрешите обычный DNS." : "."), Status(backup));
        }

        backup.Choice = choice;
        backup.CustomIps = choice == DnsPresets.Custom ? [.. servers] : null;
        SaveBackup(backup);
        var names = string.Join(", ", picked.Select(a => a.Name));
        var how = choice == DnsPresets.Auto ? "DNS как у провайдера" : encrypt ? $"{title}, защищённый" : $"{title}, без шифрования";
        return new(true, $"{how} — {names}.", Status(backup));
    }

    /// <summary>Нет IPv6 на адаптере — адреса IPv6 не пишем.</summary>
    static AdapterDns Fit(NetAdapter a, AdapterDns d) =>
        a.Ipv6Index > 0 ? d : d with
        {
            Ipv6 = [],
            Doh = d.Doh.Where(kv => !DnsValues.IsIpv6(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
        };

    void Rollback(List<NetAdapter> picked, Dictionary<string, AdapterDns> before, List<string> addedNow, Backup backup)
    {
        foreach (var a in picked)
        {
            try { sys.Write(a, before[a.Id]); } catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var ip in addedNow)
        {
            try { sys.UnregisterDoh(ip); backup.AddedDoh.Remove(ip); } catch (Exception e) when (e is InvalidOperationException or IOException) { }
        }
        try { sys.FlushCache(); } catch (Exception e) when (e is InvalidOperationException) { }
        try { SaveBackup(backup); } catch (IOException) { }
    }

    async Task<bool> VerifyAsync(CancellationToken ct)
    {
        for (int i = 0; i < VerifyAttempts; i++)
        {
            if (i > 0) await Task.Delay(VerifyDelay, ct);
            if (await sys.ResolvesAsync(ct)) return true;
        }
        return false;
    }

    /// <summary>«Вернуть как было»: прежние настройки каждого адаптера, свои регистрации DoH — убрать.</summary>
    public Task<DnsSetResult> RestoreAsync(CancellationToken ct)
    {
        var backup = LoadBackup();
        if (backup is null) return Task.FromResult(new DnsSetResult(true, "DNS не меняли — возвращать нечего.", Status(null)));
        var now = sys.Adapters().ToDictionary(a => a.Id);
        var restored = new List<string>();
        foreach (var (id, saved) in backup.Adapters)
        {
            ct.ThrowIfCancellationRequested();
            if (!now.TryGetValue(id, out var a)) continue;   // адаптера больше нет — нечего возвращать
            sys.Write(a, saved.Dns);
            restored.Add(a.Name);
        }
        foreach (var ip in backup.AddedDoh) sys.UnregisterDoh(ip);
        sys.FlushCache();
        File.Delete(backupFile);
        var msg = restored.Count > 0 ? $"DNS — как было: {string.Join(", ", restored)}." : "DNS — как было.";
        return Task.FromResult(new DnsSetResult(true, msg, Status(null)));
    }
}
