using DpiShell.Core.Localization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Стратегия, разобранная из чужого текста: название и роли. Id и источник назначает тот, кто её сохраняет.</summary>
/// <param name="FromBat">Разобрана из строки запуска winws (.bat Flowseal), а не из строки DpiShell.</param>
public sealed record ParsedStrategy(string Title, IReadOnlyDictionary<string, string> Roles, bool FromBat = false);

/// <summary>Причина отказа одной фразой для человека («Параметр --xxx не разрешён», «Строка повреждена»).</summary>
public sealed class StrategyFormatException(string reason) : Exception(reason);

/// <summary>
/// Обмен стратегиями одной строкой: <c>dpishell:1:</c> + base64url(deflate(JSON {title, roles})). Строгий разбор: версия, размер,
/// только известные роли, только параметры из белого списка <see cref="WinwsArgs"/> (как у каталога), файлы fake — только
/// из папки программы. Строка приходит от постороннего человека, а winws работает с правами администратора, поэтому
/// всё, что не прошло проверку, отклоняется целиком — ничего не «чинится» молча.
/// Вторая форма входа — строка запуска winws из .bat Flowseal (<see cref="FlowsealBat"/>).
/// </summary>
public static class StrategyExchange
{
    public const string Prefix = "dpishell:1:";
    /// <summary>Длина строки обмена (знаков).</summary>
    public const int MaxText = 8 * 1024;
    /// <summary>JSON после распаковки: защита от «бомбы» — читаем не больше.</summary>
    public const int MaxJson = 16 * 1024;
    public const int MaxTitle = 60;

    /// <summary>Строка обмена из названия и ролей (роли — в порядке командной строки).</summary>
    public static string Encode(string title, IReadOnlyDictionary<string, string> roles)
    {
        var ordered = new JsonObject();
        foreach (var (role, args) in roles.OrderBy(r => Array.IndexOf(WinwsArgs.RoleOrder, r.Key)))
            ordered[role] = args;
        var json = new JsonObject { ["title"] = CleanTitle(title), ["roles"] = ordered }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var raw = Encoding.UTF8.GetBytes(json);
        using var ms = new MemoryStream();
        using (var d = new DeflateStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) d.Write(raw);
        var text = Prefix + Convert.ToBase64String(ms.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (text.Length > MaxText) throw new StrategyFormatException(L.T("core.xchg.too_long"));
        return text;
    }

    /// <summary>Строка <c>dpishell:1:…</c> или строка запуска winws из .bat. Отказ — <see cref="StrategyFormatException"/>.</summary>
    public static ParsedStrategy Parse(string? text, Func<string, bool>? fakeExists = null)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new StrategyFormatException(L.T("core.xchg.clipboard_empty"));
        var t = text.Trim();
        return t.StartsWith("dpishell:", StringComparison.OrdinalIgnoreCase) ? Decode(t, fakeExists) : FlowsealBat.Parse(t, fakeExists);
    }

    public static ParsedStrategy Decode(string text, Func<string, bool>? fakeExists = null)
    {
        var t = text.Trim();
        if (t.Length > MaxText) throw new StrategyFormatException(L.T("core.xchg.line_too_long", new { kb = MaxText / 1024 }));
        if (!t.StartsWith(Prefix, StringComparison.Ordinal))
        {
            var m = Regex.Match(t, @"^dpishell:(\d{1,4}):", RegexOptions.IgnoreCase);
            throw new StrategyFormatException(m.Success ? L.T("core.xchg.version", new { version = m.Groups[1].Value }) : L.T("core.xchg.damaged"));
        }
        var body = t[Prefix.Length..];
        if (body.Length == 0 || !Regex.IsMatch(body, @"^[A-Za-z0-9_-]+\z")) throw new StrategyFormatException(L.T("core.xchg.damaged"));
        byte[] packed;
        try
        {
            var b64 = body.Replace('-', '+').Replace('_', '/');
            packed = Convert.FromBase64String(b64 + new string('=', (4 - b64.Length % 4) % 4));
        }
        catch (FormatException) { throw new StrategyFormatException(L.T("core.xchg.damaged")); }

        byte[] raw;
        try
        {
            using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            var buf = new byte[4096];
            int n;
            while ((n = inflate.Read(buf, 0, buf.Length)) > 0)
            {
                ms.Write(buf, 0, n);
                if (ms.Length > MaxJson) throw new StrategyFormatException(L.T("core.xchg.too_big_unpacked"));
            }
            raw = ms.ToArray();
        }
        catch (InvalidDataException) { throw new StrategyFormatException(L.T("core.xchg.damaged")); }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { MaxDepth = 4, AllowDuplicateProperties = false });
            if (node is JsonObject probe) _ = probe.Count;   // повтор ключа всплывает лениво
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { throw new StrategyFormatException(L.T("core.xchg.damaged")); }
        if (node is not JsonObject o) throw new StrategyFormatException(L.T("core.xchg.damaged"));
        foreach (var (k, _) in o)
            if (k is not ("title" or "roles")) throw new StrategyFormatException(L.T("core.xchg.extra_field", new { name = Cut(k) }));
        if (o["title"] is not JsonValue tv || !tv.TryGetValue<string>(out var title)) throw new StrategyFormatException(L.T("core.xchg.no_title"));
        if (o["roles"] is not JsonObject ro) throw new StrategyFormatException(L.T("core.xchg.no_roles"));
        var roles = new Dictionary<string, string>();
        foreach (var (role, v) in ro)
        {
            if (v is not JsonValue rv || !rv.TryGetValue<string>(out var args)) throw new StrategyFormatException(L.T("core.xchg.role_needs_string", new { role = Cut(role) }));
            roles[role] = args;
        }
        if (CheckRoles(roles, fakeExists) is { } problem) throw new StrategyFormatException(problem);
        return new(CleanTitle(title), roles);
    }

    /// <summary>
    /// Роли целиком: не пусто, каждая роль непуста и проходит белый список (<see cref="WinwsArgs.Problem"/>).
    /// Единая проверка для вставки, .bat и окна правки. Null — всё в порядке.
    /// </summary>
    public static string? CheckRoles(IReadOnlyDictionary<string, string> roles, Func<string, bool>? fakeExists = null)
    {
        if (roles.Count == 0) return L.T("core.xchg.no_role_at_all");
        foreach (var role in WinwsArgs.RoleOrder.Concat(roles.Keys.Except(WinwsArgs.RoleOrder)).Where(roles.ContainsKey))
        {
            var args = roles[role];
            if (WinwsArgs.RoleOrder.Contains(role) && args.Trim().Length == 0) return L.T("core.xchg.role_empty", new { role });
            if (WinwsArgs.Problem(role, args, fakeExists) is { } p) return p;
        }
        return null;
    }

    /// <summary>Название для показа: без управляющих и невидимых знаков (в том числе меняющих направление текста), в одну строку, не длиннее <see cref="MaxTitle"/>.</summary>
    public static string CleanTitle(string? title)
    {
        var sb = new StringBuilder();
        foreach (var r in (title ?? "").EnumerateRunes())
        {
            var cat = Rune.GetUnicodeCategory(r);
            if (cat is System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator)
                sb.Append(' ');   // перевод строки и табуляция — пробел
            else if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.OtherNotAssigned or System.Globalization.UnicodeCategory.PrivateUse)
                continue;         // невидимые знаки, в том числе меняющие направление текста, — убрать
            else sb.Append(r.ToString());
        }
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        if (s.Length > MaxTitle) s = s[..MaxTitle].TrimEnd();
        return s.Length == 0 ? L.T("core.xchg.untitled") : s;
    }

    static string Cut(string s) => s.Length > 30 ? s[..30] + "…" : s;
}

/// <summary>
/// Строка запуска winws из .bat Flowseal → роли. Тем же способом, как каталог собран из general*.bat (docs/vetting.md): фильтры, списки
/// и порты winws снимаются — их назначает <see cref="WinwsArgs.Build"/>; профиль (до <c>--new</c>) относится к роли по порту и виду списка;
/// <c>^!</c> → <c>!</c>; <c>%BIN%x.bin</c> → <c>{fake}/x.bin</c> (файл должен лежать в папке фейков программы). Неоднозначное — отказ с причиной.
/// </summary>
public static class FlowsealBat
{
    public const int MaxText = 16 * 1024;

    static readonly Regex ListFileRx = new(@"^(%LISTS%|lists[\\/])?[A-Za-z0-9_.-]+\.txt\z", RegexOptions.IgnoreCase);
    static readonly Regex DomainsRx = new(@"^[A-Za-z0-9.,-]+\z");
    static readonly Regex WfRx = new(@"^[0-9A-Za-z,%-]+\z");
    static readonly Regex PortsRx = new(@"^[0-9,%A-Za-z-]+\z");
    static readonly Regex BinRx = new(@"^(%BIN%|bin[\\/])([A-Za-z0-9_.-]+\.bin)\z", RegexOptions.IgnoreCase);

    public static ParsedStrategy Parse(string text, Func<string, bool>? fakeExists = null)
    {
        if (text.Length > MaxText) throw new StrategyFormatException(L.T("core.xchg.text_too_long", new { kb = MaxText / 1024 }));
        // Продолжение строки cmd: «^» в конце. «^!» (экранированный «!») остаётся и разбирается ниже.
        var joined = Regex.Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), @"\^[ \t]*\n[ \t]*", " ");
        var lines = joined.Split('\n').Where(l => l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        string line;
        if (lines.Count == 1)
        {
            var i = lines[0].IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
            line = lines[0][(i + "winws.exe".Length)..];
            // "%BIN%winws.exe" ...: закрывающая кавычка имени файла не должна открыть кавычки у разбора.
            if (line.StartsWith('"')) line = line[1..];
        }
        else if (lines.Count > 1) throw new StrategyFormatException(L.T("core.xchg.several_runs"));
        else if (joined.TrimStart().StartsWith("--", StringComparison.Ordinal) && !joined.Trim().Contains('\n')) line = joined;
        else throw new StrategyFormatException(L.T("core.xchg.not_ours"));

        line = line.Replace("^!", "!");
        if (line.Contains('^')) throw new StrategyFormatException(L.T("core.xchg.cmd_chars"));
        var tokens = Tokenize(line);
        var profiles = new List<List<string>> { new() };
        foreach (var tok in tokens)
        {
            if (tok == "--new") { profiles.Add([]); continue; }
            profiles[^1].Add(tok);
        }
        var roles = new Dictionary<string, string>();
        foreach (var profile in profiles)
        {
            var (role, desync) = ReadProfile(profile);
            if (role is null) continue;   // только --wf-*: не профиль
            if (roles.ContainsKey(role)) throw new StrategyFormatException(L.T("core.xchg.two_profiles", new { role }));
            roles[role] = desync;
        }
        if (roles.Count == 0) throw new StrategyFormatException(L.T("core.xchg.no_profile"));
        if (StrategyExchange.CheckRoles(roles, fakeExists) is { } problem) throw new StrategyFormatException(problem);
        return new(L.T("core.xchg.from_bat"), roles, FromBat: true);
    }

    /// <summary>Разбор по пробелам с учётом двойных кавычек (путь в кавычках). Кавычки в значения не попадают.</summary>
    static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (any) { tokens.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            if (c < ' ') throw new StrategyFormatException(L.T("core.xchg.control_chars"));
            sb.Append(c); any = true;
        }
        if (quoted) throw new StrategyFormatException(L.T("core.xchg.open_quote"));
        if (any) tokens.Add(sb.ToString());
        return tokens;
    }

    sealed class Filters { public string? Tcp, Udp; public bool Hostlist, Ipset, Domains; public string HostlistName = ""; }

    static (string? Role, string Desync) ReadProfile(List<string> tokens)
    {
        var f = new Filters();
        var desync = new List<string>();
        bool onlyWf = true;
        foreach (var tok in tokens)
        {
            if (!tok.StartsWith("--", StringComparison.Ordinal)) throw new StrategyFormatException(L.T("core.args.not_winws", new { tok = Cut(tok) }));
            var eq = tok.IndexOf('=');
            var name = eq < 0 ? tok : tok[..eq];
            var value = eq < 0 ? "" : tok[(eq + 1)..];
            switch (name)
            {
                case "--wf-tcp" or "--wf-udp":
                    if (!WfRx.IsMatch(value)) throw new StrategyFormatException(L.T("core.xchg.bad_value_name", new { name }));
                    continue;   // перехват WinDivert программа задаёт сама
                case "--filter-tcp" or "--filter-udp":
                    if (!PortsRx.IsMatch(value)) throw new StrategyFormatException(L.T("core.xchg.bad_ports", new { name }));
                    if (name == "--filter-tcp") f.Tcp = value; else f.Udp = value;
                    break;
                case "--filter-l7":
                    if (!Regex.IsMatch(value, @"^[a-z0-9,]+\z")) throw new StrategyFormatException(L.T("core.xchg.bad_l7"));
                    break;
                case "--hostlist" or "--hostlist-exclude" or "--ipset" or "--ipset-exclude":
                    if (!ListFileRx.IsMatch(value)) throw new StrategyFormatException(L.T("core.args.path_outside", new { tok = Cut(tok) }));
                    if (name == "--hostlist") { f.Hostlist = true; f.HostlistName = value; }
                    else if (name == "--ipset") f.Ipset = true;
                    break;
                case "--hostlist-domains":
                    if (!DomainsRx.IsMatch(value)) throw new StrategyFormatException(L.T("core.xchg.bad_value_name", new { name }));
                    f.Domains = true;
                    break;
                default:
                    desync.Add(Desync(name, value, tok));
                    break;
            }
            onlyWf = false;
        }
        if (onlyWf) return (null, "");
        var line = string.Join(' ', desync);
        string role;
        try { role = Classify(f); }
        catch (StrategyFormatException)
        {
            // Сначала названное неразрешённое: «Параметр --xxx не разрешён» полезнее, чем «не удалось отнести профиль».
            if (WinwsArgs.Problem("game_tcp", line) is { } p) throw new StrategyFormatException(p);
            throw;
        }
        return (role, line);
    }

    static string Desync(string name, string value, string tok)
    {
        // Значение в кавычках с пробелом ("fake --wf-raw=x") не должно превратиться в два параметра.
        if (tok.Any(char.IsWhiteSpace)) throw new StrategyFormatException(L.T("core.args.bad_chars", new { tok = Cut(tok) }));
        var m = BinRx.Match(value);
        if (m.Success) value = "{fake}/" + m.Groups[2].Value;
        else if (value.Contains('%')) throw new StrategyFormatException(L.T("core.xchg.unknown_variable", new { tok = Cut(tok) }));
        return value.Length == 0 ? name : name + "=" + value;
    }

    /// <summary>Роль профиля по портам и виду списка. Порты игрового режима (%GameFilterTCP%/UDP, 1024-65535) снимаются.</summary>
    static string Classify(Filters f)
    {
        if (f.Tcp is not null && f.Udp is not null) throw new StrategyFormatException(L.T("core.xchg.tcp_and_udp"));
        if (f.Tcp is null && f.Udp is null) throw new StrategyFormatException(L.T("core.xchg.no_filter"));
        if (f.Hostlist && f.Ipset) throw new StrategyFormatException(L.T("core.xchg.hostlist_and_ipset"));
        bool tcp = f.Tcp is not null;
        var ports = (tcp ? f.Tcp : f.Udp)!;
        var rest = ports.Split(',').Where(p => p.Length > 0 && !p.StartsWith("%Game", StringComparison.OrdinalIgnoreCase) && p != WinwsArgs.GamePorts).ToList();
        string norm = string.Join(',', rest);
        if (rest.Count == 0) return tcp ? "game_tcp" : "game_udp";
        if (rest.Count != ports.Split(',').Length && !f.Ipset) throw new StrategyFormatException(L.T("core.xchg.ports_mixed", new { ports }));
        string? role = (tcp, norm) switch
        {
            (false, "443") when f.Ipset => "ipset_udp",
            (false, "443") when f.Hostlist => "quic",
            (false, "19294-19344,50000-50100") => "discord_udp",
            (true, "2053,2083,2087,2096,8443") when f.Domains => "discord_media",
            (true, "443") when f.Hostlist && f.HostlistName.Contains("google", StringComparison.OrdinalIgnoreCase) => "google",
            (true, "80,443") when f.Hostlist && f.HostlistName.Contains("discord", StringComparison.OrdinalIgnoreCase) => "discord_tcp",
            (true, "80,443") when f.Hostlist => "general",
            (true, "80,443") or (true, "80,443,8443") when f.Ipset => "ipset_tcp",
            _ => null,
        };
        return role ?? throw new StrategyFormatException(L.T("core.xchg.no_role_match", new { proto = tcp ? "tcp" : "udp", ports = Cut(ports) }));
    }

    static string Cut(string s) => s.Length > 40 ? s[..40] + "…" : s;
}
