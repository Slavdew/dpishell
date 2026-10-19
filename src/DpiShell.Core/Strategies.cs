using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiShell.Core;

/// <summary>
/// Стратегия — набор параметров десинхронизации по «ролям» (какой трафик обрабатывается).
/// Фильтры (порты, списки) ролям назначает <see cref="WinwsArgs"/>, а не каталог: так каталог
/// нельзя использовать, чтобы направить winws на чужие файлы.
/// </summary>
/// <param name="Parts">Только у source "mix" (<see cref="MixBuilder"/>): из какой стратегии взята часть для каждой группы целей («youtube» → id, «discord» → id). У остальных null.</param>
public sealed record Strategy(string Id, string Title, string Source, Dictionary<string, string> Roles, Dictionary<string, string>? Parts = null);

public sealed record StrategyCatalog(string Engine, int Version, string Source, List<Strategy> Strategies)
{
    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static StrategyCatalog Load(string path)
    {
        var c = JsonSerializer.Deserialize<StrategyCatalog>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Пустой каталог стратегий");
        if (c.Engine != "winws") throw new InvalidDataException($"Движок {c.Engine} не поддерживается");
        foreach (var s in c.Strategies) WinwsArgs.Validate(s);
        return c;
    }

    public Strategy? Find(string id) => Strategies.FirstOrDefault(s => s.Id == id);
}

/// <summary>
/// Что сверх стратегии включено пользователем: режим IP-списка и игровой режим (architecture.md «IP-списки и игровой режим»).
/// Default — как при подборе: IP-список выключен (заглушка), игровой режим выключен.
/// </summary>
/// <param name="Advanced">Продвинутый режим «сайты → стратегия» (включён и строки разрешены); null — как обычно.</param>
public sealed record BuildOptions(IpsetMode Ipset = IpsetMode.Off, GameMode Game = GameMode.Off, IReadOnlyList<AppProfile>? Profiles = null, AdvancedPlan? Advanced = null)
{
    public static readonly BuildOptions Default = new();

    /// <summary>Игровые профили работают только по загруженному списку: при «Выкл» ловить нечего, при «Все адреса» — запрещено (§1).</summary>
    public bool GameTcp => Ipset == IpsetMode.List && Game is GameMode.TcpUdp or GameMode.Tcp;
    public bool GameUdp => Ipset == IpsetMode.List && Game is GameMode.TcpUdp or GameMode.Udp;

    /// <summary>Из state.json. «Все адреса» с истёкшим сроком уже здесь считается прежним режимом — даже если таймер окна не сработал.</summary>
    /// <param name="find">Стратегии для строк продвинутого режима; null — режим не учитывается (чтение настроек без каталога).</param>
    /// <param name="currentId">Текущая стратегия: по ней идут строки «auto» до подбора.</param>
    public static BuildOptions From(AppState s, DateTime utcNow, Func<string, Strategy?>? find = null, string? currentId = null)
    {
        var mode = ParseIpset(s.IpsetMode);
        if (mode == IpsetMode.Any && (s.IpsetAnyUntil is not { } until || until <= utcNow))
            mode = ParseIpset(s.IpsetModeBeforeAny) is var back && back != IpsetMode.Any ? back : IpsetMode.Off;
        // Профили программ (ТЗ 13): в state.json их мог тронуть кто угодно — в командную строку идут только прошедшие проверку.
        var profiles = s.AppProfiles.Where(p => AppProfiles.Problem(p) is null).Take(AppProfiles.MaxProfiles).ToList();
        var advanced = find is null ? null : AdvancedSites.Plan(s.Advanced, find, currentId);
        return new(mode, ParseGame(s.GameMode), profiles.Count > 0 ? profiles : null, advanced);
    }

    public static IpsetMode ParseIpset(string? s) => s switch { "list" => IpsetMode.List, "any" => IpsetMode.Any, _ => IpsetMode.Off };
    public static string Format(IpsetMode m) => m switch { IpsetMode.List => "list", IpsetMode.Any => "any", _ => "off" };
    public static GameMode ParseGame(string? s) => s switch { "all" => GameMode.TcpUdp, "tcp" => GameMode.Tcp, "udp" => GameMode.Udp, _ => GameMode.Off };
    public static string Format(GameMode m) => m switch { GameMode.TcpUdp => "all", GameMode.Tcp => "tcp", GameMode.Udp => "udp", _ => "off" };
}

/// <summary>Сборка командной строки winws. Повторяет устройство general.bat Flowseal, включая game filter.</summary>
public static class WinwsArgs
{
    public const string WfTcp = "80,443,2053,2083,2087,2096,8443";
    public const string WfUdp = "443,19294-19344,50000-50100";
    /// <summary>Порты игрового режима (Flowseal game filter, general.bat:16).</summary>
    public const string GamePorts = "1024-65535";

    /// <summary>UDP-профиль игр для стратегий без своей роли game_udp (general.bat:25).</summary>
    public const string GameUdpDefault =
        "--dpi-desync=fake --dpi-desync-repeats=12 --dpi-desync-any-protocol=1 --dpi-desync-fake-unknown-udp={fake}/ACTIVE_GAME_UDP.bin --dpi-desync-cutoff=n2";

    // Порядок ролей = порядок профилей: winws берёт первый подходящий профиль. Игровые — последними (general.bat:24-25).
    // discord_tcp (договор с роутером §5.3, каталог 3): TCP 80/443 по discord.txt — до google и general, чтобы Discord мог идти по своей части
    // смешанной стратегии. У каталожных стратегий этой роли нет — их командная строка не меняется.
    public static readonly string[] RoleOrder = ["quic", "discord_udp", "discord_media", "discord_tcp", "google", "general", "ipset_udp", "ipset_tcp", "game_tcp", "game_udp"];

    // Разрешённые параметры десинхронизации: всё, что встречается в проверенном каталоге (docs/vetting.md).
    static readonly HashSet<string> AllowedOptions =
    [
        "--dpi-desync", "--dpi-desync-repeats", "--dpi-desync-fooling", "--dpi-desync-split-pos", "--dpi-desync-split-seqovl",
        "--dpi-desync-split-seqovl-pattern", "--dpi-desync-fake-tls", "--dpi-desync-fake-tls-mod", "--dpi-desync-fake-http",
        "--dpi-desync-fake-quic", "--dpi-desync-fake-discord", "--dpi-desync-fake-stun",
        "--dpi-desync-fakedsplit-pattern", "--dpi-desync-autottl", "--dpi-desync-ttl", "--dpi-desync-badseq-increment",
        "--dpi-desync-fake-syndata", "--dpi-desync-hostfakesplit-mod",
        "--dpi-desync-hostfakesplit-midhost", "--dpi-desync-fake-tcp-mod", "--ip-id", "--dup", "--dup-cutoff", "--dup-fooling",
        "--dup-autottl", "--dup-ttl", "--dpi-desync-fakedsplit-mod", "--dpi-desync-start", "--dpi-desync-badack-increment",
    ];

    // Только в ролях game_* (architecture.md «IP-списки и игровой режим»): десинк любого протокола, ограниченный первыми пакетами, и фейки неизвестных протоколов.
    // Имена сверены с nfq/nfqws.c и docs/readme.md zapret v72.13.
    static readonly HashSet<string> GameOnlyOptions =
        ["--dpi-desync-any-protocol", "--dpi-desync-cutoff", "--dpi-desync-fake-unknown-udp", "--dpi-desync-fake-unknown"];

    // Параметры, значение которых — файл: только {fake}/имя.bin или шестнадцатеричные байты.
    // Голое имя winws искал бы в рабочем каталоге (core\), то есть мог бы прочитать свою же dll.
    static readonly HashSet<string> FileOptions =
    [
        "--dpi-desync-split-seqovl-pattern", "--dpi-desync-fake-tls", "--dpi-desync-fake-http", "--dpi-desync-fake-quic",
        "--dpi-desync-fake-discord", "--dpi-desync-fake-stun", "--dpi-desync-fake-unknown-udp", "--dpi-desync-fakedsplit-pattern",
        "--dpi-desync-fake-syndata", "--dpi-desync-fake-unknown",
    ];

    // Токен целиком: без пробельных символов, кавычек и обратных слешей — иначе строка службы разберётся иначе, чем список.
    // \z, а не $: '$' пропускал бы перевод строки в конце токена.
    static readonly System.Text.RegularExpressions.Regex TokenRx = new(@"^--[a-z0-9-]+(=[A-Za-z0-9_.,+=/{}!-]+)?\z");
    static readonly System.Text.RegularExpressions.Regex FileValueRx = new(@"^(\{fake\}/[A-Za-z0-9_.-]+\.bin|0x[0-9A-Fa-f]+|!)\z"); // '!' — встроенный фейк winws

    /// <summary>Предел строки одной роли: у самых длинных в каталоге около 500 знаков.</summary>
    public const int MaxRoleLength = 1200;

    public static void Validate(Strategy s)
    {
        foreach (var (role, args) in s.Roles)
            if (Problem(role, args) is { } problem) throw new InvalidDataException($"{s.Id}: {problem}");
    }

    /// <summary>
    /// Что не так с ролью, одной фразой для человека; null — всё в порядке. Единый разбор для каталога, своих и вставленных стратегий.
    /// <paramref name="fakeExists"/> — есть ли файл в папке фейков программы (имя без {fake}/); null — наличие не проверяется.
    /// </summary>
    public static string? Problem(string role, string args, Func<string, bool>? fakeExists = null)
    {
        if (!RoleOrder.Contains(role)) return $"Неизвестная роль «{Shorten(role)}»";
        if (args.Length > MaxRoleLength) return $"Строка роли «{role}» слишком длинная";
        foreach (var tok in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = tok.Split('=', 2)[0];
            if (!TokenRx.IsMatch(tok))
            {
                // Имя из белого списка, а значение с лишними символами — «недопустимые символы»; чужое имя — «не разрешён».
                var bare = System.Text.RegularExpressions.Regex.Match(tok, @"^--[a-z0-9-]+").Value;
                return AllowedOptions.Contains(bare) || GameOnlyOptions.Contains(bare)
                    ? $"В «{Shorten(tok)}» недопустимые символы"
                    : bare.Length > 0 ? $"Параметр {bare} не разрешён" : $"«{Shorten(tok)}» — не параметр winws";
            }
            if (!AllowedOptions.Contains(name))
            {
                if (GameOnlyOptions.Contains(name))
                {
                    if (!role.StartsWith("game_")) return $"Параметр {name} разрешён только в игровых ролях";
                }
                else return $"Параметр {name} не разрешён";
            }
            var val = tok.Contains('=') ? tok.Split('=', 2)[1] : "";
            if (FileOptions.Contains(name) ? !FileValueRx.IsMatch(val) : val.Contains('/'))
                return $"Путь вне папки программы: {Shorten(tok)}";
            if (val.Contains(".."))
                return $"Недопустимое значение: {Shorten(tok)}";
            if (fakeExists is not null && val.StartsWith("{fake}/", StringComparison.Ordinal) && !fakeExists(val["{fake}/".Length..]))
                return $"Файла {val["{fake}/".Length..]} нет в папке программы";
        }
        return null;
    }

    static string Shorten(string s) => s.Length > 40 ? s[..40] + "…" : s;

    /// <summary>
    /// Роли, которые попадут в командную строку: игровые — только при включённом режиме (из каталога, а если у стратегии
    /// их нет — выведенные из роли general, как устроены general*.bat), без него — никогда.
    /// </summary>
    public static Dictionary<string, string> ActiveRoles(Strategy s, BuildOptions o)
    {
        var roles = s.Roles.Where(r => !r.Key.StartsWith("game_")).ToDictionary(r => r.Key, r => r.Value);
        if (o.GameTcp && (s.Roles.GetValueOrDefault("game_tcp") ?? DeriveGameTcp(s.Roles.GetValueOrDefault("general"))) is { } t)
            roles["game_tcp"] = t;
        if (o.GameUdp) roles["game_udp"] = s.Roles.GetValueOrDefault("game_udp") ?? GameUdpDefault;
        return roles;
    }

    /// <summary>TCP-профиль игр из десинка general: любой протокол, только первые 3 пакета; с badseq — смещение вне окна (readme.md:349).</summary>
    public static string? DeriveGameTcp(string? general)
    {
        if (string.IsNullOrWhiteSpace(general)) return null;
        var t = general.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!t.Any(x => x.StartsWith("--dpi-desync-any-protocol"))) t.Add("--dpi-desync-any-protocol=1");
        if (!t.Any(x => x.StartsWith("--dpi-desync-cutoff="))) t.Add("--dpi-desync-cutoff=n3");
        if (t.Any(x => x.StartsWith("--dpi-desync-fooling=") && x.Contains("badseq")) && !t.Any(x => x.StartsWith("--dpi-desync-badseq-increment=")))
            t.Add("--dpi-desync-badseq-increment=0x80000000");
        return string.Join(' ', t);
    }

    public static IReadOnlyList<string> Build(Strategy s, AppPaths p, BuildOptions? options = null)
    {
        var o = options ?? BuildOptions.Default;
        string L(string f) => Path.Combine(p.Lists, f);
        // Режим IP-списка меняет только подставляемый файл (как у Flowseal), каталог тот же.
        string[] include = o.Ipset switch
        {
            IpsetMode.List => [$"--ipset={L(IpLists.AllFile)}", $"--ipset={L(IpLists.UserFile)}"],
            IpsetMode.Any => [$"--ipset={L(IpLists.AnyFile)}"],   // единственный и пустой: все include пусты = любой адрес
            _ => [$"--ipset={L(IpLists.NoneFile)}"],
        };
        string[] ipsetExclude = [$"--ipset-exclude={L(IpLists.ExcludeFile)}", $"--ipset-exclude={L(IpLists.UserExcludeFile)}"];
        // Списки собираются поэлементно: путь с пробелом остаётся одним аргументом.
        string[] excludes =
        [
            $"--hostlist-exclude={L("exclude.txt")}", $"--hostlist-exclude={L(DataStore.UserExclude)}",
            .. ipsetExclude,
        ];
        string[] hostlists = [$"--hostlist={L("general.txt")}", $"--hostlist={L(DataStore.UserGeneral)}", .. excludes];
        string[] ipsetAll =
        [
            .. include, $"--hostlist-exclude={L("exclude.txt")}",
            $"--hostlist-exclude={L(DataStore.UserExclude)}", .. ipsetExclude,
        ];
        // Продвинутый режим: «для всех адресов» — общие роли без своего списка имён, только исключения (как «Все сайты» на роутере).
        var adv = o.Advanced;
        var general = adv?.Others == AdvancedSites.OthersAll ? excludes : hostlists;
        var filters = new Dictionary<string, string[]>
        {
            ["quic"] = ["--filter-udp=443", .. general],
            ["discord_udp"] = ["--filter-udp=19294-19344,50000-50100", "--filter-l7=discord,stun"],
            ["discord_media"] = ["--filter-tcp=2053,2083,2087,2096,8443", "--hostlist-domains=discord.media"],
            // Свой список в фильтре, как у google: в режиме «все адреса» он остаётся (иначе роль забрала бы весь TCP до general).
            ["discord_tcp"] = ["--filter-tcp=80,443", $"--hostlist={L("discord.txt")}", .. excludes],
            ["google"] = ["--filter-tcp=443", $"--hostlist={L("google.txt")}"],
            ["general"] = ["--filter-tcp=80,443", .. general],
            ["ipset_udp"] = ["--filter-udp=443", .. ipsetAll],
            ["ipset_tcp"] = ["--filter-tcp=80,443,8443", .. ipsetAll],
            ["game_tcp"] = [$"--filter-tcp={GamePorts}", .. include, .. ipsetExclude],
            ["game_udp"] = [$"--filter-udp={GamePorts}", .. include, .. ipsetExclude],
        };

        var roles = ActiveRoles(s, o);
        // Профили программ: порты профиля добавляются в фильтр WinDivert (иначе пакеты не дойдут до winws), .
        var profiles = (o.Profiles ?? []).Where(pr => AppProfiles.Problem(pr) is null).ToList();
        string Wf(string baseWf, bool game, IEnumerable<string> extra)
        {
            var parts = new List<string> { baseWf };
            if (game) parts.Add(GamePorts);
            parts.AddRange(extra.Where(e => e.Length > 0));   // пересечение с игровым диапазоном фильтру WinDivert не мешает
            return string.Join(',', parts);
        }
        // WinDivert отдаёт winws верхние порты только при включённом игровом режиме — иначе это лишняя нагрузка на CPU.
        var args = new List<string>
        {
            $"--wf-tcp={Wf(WfTcp, roles.ContainsKey("game_tcp"), profiles.Select(pr => pr.TcpPorts))}",
            $"--wf-udp={Wf(WfUdp, roles.ContainsKey("game_udp"), profiles.Select(pr => pr.UdpPorts))}",
        };
        bool first = true;
        // Строки продвинутого режима — самые первые профили (первое совпадение): сайты строки → десинк общей роли её стратегии (и QUIC, если он есть).
        // Домены — только прошедшие строгий шаблон, параметры — только из белого списка; своих параметров строка не несёт.
        foreach (var row in adv?.Applied ?? [])
        {
            var domains = row.Domains.Where(AdvancedSites.IsDomain).Take(AdvancedSites.MaxDomains).ToList();
            if (domains.Count == 0) continue;
            foreach (var (role, filter) in new[] { ("general", "--filter-tcp=80,443"), ("quic", "--filter-udp=443") })
            {
                if (row.Strategy!.Roles.GetValueOrDefault(role) is not { } desync || Problem(role, desync) is not null) continue;
                if (!first) args.Add("--new");
                first = false;
                args.Add(filter);
                args.Add("--hostlist-domains=" + string.Join(',', domains));
                args.AddRange(desync.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Replace("{fake}/", p.Fake + Path.DirectorySeparatorChar)));
            }
        }
        // Сайты «напрямую» — исключения во всех ролях каталога (в режиме IP-списка нулевая фаза ipset-профиля их всё равно может задеть: имя ещё не известно).
        var direct = adv?.Direct.Where(AdvancedSites.IsDomain).ToList() ?? [];
        string[] directArgs = direct.Count > 0 ? ["--hostlist-exclude-domains=" + string.Join(',', direct)] : [];
        // Профили программ идут сразу после строк продвинутого режима и до общих ролей: winws берёт первый подходящий профиль, а general и quic ловили бы те же порты.
        foreach (var pr in profiles)
        {
            foreach (var (ports, desync, isTcp) in new[] { (pr.TcpPorts, pr.TcpDesync, true), (pr.UdpPorts, pr.UdpDesync, false) })
            {
                if (ports.Length == 0) continue;
                if (!first) args.Add("--new");
                first = false;
                args.Add($"--filter-{(isTcp ? "tcp" : "udp")}={ports}");
                args.Add($"--ipset={L(AppProfiles.FileName(pr.Id))}");
                args.AddRange(ipsetExclude);
                args.AddRange(desync.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Replace("{fake}/", p.Fake + Path.DirectorySeparatorChar)));
            }
        }
        foreach (var role in RoleOrder)
        {
            // «Остальные адреса — напрямую»: роли каталога не включаются, работают только строки (и профили программ).
            if (adv?.Others == AdvancedSites.OthersDirect) break;
            if (!roles.TryGetValue(role, out var desync)) continue;
            if (!first) args.Add("--new");
            first = false;
            args.AddRange(filters[role]);
            args.AddRange(directArgs);
            // Сначала режем каталожную строку (в ней пробелов внутри значений нет — см. Validate), потом подставляем путь.
            args.AddRange(desync.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Replace("{fake}/", p.Fake + Path.DirectorySeparatorChar)));
        }
        return args;
    }

    /// <summary>Строка для командной строки/службы. Кавычки — только у аргументов с пробелами (пути).</summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a =>
        {
            if (a.Contains('"')) throw new ArgumentException($"Кавычка в аргументе winws: {a}");
            return a.Any(char.IsWhiteSpace) ? Quote(a) : a;
        }));

    static string Quote(string a)
    {
        var i = a.IndexOf('=');
        return i > 0 ? $"{a[..(i + 1)]}\"{a[(i + 1)..]}\"" : $"\"{a}\"";
    }
}
