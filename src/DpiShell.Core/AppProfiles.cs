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
    /// <summary>
    /// Свои параметры профиля («Свои»: пришли из сборки Flowseal или строки обмена) для game_tcp / game_udp — тот же белый список, что у каталога.
    /// Пусто — своих нет, профиль идёт игровой частью стратегии (<see cref="Strategy"/>).
    /// </summary>
    public string TcpDesync { get; set; } = "";
    public string UdpDesync { get; set; } = "";
    /// <summary>Профиль включён: в командную строку winws идут только включённые (каждый — отдельно, несколько сразу).</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>
    /// Чем идёт профиль при «Разных стратегиях» (<see cref="AppState.ProfilesSeparate"/>): <see cref="AppProfiles.Default"/> — игровая часть стратегии ПК,
    /// иначе id стратегии (её игровая часть). Без «Разных стратегий» — «По умолчанию», выбор хранится. Профиль со своими параметрами идёт ими всегда
    /// (<see cref="AppProfiles.Own"/>), выбора у него нет.
    /// null — запись до API 1.22 (выбор выводится из <see cref="Variant"/>, <see cref="AppProfiles.MigrateChoices"/>).
    /// </summary>
    public string? Strategy { get; set; }
    /// <summary>
    /// Сигнатура пакета (необязательна, для TCP и UDP отдельно): длина полезной нагрузки (0 — не задана) и её начало, hex до 16 байт.
    /// Задана — помощник сам собирает фильтр WinDivert (<see cref="AppProfiles.Filter"/>), порты этого протокола в общий фильтр не попадают.
    /// </summary>
    public int TcpPayloadLength { get; set; }
    public string TcpPayloadHex { get; set; } = "";
    public int UdpPayloadLength { get; set; }
    public string UdpPayloadHex { get; set; } = "";
    /// <summary>
    /// «Свой фильтр WinDivert» (для опытных), по протоколам: заполнен — заменяет фильтр, собранный из сигнатуры. Проверка — <see cref="CustomFilter.Problem"/>.
    /// </summary>
    public string TcpCustomFilter { get; set; } = "";
    public string UdpCustomFilter { get; set; } = "";
    /// <summary>До API 1.22: номер варианта параметров; -1 — свои параметры. Читается только для перевода в <see cref="Strategy"/>.</summary>
    public int Variant { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Копия (список адресов — свой).</summary>
    public AppProfile Clone()
    {
        var c = (AppProfile)MemberwiseClone();
        c.Addresses = [.. Addresses];
        return c;
    }

    /// <summary>Свои параметры есть (хотя бы у одного протокола профиля).</summary>
    public bool HasOwn => TcpPorts.Length > 0 && TcpDesync.Length > 0 || UdpPorts.Length > 0 && UdpDesync.Length > 0;
}

/// <summary>Вариант параметров для профиля: пара game_tcp/game_udp из готовых стратегий (первым — текущая). <paramref name="StrategyId"/> — с API 1.22.</summary>
public sealed record ProfileVariant(int Index, string Title, string Tcp, string Udp, string StrategyId = "");

/// <summary>Правила профилей: что допустимо в state.json и в командной строке. Всё, что попадёт в winws, проходит здесь.</summary>
public static partial class AppProfiles
{
    public const int MaxProfiles = 8, MaxAddresses = 500, MaxPortRanges = 16, MaxTitle = 60, MaxVariants = 8;
    /// <summary>Выбор стратегии профиля: игровая часть стратегии ПК / свои параметры (иначе — id стратегии).</summary>
    public const string Default = "default", Own = "own";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z")] private static partial Regex ChoiceRx();

    /// <summary>Выбор по форме: «default», «own» или id стратегии (есть ли такая — решает сборка, нет — «По умолчанию»).</summary>
    public static bool IsValidChoice(string? s) => s is not null && ChoiceRx().IsMatch(s);

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
        if (tcp && p.TcpCustomFilter.Length > 0 && CustomFilter.Problem(p.TcpCustomFilter, true) is { } ct) return L.T("core.profile.tcp_filter", new { problem = ct });
        if (udp && p.UdpCustomFilter.Length > 0 && CustomFilter.Problem(p.UdpCustomFilter, false) is { } cu) return L.T("core.profile.udp_filter", new { problem = cu });
        if (!tcp && p.TcpCustomFilter.Length > 0 || !udp && p.UdpCustomFilter.Length > 0) return L.T("core.profile.sig_no_ports");
        if (SignatureProblem(p.TcpPayloadLength, p.TcpPayloadHex, tcp) is { } st) return L.T("core.profile.tcp_sig", new { problem = st });
        if (SignatureProblem(p.UdpPayloadLength, p.UdpPayloadHex, udp) is { } su) return L.T("core.profile.udp_sig", new { problem = su });
        // Адреса нужны, если у какого-то протокола профиля нет сигнатуры: иначе профиль ловил бы всё на своих портах.
        bool needAddresses = tcp && !HasSignature(p, true) || udp && !HasSignature(p, false);
        if (p.Addresses.Count > MaxAddresses || p.Addresses.Count == 0 && needAddresses) return L.T("core.profile.addr_count", new { max = MaxAddresses });
        foreach (var (ports, custom, isTcp) in new[] { (p.TcpPorts, p.TcpCustomFilter, true), (p.UdpPorts, p.UdpCustomFilter, false) })
        {
            if (ports.Length == 0) continue;
            string proto = isTcp ? "TCP" : "UDP";
            // Профиль winws выбирается по портам: порты сверх DstPort своего фильтра ловили бы всё подряд (их перехватывают общие роли).
            if (custom.Length > 0 && CustomFilter.DstPorts(custom, isTcp) is { } allowed && !CustomFilter.Covers(allowed, ports))
                return L.T("core.profile.filter_ports", new { proto, ports });
            // Без адресов профиль ловит в winws все пакеты своих портов. Порты, которые WinDivert перехватывает для сайтов, ушли бы в игровой десинк.
            if (p.Addresses.Count == 0 && FirstCommonPort(ports, isTcp ? WinwsArgs.WfTcp : WinwsArgs.WfUdp) is { } port)
                return L.T("core.profile.wf_overlap", new { port, proto });
        }
        if (Filter(p, true) is { } ft && ft.Length > MaxFilterBytes || Filter(p, false) is { } fu && fu.Length > MaxFilterBytes)
            return L.T("core.profile.filter_limit", new { kb = MaxFilterBytes / 1024 });
        foreach (var a in p.Addresses)
        {
            if (!Cidr.TryParse(a, requireLen: false, out var c, out var fixedHost, out _) || fixedHost || c.ToString() != Canonical(a))
                return L.T("core.profile.bad_addr", new { addr = Short(a) });
            if (IpsetValidator.Forbidden.Any(f => f.Net.Overlaps(c))) return L.T("core.profile.service_addr", new { addr = Short(a) });
            if (c.Len < (c.V6 ? 48 : 16)) return L.T("core.profile.wide_net", new { addr = Short(a) });
        }
        // Свои параметры (если есть) — тем же белым списком, что и каталог: только game_*, только разрешённые параметры, файлы из fake\.
        // Без своих профиль идёт игровой частью стратегии из каталога — она проверена при загрузке каталога.
        if (tcp && p.TcpDesync.Length > 0 && WinwsArgs.Problem("game_tcp", p.TcpDesync) is { } t) return L.T("core.profile.tcp_params", new { problem = t });
        if (udp && p.UdpDesync.Length > 0 && WinwsArgs.Problem("game_udp", p.UdpDesync) is { } u) return L.T("core.profile.udp_params", new { problem = u });
        if (!tcp && p.TcpDesync.Length > 0 || !udp && p.UdpDesync.Length > 0) return L.T("core.profile.sig_no_ports");
        if (p.Strategy is not null && !IsValidChoice(p.Strategy)) return L.T("core.profile.bad_choice");
        return null;
    }

    static List<(int Lo, int Hi)> PortRanges(string ports) => ports.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part =>
    {
        var e = part.Split('-');
        int lo = int.Parse(e[0]);
        return (lo, e.Length == 2 ? int.Parse(e[1]) : lo);
    }).ToList();

    /// <summary>Наименьший общий порт двух строк портов или null.</summary>
    public static int? FirstCommonPort(string a, string b) =>
        (from x in PortRanges(a) from y in PortRanges(b) where Math.Max(x.Lo, y.Lo) <= Math.Min(x.Hi, y.Hi) select (int?)Math.Max(x.Lo, y.Lo)).Min();

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
            var (tcp, udp) = GameParts(s);
            if (tcp is null || WinwsArgs.Problem("game_tcp", tcp) is not null || WinwsArgs.Problem("game_udp", udp) is not null) continue;
            if (!seen.Add((tcp, udp))) continue;
            result.Add(new(result.Count, L.T(s.Id == currentId ? "apps.core.variant_current" : "apps.core.variant", new { title = s.Title }), tcp, udp, s.Id));
            if (result.Count >= MaxVariants) break;
        }
        return result;
    }

    /// <summary>
    /// Игровая часть стратегии — как у «Общего» игрового режима: game_tcp стратегии или выведенный из general (<see cref="WinwsArgs.DeriveGameTcp"/>),
    /// game_udp или умолчание. TCP null — у стратегии нет ни game_tcp, ни general.
    /// </summary>
    public static (string? Tcp, string Udp) GameParts(Strategy s) =>
        (s.Roles.GetValueOrDefault("game_tcp") ?? WinwsArgs.DeriveGameTcp(s.Roles.GetValueOrDefault("general")), s.Roles.GetValueOrDefault("game_udp") ?? WinwsArgs.GameUdpDefault);

    /// <summary>
    /// Действующий выбор: свои параметры есть — всегда «Свои» (подобраны под игру, стратегии каталога им не замена; владелец 28.10, вопрос 93);
    /// иначе без «Разных стратегий» — «По умолчанию», с ними — выбранная стратегия.
    /// </summary>
    public static string Choice(AppProfile p, bool separate) =>
        p.HasOwn ? Own : !separate || p.Strategy is null or Own ? Default : p.Strategy;

    /// <summary>
    /// Профиль для сборки: копия, в которой <see cref="AppProfile.TcpDesync"/>/<see cref="AppProfile.UdpDesync"/> — то, чем он пойдёт сейчас,
    /// кроме «По умолчанию»: там пусто, и <see cref="WinwsArgs.Build"/> берёт игровую часть той стратегии, с которой собирает (стратегия ПК меняется — меняется и она).
    /// Стратегии из выбора нет (удалена) — «По умолчанию».
    /// </summary>
    public static AppProfile ForBuild(AppProfile p, bool separate, Func<string, Strategy?>? find)
    {
        var c = p.Clone();
        var choice = Choice(p, separate);
        if (choice == Own) return c;
        c.TcpDesync = c.UdpDesync = "";
        if (choice != Default && find?.Invoke(choice) is { } s)
        {
            var (tcp, udp) = GameParts(s);
            if (p.TcpPorts.Length > 0 && tcp is not null && WinwsArgs.Problem("game_tcp", tcp) is null) c.TcpDesync = tcp;
            if (p.UdpPorts.Length > 0 && WinwsArgs.Problem("game_udp", udp) is null) c.UdpDesync = udp;
        }
        return c;
    }

    /// <summary>Профиль, как он пойдёт со стратегией ПК <paramref name="current"/>: «По умолчанию» тоже заполнено (для показа, экспорта настроек и приманок связки).</summary>
    public static AppProfile Effective(AppProfile p, bool separate, Func<string, Strategy?>? find, Strategy? current)
    {
        var c = ForBuild(p, separate, find);
        if (current is not null)
        {
            var (tcp, udp) = GameParts(current);
            if (c.TcpPorts.Length > 0 && c.TcpDesync.Length == 0) c.TcpDesync = tcp ?? "";
            if (c.UdpPorts.Length > 0 && c.UdpDesync.Length == 0) c.UdpDesync = udp;
        }
        return c;
    }

    /// <summary>
    /// Профили до API 1.22 (<see cref="AppProfile.Strategy"/> = null): «вариант» → выбор. Свои параметры (Variant -1) — «Свои»; вариант 0 (как у текущей
    /// стратегии) — «По умолчанию», своих нет; другой вариант — та стратегия, чья игровая часть совпала с записанной; не нашлась — записанное остаётся «Своими»
    /// (ничего не теряется). True — у профиля без своих параметров выбрана стратегия (значит, нужны «Разные стратегии», чтобы всё шло как раньше).
    /// </summary>
    public static bool MigrateChoices(IEnumerable<AppProfile> profiles, IEnumerable<Strategy> all, string? currentId)
    {
        var list = all.ToList();
        bool separate = false;
        // Негодный профиль (state.json трогали) не «чинится» переводом: он как был не идёт в winws.
        foreach (var p in profiles.Where(p => p.Strategy is null && Problem(p) is null))
        {
            if (p.Variant < 0) p.Strategy = Own;
            else if (p.Variant == 0) { p.Strategy = Default; p.TcpDesync = p.UdpDesync = ""; }
            else
            {
                var match = list.Where(s => s.Id != currentId).FirstOrDefault(s => GameParts(s) is var (t, u) && (p.TcpPorts.Length == 0 || t == p.TcpDesync) && (p.UdpPorts.Length == 0 || u == p.UdpDesync));
                if (match is not null) { p.Strategy = match.Id; p.TcpDesync = p.UdpDesync = ""; }
                else p.Strategy = Own;
            }
            p.Variant = 0;
            if (p.Strategy is not (Default or Own)) separate = true;
        }
        return separate;
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
