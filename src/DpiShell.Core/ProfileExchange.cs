using System.Text;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>Профиль текстом в формате сборок Flowseal: три куска — фильтр WinDivert, параметры из general.bat, список подсетей.</summary>
public sealed record ProfileText(string Filter, string Params, string Subnets);

/// <summary>Результат разбора: профиль для сохранения или причина отказа одной фразой.</summary>
public sealed record ProfileImport(string Title, string TcpPorts, string UdpPorts, string TcpDesync, string UdpDesync,
    ProfileSignature Signature, IReadOnlyList<string> Addresses, IReadOnlyList<string> Notes)
{
    public ProfileSignature ToSignature() => Signature with { TcpDesync = TcpPorts.Length > 0 ? TcpDesync : null, UdpDesync = UdpPorts.Length > 0 ? UdpDesync : null };
}

public sealed record ProfileImportResult(ProfileImport? Profile, string? Problem);

/// <summary>
/// Обмен профилями в формате Flowseal и форков (общего стандарта нет; де-факто — три куска текста). Разбор строгий: фильтр — только
/// подмножество грамматики WinDivert, которое строит сама программа; параметры — тем же белым списком, что и каталог стратегий
/// (<see cref="WinwsArgs.Problem"/>), фейк-файлы — только из каталога; подсети — проверкой IP-списков (<see cref="IpsetValidator"/>).
/// Всё лишнее — отказ с указанием, что не подошло. Чистая функция: ни файлов, ни системы.
/// </summary>
public static partial class ProfileExchange
{
    // Параметры, которых в профиле быть не может: адреса, списки и фильтр WinDivert задаёт программа. Их из чужой строки молча отбрасываем и говорим об этом.
    static readonly string[] DroppedPrefixes = ["--wf-", "--ipset", "--hostlist", "--filter-l3", "--filter-l7", "--filter-icmp"];

    [GeneratedRegex(@"\G\s*(?:(?<p>[()])|(?<op>>=|<=|=)|(?<id>[A-Za-z][A-Za-z0-9_.]*(?:\[[0-9]{1,4}\])?)|(?<n>0[xX][0-9A-Fa-f]{1,8}|[0-9]{1,10}))")]
    private static partial Regex TokenRx();
    [GeneratedRegex(@"^[A-Za-z0-9_.-]+\.bin\z")] private static partial Regex FakeNameRx();
    [GeneratedRegex(@"^payload32\[([0-9]{1,4})\]\z")] private static partial Regex P32Rx();
    [GeneratedRegex(@"^payload\[([0-9]{1,4})\]\z")] private static partial Regex P8Rx();

    // ---------- экспорт ----------

    public static ProfileText Export(AppProfile p)
    {
        var filters = new[] { true, false }.Select(t => AppProfiles.Filter(p, t)).OfType<string>();
        var groups = new List<string>();
        foreach (var (ports, desync, tcp) in new[] { (p.TcpPorts, p.TcpDesync, true), (p.UdpPorts, p.UdpDesync, false) })
            if (ports.Length > 0)
                groups.Add($"--filter-{(tcp ? "tcp" : "udp")}={ports} " + desync.Replace("{fake}/", "%BIN%"));
        return new(string.Join('\n', filters), string.Join(" --new ", groups), string.Join('\n', p.Addresses));
    }

    // Заголовки для «Скопировать» одним куском: имена файлов сборки Flowseal, откуда берутся три части.
    public const string FilterMark = "# windivert.filter", ParamsMark = "# general.bat", SubnetsMark = "# ipset";

    /// <summary>Три куска одним текстом — для буфера обмена (вставить другу или в сборку Flowseal по частям).</summary>
    public static string Combine(ProfileText t) => string.Join(Lf, FilterMark, t.Filter, "", ParamsMark, t.Params, "", SubnetsMark, t.Subnets).TrimEnd() + Lf;

    const char Lf = (char)10, Cr = (char)13;

    /// <summary>Обратное к <see cref="Combine"/>: текст с заголовками → три куска; без заголовков — null.</summary>
    public static ProfileText? SplitCombined(string text)
    {
        string? current = null;
        var parts = new Dictionary<string, StringBuilder>();
        foreach (var raw in text.Split(Lf))
        {
            var line = raw.TrimEnd(Cr);
            var t = line.Trim();
            if (t is FilterMark or ParamsMark or SubnetsMark) { current = t; parts[t] = new(); continue; }
            if (current is not null) parts[current].AppendLine(line);
        }
        if (parts.Count == 0) return null;
        string Get(string k) => parts.TryGetValue(k, out var sb) ? sb.ToString().Trim() : "";
        return new(Get(FilterMark), Get(ParamsMark), Get(SubnetsMark));
    }

    // ---------- импорт ----------

    /// <param name="knownFakes">Имена фейк-файлов из каталога («stun.bin»): чужие файлы профиль не принимает.</param>
    /// <remarks>Фильтр, не разобравшийся в простые поля, принимается как свой фильтр (после <see cref="CustomFilter.Problem"/>), с пометкой в итоге.</remarks>
    public static ProfileImportResult Import(string title, string filterText, string paramsText, string subnetsText, IReadOnlySet<string> knownFakes)
    {
        try { return Parse(title, filterText ?? "", paramsText ?? "", subnetsText ?? "", knownFakes); }
        catch (ImportProblem e) { return new(null, e.Message); }
    }

    sealed class ImportProblem(string message) : Exception(message);

    static ImportProblem Bad(string key, object? args = null) => new(L.T(key, args));

    sealed record Sig(bool Tcp, string Ports, int Length, string Hex, string Custom = "");

    static ProfileImportResult Parse(string title, string filterText, string paramsText, string subnetsText, IReadOnlySet<string> knownFakes)
    {
        var notes = new List<string>();
        // 1. Фильтры: по строке на фильтр, один на протокол.
        var sigs = new Dictionary<bool, Sig>();
        foreach (var raw in filterText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            Sig sig;
            try { sig = ParseFilter(line); }
            catch (ImportProblem)
            {
                sig = CustomSig(line);
                notes.Add(L.T("core.xchg.filter.custom_note"));
            }
            if (!sigs.TryAdd(sig.Tcp, sig)) throw Bad("core.xchg.filter.twice", new { proto = sig.Tcp ? "TCP" : "UDP" });
        }
        // 2. Параметры: группы между --new, по одной на протокол.
        var groups = ParseParams(paramsText, sigs.Count == 1 ? sigs.Keys.First() : null, knownFakes, notes);
        // 3. Порты и сигнатуры по протоколам.
        string tcpPorts = "", udpPorts = "", tcpDesync = "", udpDesync = "";
        int tcpLen = 0, udpLen = 0; string tcpHex = "", udpHex = "", tcpFilter = "", udpFilter = "";
        foreach (var tcp in new[] { true, false })
        {
            sigs.TryGetValue(tcp, out var sig);
            groups.TryGetValue(tcp, out var g);
            if (sig is null && g is null) continue;
            string proto = tcp ? "TCP" : "UDP";
            if (g is null) throw Bad("core.xchg.no_params", new { proto });
            string ports = g.Ports.Length > 0 ? g.Ports : sig?.Ports ?? "";
            if (sig is { Ports.Length: > 0 } && g.Ports.Length > 0 && g.Ports != sig.Ports) throw Bad("core.xchg.ports_differ", new { proto, filter = sig.Ports, parameters = g.Ports });
            if (ports.Length == 0) throw Bad("core.xchg.no_ports", new { proto });
            // Без сигнатуры профиль получает все пакеты своих портов: any-protocol без cutoff шлёт фейки на каждый (winws: «TRASH FLOOD»).
            if (sig is null && CutoffMissing(g.Desync))
            {
                g = g with { Desync = g.Desync + " --dpi-desync-cutoff=n2" };
                notes.Add(L.T("core.xchg.cutoff_added", new { proto }));
            }
            if (tcp) { tcpPorts = ports; tcpDesync = g.Desync; tcpLen = sig?.Length ?? 0; tcpHex = sig?.Hex ?? ""; tcpFilter = sig?.Custom ?? ""; }
            else { udpPorts = ports; udpDesync = g.Desync; udpLen = sig?.Length ?? 0; udpHex = sig?.Hex ?? ""; udpFilter = sig?.Custom ?? ""; }
        }
        if (tcpPorts.Length == 0 && udpPorts.Length == 0) throw Bad("core.xchg.nothing");
        // 4. Подсети.
        var addresses = new List<string>();
        if (subnetsText.Any(c => !char.IsWhiteSpace(c)))
        {
            var v = IpsetValidator.Validate(subnetsText, IpsetKind.User);
            if (v.Dropped > 0) throw Bad("core.xchg.subnets_bad", new { problem = v.Problems.FirstOrDefault() ?? "" });
            if (v.Entries.Count > AppProfiles.MaxAddresses) throw Bad("core.xchg.subnets_many", new { n = v.Entries.Count, max = AppProfiles.MaxAddresses });
            addresses.AddRange(v.Entries);
        }
        var signature = new ProfileSignature(tcpLen, tcpHex, udpLen, udpHex, TcpFilter: tcpFilter, UdpFilter: udpFilter);
        var imp = new ProfileImport(title.Trim(), tcpPorts, udpPorts, tcpDesync, udpDesync, signature, addresses, notes);
        // Итоговая проверка тем же кодом, что у помощника.
        var probe = new AppProfile
        {
            Id = "p00000000", Title = imp.Title, TcpPorts = tcpPorts, UdpPorts = udpPorts, Addresses = addresses, TcpDesync = tcpDesync, UdpDesync = udpDesync,
            TcpPayloadLength = tcpLen, TcpPayloadHex = tcpHex, UdpPayloadLength = udpLen, UdpPayloadHex = udpHex,
            TcpCustomFilter = tcpFilter, UdpCustomFilter = udpFilter,
        };
        if (AppProfiles.Problem(probe) is { } problem) throw new ImportProblem(problem);
        return new(imp, null);
    }

    static bool CutoffMissing(string desync)
    {
        var args = desync.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return args.Any(a => a is "--dpi-desync-any-protocol" or "--dpi-desync-any-protocol=1") && !args.Any(a => a.StartsWith("--dpi-desync-cutoff="));
    }

    // ---------- фильтр WinDivert ----------

    abstract record Node;
    sealed record Flag(string Name) : Node;
    sealed record Atom(string Field, string Op, long Value) : Node;
    sealed record Bin(bool IsAnd, List<Node> Items) : Node;

    sealed class FilterParser(string text)
    {
        readonly List<(string Kind, string Text)> _t = Tokens(text);
        int _i;

        static List<(string, string)> Tokens(string s)
        {
            var list = new List<(string, string)>();
            int pos = 0;
            while (pos < s.Length)
            {
                if (s.AsSpan(pos).IsWhiteSpace()) break;
                var m = TokenRx().Match(s, pos);
                if (!m.Success) throw Bad("core.xchg.filter.unsupported", new { part = Cut(s[pos..].Trim()) });
                pos = m.Index + m.Length;
                foreach (var name in new[] { "p", "op", "id", "n" })
                    if (m.Groups[name].Success) { list.Add((name, m.Groups[name].Value)); break; }
            }
            return list;
        }

        public Node Parse()
        {
            var n = Or();
            if (_i < _t.Count) throw Bad("core.xchg.filter.unsupported", new { part = Cut(_t[_i].Text) });
            return n;
        }

        bool Word(string w) => _i < _t.Count && _t[_i].Kind == "id" && string.Equals(_t[_i].Text, w, StringComparison.OrdinalIgnoreCase);

        Node Or()
        {
            var items = new List<Node> { And() };
            while (Word("or")) { _i++; items.Add(And()); }
            return items.Count == 1 ? items[0] : new Bin(false, items);
        }

        Node And()
        {
            var items = new List<Node> { Primary() };
            while (Word("and")) { _i++; items.Add(Primary()); }
            return items.Count == 1 ? items[0] : new Bin(true, items);
        }

        Node Primary()
        {
            if (_i >= _t.Count) throw Bad("core.xchg.filter.unsupported", new { part = "…" });
            var (kind, txt) = _t[_i];
            if (kind == "p" && txt == "(")
            {
                _i++;
                var n = Or();
                if (_i >= _t.Count || _t[_i].Text != ")") throw Bad("core.xchg.filter.unsupported", new { part = "(" });
                _i++;
                return n;
            }
            if (kind != "id") throw Bad("core.xchg.filter.unsupported", new { part = Cut(txt) });
            _i++;
            if (_i < _t.Count && _t[_i].Kind == "op")
            {
                var op = _t[_i++].Text;
                if (_i >= _t.Count || _t[_i].Kind != "n") throw Bad("core.xchg.filter.unsupported", new { part = Cut(txt + " " + op) });
                var num = _t[_i++].Text;
                long v = num.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(num[2..], 16) : long.Parse(num);
                return new Atom(txt.ToLowerInvariant(), op, v);
            }
            return new Flag(txt.ToLowerInvariant());
        }
    }

    static string Cut(string s) => s.Length > 40 ? s[..40] + "…" : s;

    static Sig ParseFilter(string line)
    {
        var root = new FilterParser(line).Parse();
        var items = root is Bin { IsAnd: true } b ? b.Items : root is Bin ? throw Bad("core.xchg.filter.or_top") : new List<Node> { root };
        bool outbound = false; bool? tcp = null;
        var loose = new List<Atom>(); List<(int, int)>? groupPorts = null;
        int length = 0; var bytes = new Dictionary<int, byte>();

        bool Proto(string field)
        {
            var dot = field.IndexOf('.');
            var p = dot > 0 ? field[..dot] : field;
            if (p is not ("tcp" or "udp")) throw Bad("core.xchg.filter.unsupported", new { part = Cut(field) });
            bool isTcp = p == "tcp";
            if (tcp is { } t && t != isTcp) throw Bad("core.xchg.filter.both");
            tcp = isTcp;
            return isTcp;
        }
        string Suffix(string field) => field[(field.IndexOf('.') + 1)..];

        foreach (var it in items)
        {
            switch (it)
            {
                case Flag { Name: "outbound" }: outbound = true; break;
                case Flag f: throw Bad("core.xchg.filter.unsupported", new { part = Cut(f.Name) });
                case Atom a:
                {
                    Proto(a.Field);
                    var sfx = Suffix(a.Field);
                    if (sfx == "dstport") { loose.Add(a); break; }
                    if (a.Op != "=") throw Bad("core.xchg.filter.unsupported", new { part = Cut(a.Field + " " + a.Op) });
                    if (sfx == "payloadlength")
                    {
                        if (a.Value is < 1 or > AppProfiles.MaxPayloadLength) throw Bad("core.xchg.filter.length", new { max = AppProfiles.MaxPayloadLength });
                        length = (int)a.Value;
                    }
                    else if (P32Rx().Match(sfx) is { Success: true } m32)
                    {
                        int k = int.Parse(m32.Groups[1].Value);
                        if (k >= AppProfiles.MaxSignatureBytes / 4 || a.Value > uint.MaxValue) throw Bad("core.xchg.filter.payload");
                        for (int j = 0; j < 4; j++) if (!bytes.TryAdd(k * 4 + j, (byte)(a.Value >> (24 - 8 * j)))) throw Bad("core.xchg.filter.payload");
                    }
                    else if (P8Rx().Match(sfx) is { Success: true } m8)
                    {
                        int i = int.Parse(m8.Groups[1].Value);
                        if (i >= AppProfiles.MaxSignatureBytes || a.Value > 255 || !bytes.TryAdd(i, (byte)a.Value)) throw Bad("core.xchg.filter.payload");
                    }
                    else throw Bad("core.xchg.filter.unsupported", new { part = Cut(a.Field) });
                    break;
                }
                case Bin g:
                    if (groupPorts is not null) throw Bad("core.xchg.filter.ports");
                    groupPorts = PortsOf(g, Proto);
                    break;
            }
        }
        if (!outbound) throw Bad("core.xchg.filter.no_outbound");
        if (tcp is not { } isTcpFinal) throw Bad("core.xchg.filter.no_proto");

        List<(int Lo, int Hi)> ranges;
        if (loose.Count > 0)
        {
            if (groupPorts is not null) throw Bad("core.xchg.filter.ports");
            ranges = [LooseRange(loose)];
        }
        else ranges = groupPorts ?? throw Bad("core.xchg.filter.no_port");

        string? ports = AppProfiles.NormalizePorts(string.Join(',', ranges.Select(r => r.Lo == r.Hi ? r.Lo.ToString() : $"{r.Lo}-{r.Hi}")));
        if (ports is null) throw Bad("core.xchg.filter.port_range");

        // Байты сигнатуры — подряд с начала (дыры строить нечем) и не больше 16.
        string hex = "";
        if (bytes.Count > 0)
        {
            if (bytes.Keys.Max() + 1 != bytes.Count) throw Bad("core.xchg.filter.payload_gap");
            hex = Convert.ToHexString([.. Enumerable.Range(0, bytes.Count).Select(i => bytes[i])]).ToLowerInvariant();
        }
        if (AppProfiles.SignatureProblem(length, hex, true) is { } why) throw new ImportProblem(why);
        return new(isTcpFinal, ports, length, hex);
    }

    /// <summary>Фильтр не разобрался в простые поля: идёт как есть, как свой фильтр, но после проверки по белому списку.</summary>
    static Sig CustomSig(string line)
    {
        bool tcp = CustomFilter.ProtocolOf(line) ?? throw Bad("core.filter.no_proto_any");
        if (CustomFilter.Problem(line, tcp) is { } why) throw new ImportProblem(why);
        return new(tcp, "", 0, "", line.Trim());
    }

    static (int, int) LooseRange(List<Atom> atoms)
    {
        if (atoms.Count == 1 && atoms[0].Op == "=") return Range(atoms[0].Value, atoms[0].Value);
        if (atoms.Count == 2 && atoms.Any(a => a.Op == ">=") && atoms.Any(a => a.Op == "<="))
            return Range(atoms.First(a => a.Op == ">=").Value, atoms.First(a => a.Op == "<=").Value);
        throw Bad("core.xchg.filter.ports");
    }

    static (int, int) Range(long lo, long hi)
    {
        if (lo < 1 || hi > 65535 || lo > hi) throw Bad("core.xchg.filter.port_range");
        return ((int)lo, (int)hi);
    }

    /// <summary>Порты из скобок: <c>DstPort = n</c>, <c>DstPort &gt;= a and DstPort &lt;= b</c> или несколько таких через <c>or</c>.</summary>
    static List<(int, int)> PortsOf(Node n, Func<string, bool> proto)
    {
        switch (n)
        {
            case Atom { Op: "=" } a when a.Field.EndsWith(".dstport"): proto(a.Field); return [Range(a.Value, a.Value)];
            case Bin { IsAnd: true, Items: [Atom a1, Atom a2] } when a1.Field.EndsWith(".dstport") && a2.Field.EndsWith(".dstport"):
                proto(a1.Field); proto(a2.Field);
                return [LooseRange([a1, a2])];
            case Bin { IsAnd: false } o: return [.. o.Items.SelectMany(i => PortsOf(i, proto))];
            default: throw Bad("core.xchg.filter.ports");
        }
    }

    // ---------- параметры winws ----------

    sealed record Group(bool Tcp, string Ports, string Desync);

    static Dictionary<bool, Group> ParseParams(string text, bool? defaultTcp, IReadOnlySet<string> knownFakes, List<string> notes)
    {
        var result = new Dictionary<bool, Group>();
        var tokens = Split(text.Replace("^!", "!"));
        // Строка может быть скопирована целиком (start … winws.exe …): всё до первого параметра — оболочка вызова.
        int first = tokens.FindIndex(t => t.StartsWith("--", StringComparison.Ordinal));
        if (first < 0) return result;
        var dropped = new SortedSet<string>(StringComparer.Ordinal);
        var cur = new List<string>(); bool? tcp = null; string ports = "";

        void Flush()
        {
            if (cur.Count == 0 && tcp is null && ports.Length == 0) return;
            var proto = tcp ?? defaultTcp ?? throw Bad("core.xchg.params.no_proto");
            string name = proto ? "TCP" : "UDP";
            if (result.ContainsKey(proto)) throw Bad("core.xchg.params.twice", new { proto = name });
            var desync = string.Join(' ', cur);
            if (!cur.Any(t => t.StartsWith("--dpi-desync=", StringComparison.Ordinal))) throw Bad("core.xchg.params.empty", new { proto = name });
            if (WinwsArgs.Problem(proto ? "game_tcp" : "game_udp", desync) is { } why) throw Bad("core.xchg.params.problem", new { proto = name, problem = why });
            result[proto] = new(proto, ports, desync);
            cur = []; tcp = null; ports = "";
        }

        foreach (var tok in tokens.Skip(first))
        {
            if (tok == "^") continue;
            if (!tok.StartsWith("--", StringComparison.Ordinal)) throw Bad("core.xchg.params.stray", new { tok = Cut(tok) });
            var name = tok.Split('=', 2)[0];
            var val = tok.Contains('=') ? tok.Split('=', 2)[1] : "";
            if (name == "--new") { Flush(); continue; }
            if (name is "--filter-tcp" or "--filter-udp")
            {
                bool isTcp = name == "--filter-tcp";
                if (tcp is { } t && t != isTcp) throw Bad("core.xchg.params.both");
                tcp = isTcp;
                if (val.Contains('%')) throw Bad("core.xchg.params.variable", new { tok = Cut(tok) });
                ports = AppProfiles.NormalizePorts(val) ?? throw Bad(isTcp ? "core.profile.bad_tcp" : "core.profile.bad_udp");
                continue;
            }
            if (DroppedPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) { dropped.Add(name); continue; }
            if (val.Contains('%') && !val.Contains(".bin", StringComparison.OrdinalIgnoreCase)) throw Bad("core.xchg.params.variable", new { tok = Cut(tok) });
            cur.Add(val.Length > 0 && IsFileRef(val) ? name + "=" + Fake(val, knownFakes) : tok);
        }
        Flush();
        if (dropped.Count > 0) notes.Add(L.T("core.xchg.params.dropped", new { names = string.Join(", ", dropped) }));
        return result;
    }

    static bool IsFileRef(string v) => v.Contains('\\') || v.Contains('%') || v.Contains(".bin", StringComparison.OrdinalIgnoreCase) || v.StartsWith("{fake}/", StringComparison.Ordinal);

    /// <summary>Ссылка на файл фейка → <c>{fake}/имя.bin</c>, если такое имя есть в каталоге; пути из чужой сборки отбрасываются до имени.</summary>
    static string Fake(string value, IReadOnlySet<string> known)
    {
        int cut = value.LastIndexOfAny(['\\', '/', '%', '@']);
        var name = cut >= 0 ? value[(cut + 1)..] : value;
        if (!FakeNameRx().IsMatch(name)) throw Bad("core.args.path_outside", new { tok = Cut(value) });
        if (!known.Contains(name)) throw Bad("core.xchg.params.fake", new { file = name });
        return "{fake}/" + name;
    }

    /// <summary>Разбор строки по пробелам с двойными кавычками (они снимаются); переводы строк — тоже пробелы.</summary>
    static List<string> Split(string s)
    {
        var list = new List<string>(); var sb = new StringBuilder(); bool q = false, has = false;
        foreach (var c in s)
        {
            if (c == '"') { q = !q; has = true; continue; }
            if (char.IsWhiteSpace(c) && !q) { if (has || sb.Length > 0) list.Add(sb.ToString()); sb.Clear(); has = false; continue; }
            sb.Append(c);
        }
        if (has || sb.Length > 0) list.Add(sb.ToString());
        return list;
    }

    /// <summary>Имена файлов фейков из каталога стратегий — то, что профиль из импорта вправе использовать.</summary>
    public static HashSet<string> KnownFakes(IEnumerable<Strategy> strategies)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in strategies)
            foreach (var args in s.Roles.Values)
                foreach (Match m in FakeRefRx().Matches(args)) set.Add(m.Groups[1].Value);
        return set;
    }

    [GeneratedRegex(@"\{fake\}/([A-Za-z0-9_.-]+\.bin)")] private static partial Regex FakeRefRx();
}
