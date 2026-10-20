using System.Net;
using DpiShell.Core.Localization;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>
/// Профиль «под программу» (ТЗ 13): порты и адреса, записанные у игры, и параметры оптимизации для них. В командной строке winws это
/// отдельный профиль (<c>--new</c>) перед общими ролями: он срабатывает только на эти порты и адреса, общая оптимизация не меняется.
/// Живёт в state.json помощника; файл адресов lists\app-&lt;id&gt;.txt пишется из него при каждом включении.
/// </summary>
public sealed class AppProfile
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>Порты TCP «443,27015-27030»; пусто — TCP-профиля нет.</summary>
    public string TcpPorts { get; set; } = "";
    public string UdpPorts { get; set; } = "";
    /// <summary>Подсети и адреса назначения (канонический вид, без служебных сетей).</summary>
    public List<string> Addresses { get; set; } = [];
    /// <summary>Параметры игровой роли game_tcp / game_udp (проходят тот же белый список, что и каталог).</summary>
    public string TcpDesync { get; set; } = "";
    public string UdpDesync { get; set; } = "";
    /// <summary>Номер варианта параметров (для «Вариант N»).</summary>
    public int Variant { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>Вариант параметров для профиля: пара game_tcp/game_udp из готовых стратегий.</summary>
public sealed record ProfileVariant(int Index, string Title, string Tcp, string Udp);

/// <summary>Правила профилей: что допустимо в state.json и в командной строке. Всё, что попадёт в winws, проходит здесь.</summary>
public static partial class AppProfiles
{
    public const int MaxProfiles = 8, MaxAddresses = 500, MaxPortRanges = 16, MaxTitle = 60, MaxVariants = 8;

    [GeneratedRegex(@"^p[0-9a-f]{8}\z")] private static partial Regex IdRx();
    [GeneratedRegex(@"^[0-9]{1,5}(-[0-9]{1,5})?\z")] private static partial Regex PortRx();

    public static bool IsValidId(string? id) => id is not null && IdRx().IsMatch(id);

    public static string NewId() => "p" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();

    public static string FileName(string id) => $"app-{id}.txt";

    /// <summary>
    /// Порты в каноническом виде («443,27015-27030», по возрастанию, без пересечений) или null. Принимает только цифры, запятые и дефис;
    /// не больше <see cref="MaxPortRanges"/> элементов — иначе фильтр winws и перехват разрастутся.
    /// </summary>
    public static string? NormalizePorts(string? s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 200) return null;
        var ranges = new List<(int Lo, int Hi)>();
        foreach (var part in s.Split(','))
        {
            if (!PortRx().IsMatch(part)) return null;
            var ends = part.Split('-');
            int lo = int.Parse(ends[0]), hi = ends.Length == 2 ? int.Parse(ends[1]) : lo;
            if (lo < 1 || hi > 65535 || lo > hi) return null;
            ranges.Add((lo, hi));
        }
        var merged = new List<(int Lo, int Hi)>();
        foreach (var r in ranges.OrderBy(r => r.Lo))
        {
            if (merged.Count > 0 && r.Lo <= merged[^1].Hi + 1) merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, r.Hi));
            else merged.Add(r);
        }
        if (merged.Count > MaxPortRanges) return null;
        return string.Join(',', merged.Select(r => r.Lo == r.Hi ? r.Lo.ToString() : $"{r.Lo}-{r.Hi}"));
    }

    /// <summary>Набор портов → компактная строка: соседние сливаются; слишком много — верхние порты одним диапазоном.</summary>
    public static string? CompressPorts(IEnumerable<int> ports)
    {
        var all = ports.Where(p => p is >= 1 and <= 65535).Distinct().Order().ToList();
        if (all.Count == 0) return null;
        List<(int Lo, int Hi)> Runs(IEnumerable<int> xs)
        {
            var runs = new List<(int, int)>();
            foreach (var p in xs)
                if (runs.Count > 0 && p == runs[^1].Item2 + 1) runs[^1] = (runs[^1].Item1, p);
                else runs.Add((p, p));
            return runs;
        }
        var low = all.Where(p => p < 1024).Take(8).ToList();
        var high = Runs(all.Where(p => p >= 1024));
        if (high.Count > MaxPortRanges - low.Count) high = [(high[0].Lo, high[^1].Hi)];
        var final = Runs(low).Concat(high).ToList();
        return NormalizePorts(string.Join(',', final.Select(r => r.Lo == r.Hi ? r.Lo.ToString() : $"{r.Lo}-{r.Hi}")));
    }

    /// <summary>Что не так с профилем одной фразой; null — можно записывать и подавать winws.</summary>
    public static string? Problem(AppProfile p)
    {
        if (!IsValidId(p.Id)) return L.T("core.profile.bad_id");
        if (string.IsNullOrWhiteSpace(p.Title) || p.Title.Length > MaxTitle || p.Title.Any(char.IsControl)) return L.T("core.profile.bad_title");
        bool tcp = p.TcpPorts.Length > 0, udp = p.UdpPorts.Length > 0;
        if (!tcp && !udp) return L.T("core.profile.no_ports");
        if (tcp && NormalizePorts(p.TcpPorts) != p.TcpPorts) return L.T("core.profile.bad_tcp");
        if (udp && NormalizePorts(p.UdpPorts) != p.UdpPorts) return L.T("core.profile.bad_udp");
        if (p.Addresses.Count is 0 or > MaxAddresses) return L.T("core.profile.addr_count", new { max = MaxAddresses });
        foreach (var a in p.Addresses)
        {
            if (!Cidr.TryParse(a, requireLen: false, out var c, out var fixedHost, out _) || fixedHost || c.ToString() != Canonical(a))
                return L.T("core.profile.bad_addr", new { addr = Short(a) });
            if (IpsetValidator.Forbidden.Any(f => f.Net.Overlaps(c))) return L.T("core.profile.service_addr", new { addr = Short(a) });
            if (c.Len < (c.V6 ? 48 : 16)) return L.T("core.profile.wide_net", new { addr = Short(a) });
        }
        // Параметры оптимизации — тем же белым списком, что и каталог: только game_*, только разрешённые параметры, файлы из fake\.
        if (tcp && Desync("game_tcp", p.TcpDesync) is { } t) return L.T("core.profile.tcp_params", new { problem = t });
        if (udp && Desync("game_udp", p.UdpDesync) is { } u) return L.T("core.profile.udp_params", new { problem = u });
        return null;
    }

    static string? Desync(string role, string args) => args.Length == 0 ? L.T("core.profile.not_set") : WinwsArgs.Problem(role, args);

    static string Canonical(string a) => Cidr.TryParse(a, false, out var c, out _, out _) ? c.ToString() : "";
    static string Short(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>Содержимое файла адресов профиля: по строке, в каноническом виде.</summary>
    public static string FileText(AppProfile p) => string.Join('\n', p.Addresses) + "\n";

    /// <summary>
    /// Варианты параметров из готовых стратегий: сначала как у текущей, потом остальные; одинаковые пары не повторяются.
    /// TCP — game_tcp стратегии или выведенный из general (как в игровом режиме), UDP — game_udp или умолчание.
    /// </summary>
    public static IReadOnlyList<ProfileVariant> Variants(IEnumerable<Strategy> all, string? currentId)
    {
        var list = all.OrderBy(s => s.Id == currentId ? 0 : 1).ToList();
        var seen = new HashSet<(string, string)>();
        var result = new List<ProfileVariant>();
        foreach (var s in list)
        {
            var tcp = s.Roles.GetValueOrDefault("game_tcp") ?? WinwsArgs.DeriveGameTcp(s.Roles.GetValueOrDefault("general"));
            var udp = s.Roles.GetValueOrDefault("game_udp") ?? WinwsArgs.GameUdpDefault;
            if (tcp is null || WinwsArgs.Problem("game_tcp", tcp) is not null || WinwsArgs.Problem("game_udp", udp) is not null) continue;
            if (!seen.Add((tcp, udp))) continue;
            result.Add(new(result.Count, L.T(s.Id == currentId ? "apps.core.variant_current" : "apps.core.variant", new { title = s.Title }), tcp, udp));
            if (result.Count >= MaxVariants) break;
        }
        return result;
    }
}

/// <summary>Что получилось из записи: черновик профиля и отчёт «что убрано».</summary>
/// <param name="Removed">Причина → сколько подключений убрано.</param>
/// <param name="Processes">Процессы, чьи подключения вошли.</param>
public sealed record ProfileDraft(string Title, string TcpPorts, string UdpPorts, IReadOnlyList<string> Addresses,
    IReadOnlyDictionary<string, int> Removed, IReadOnlyList<string> Processes, int Kept, IReadOnlyList<string> Notes)
{
    public bool IsEmpty => Addresses.Count == 0 || (TcpPorts.Length == 0 && UdpPorts.Length == 0);
}

/// <summary>Запись → профиль: отсев шума, порты, адреса. Чистая функция без обращения к системе.</summary>
public static class AppProfileBuilder
{
    /// <summary>Сколько адресов одной сети /24 нужно увидеть, чтобы взять сеть целиком (серверы игры обычно рядом).</summary>
    public const int SubnetThreshold = 4;

    /// <summary>
    /// Что программа оставит сама: для каждого подключения — null (оставить) или причина, почему убрано.
    /// Те же правила у простого режима; продвинутый показывает их как стартовые галочки.
    /// </summary>
    public static IReadOnlyDictionary<RecordedFlow, string?> Sort(RecordingResult r)
    {
        bool processMode = r.Mode == "process";
        // Фон: адреса, к которым до игры ходили уже работавшие процессы (браузер, службы) — это не игра.
        var background = r.Flows.Where(f => !f.Fresh).Select(f => f.Address).ToHashSet();
        var d = new Dictionary<RecordedFlow, string?>();
        foreach (var f in r.Flows)
            d[f] = !f.Fresh ? L.T("apps.core.reason.before_rec")
                : FlowNoise.Reason(f, skipProcessNames: processMode)
                  ?? (background.Contains(f.Address) ? L.T("apps.core.reason.addr_before") : null);
        return d;
    }

    /// <param name="selected">Строки, отобранные человеком (продвинутый режим); null — программа выбирает сама.</param>
    public static ProfileDraft FromRecording(RecordingResult r, IReadOnlyCollection<RecordedFlow>? selected = null)
    {
        var removed = new Dictionary<string, int>();
        void Remove(string why) => removed[why] = removed.GetValueOrDefault(why) + 1;
        var notes = new List<string>(r.Notes);

        var kept = new List<RecordedFlow>();
        var sorted = Sort(r);
        foreach (var f in r.Flows)
        {
            if (selected is not null) { if (selected.Contains(f)) kept.Add(f); else Remove(L.T("apps.core.reason.manual")); continue; }
            if (sorted[f] is { } why) Remove(why); else kept.Add(f);
        }
        if (selected is not null)
        {
            // Ручной выбор тоже не пропускает локальную сеть и служебные порты: профиль на них не имеет смысла.
            var clean = kept.Where(f => FlowNoise.Reason(f, skipProcessNames: true) is null).ToList();
            if (clean.Count < kept.Count) { removed[L.T("apps.core.reason.local_port")] = kept.Count - clean.Count; kept = clean; }
        }

        var tcp = kept.Where(f => f.Proto == "tcp").Select(f => f.Port).ToList();
        var udp = kept.Where(f => f.Proto == "udp").Select(f => f.Port).ToList();
        var addresses = Addresses(kept, notes);
        var top = kept.GroupBy(f => f.Process).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        string title = top.Count > 0 ? Path.GetFileNameWithoutExtension(top[0]) : L.T("apps.core.default_title");
        if (title.Length > AppProfiles.MaxTitle) title = title[..AppProfiles.MaxTitle];
        if (!r.UdpRecorded) notes.Add(L.T("apps.core.note.no_udp"));
        return new(title, AppProfiles.CompressPorts(tcp) ?? "", AppProfiles.CompressPorts(udp) ?? "", addresses, removed,
            top.Take(8).ToList(), kept.Count, notes);
    }

    /// <summary>Адреса назначения: одиночные, а из ≥4 в одной /24 — вся /24. Лишнее сверх лимита отбрасывается.</summary>
    static List<string> Addresses(List<RecordedFlow> kept, List<string> notes)
    {
        var byAddr = kept.GroupBy(f => f.Address).Select(g => (Addr: g.Key, Count: g.Sum(x => x.Count))).OrderByDescending(x => x.Count).ToList();
        var nets = new List<string>();
        var v4Groups = byAddr.Where(x => IPAddress.TryParse(x.Addr, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            .GroupBy(x => string.Join('.', x.Addr.Split('.').Take(3))).ToList();
        foreach (var g in v4Groups)
        {
            if (g.Count() >= SubnetThreshold) nets.Add(g.Key + ".0/24");
            else nets.AddRange(g.Select(x => x.Addr + "/32"));
        }
        nets.AddRange(byAddr.Where(x => IPAddress.TryParse(x.Addr, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6).Select(x => x.Addr + "/128"));
        // Канонический вид и отсев служебных сетей — тем же валидатором, что у своих подсетей.
        var v = IpsetValidator.Validate(string.Join('\n', nets), IpsetKind.User);
        var entries = v.Entries.Where(e => Cidr.TryParse(e, false, out var c, out _, out _) && c.Len >= (c.V6 ? 48 : 16)).ToList();
        if (entries.Count > AppProfiles.MaxAddresses)
        {
            notes.Add(L.T("apps.core.note.too_many_addr", new { max = AppProfiles.MaxAddresses }));
            entries = entries.Take(AppProfiles.MaxAddresses).ToList();
        }
        return entries;
    }
}
