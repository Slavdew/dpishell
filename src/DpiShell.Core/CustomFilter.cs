using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>
/// «Свой фильтр WinDivert» в профиле (для опытных): текст, который попадёт в файл для <c>--wf-raw-part</c>. Помощник работает под SYSTEM и окну не доверяет,
/// поэтому фильтр разбирается по белому списку лексем и грамматике, а не пропускается как строка: только outbound/inbound, and/or/not, скобки,
/// сравнения, числа и поля tcp./udp./ip./ipv6. из списка. Обязательны outbound, протокол профиля и ограничение по DstPort на верхнем уровне
/// (по нему считается, что фильтр не пускает в winws весь трафик: «true» или голый «tcp» отклоняются).
/// </summary>
public static partial class CustomFilter
{
    public const int MaxLength = 4096;

    [GeneratedRegex(@"\G\s*(?:(?<p>[()])|(?<op>>=|<=|!=|=|<|>)|(?<ip>[0-9]{1,3}(?:\.[0-9]{1,3}){3}|[0-9A-Fa-f]*:[0-9A-Fa-f:]*)|(?<id>[A-Za-z][A-Za-z0-9_.]*(?:\[[0-9]{1,4}\])?)|(?<n>0[xX][0-9A-Fa-f]{1,8}|[0-9]{1,10}))")]
    private static partial Regex TokenRx();

    // Поля: (tcp|udp).(DstPort|SrcPort|PayloadLength|Payload[i]|Payload16[i]|Payload32[i]) и (ip|ipv6).(DstAddr|SrcAddr|Length).
    [GeneratedRegex(@"^(tcp|udp)\.(dstport|srcport|payloadlength|payload(?:16|32)?\[[0-9]{1,4}\])\z|^(ip|ipv6)\.(dstaddr|srcaddr|length)\z")]
    private static partial Regex FieldRx();

    abstract record Node;
    sealed record Flag(string Name) : Node;
    sealed record Atom(string Field, string Op) : Node;
    sealed record Not(Node Inner) : Node;
    sealed record Bin(bool IsAnd, List<Node> Items) : Node;

    sealed class Bad(string message) : Exception(message);

    /// <summary>Что не так с фильтром одной фразой с указанием места; null — можно писать в файл. <paramref name="tcp"/> — протокол профиля, к которому фильтр относится.</summary>
    public static string? Problem(string? filter, bool tcp)
    {
        if (string.IsNullOrEmpty(filter)) return L.T("core.filter.empty");
        if (Accepted.ContainsKey((filter, tcp))) return null;
        if (filter.Length > MaxLength) return L.T("core.filter.too_long", new { kb = MaxLength / 1024 });
        for (int i = 0; i < filter.Length; i++)
            if (char.IsControl(filter[i])) return L.T("core.filter.control", new { at = i + 1 });
        try
        {
            var ports = Check(filter, tcp);
            if (Accepted.Count >= MaxCached) Accepted.Clear();
            Accepted[(filter, tcp)] = ports;
            return null;
        }
        catch (Bad e) { return e.Message; }
    }

    /// <summary>Порты назначения, которые пропускает фильтр (диапазоны по возрастанию, без пересечений); null — фильтр не проходит <see cref="Problem"/>.</summary>
    public static IReadOnlyList<(int Lo, int Hi)>? DstPorts(string? filter, bool tcp) =>
        Problem(filter, tcp) is null && Accepted.TryGetValue((filter!, tcp), out var ports) ? ports : null;

    // Принятые фильтры и их порты: Problem зовётся на каждый status и на каждый профиль, разбор не повторяем. Отказы не кэшируются (текст зависит от языка).
    const int MaxCached = 256;
    static readonly ConcurrentDictionary<(string, bool), (int Lo, int Hi)[]> Accepted = new();

    /// <summary>Протокол фильтра по полям: true — tcp, false — udp, null — не определить или смешан.</summary>
    public static bool? ProtocolOf(string filter)
    {
        bool tcp = false, udp = false;
        foreach (Match m in ProtoRx().Matches(filter)) { if (m.Groups[1].Value.Equals("tcp", StringComparison.OrdinalIgnoreCase)) tcp = true; else udp = true; }
        return tcp == udp ? null : tcp;
    }

    [GeneratedRegex(@"\b(tcp|udp)\.", RegexOptions.IgnoreCase)] private static partial Regex ProtoRx();

    static (int Lo, int Hi)[] Check(string text, bool tcp)
    {
        var tokens = Lex(text);
        int i = 0;
        var root = ParseOr(tokens, ref i);
        if (i < tokens.Count) throw Place(tokens[i], text);
        // Верхний уровень — только «и»: иначе одно из условий могло бы пропустить весь трафик.
        if (root is Bin { IsAnd: false } or Not) throw new Bad(L.T("core.filter.top_or"));
        var top = root is Bin { IsAnd: true } b ? b.Items : new List<Node> { root };
        if (!top.Any(n => n is Flag { Name: "outbound" })) throw new Bad(L.T("core.filter.no_outbound"));
        string proto = tcp ? "tcp" : "udp";
        var protos = new HashSet<string>();
        Walk(root, a => { if (a.Field.StartsWith("tcp.") || a.Field.StartsWith("udp.")) protos.Add(a.Field[..3]); });
        if (protos.Count == 0) throw new Bad(L.T("core.filter.no_proto", new { proto = proto.ToUpperInvariant() }));
        if (protos.Count > 1 || !protos.Contains(proto)) throw new Bad(L.T("core.filter.other_proto", new { proto = proto.ToUpperInvariant() }));
        // Ограничение по DstPort: условие верхнего уровня, целиком из сравнений DstPort (без not и !=), под которое подходит не все порты и хотя бы один.
        string field = proto + ".dstport";
        var ports = top.Where(n => n is not Flag && PortOnly(n, field)).ToList();
        if (ports.Count == 0) throw new Bad(L.T("core.filter.no_port", new { proto = proto.ToUpperInvariant() }));
        var set = Full;
        foreach (var n in ports) set = Intersect(set, Ranges(n));
        long hits = set.Sum(r => (long)r.Hi - r.Lo + 1);
        if (hits == 0 || hits == 65536) throw new Bad(L.T("core.filter.port_all"));
        return set;
    }

    static Bad Place(Token t, string text) => new(L.T("core.filter.bad_token", new { at = t.Pos + 1, part = text.Substring(t.Pos, Math.Min(t.Len, 20)) }));

    record struct Token(string Kind, string Text, int Pos, int Len, long Value);

    static List<Token> Lex(string s)
    {
        var list = new List<Token>();
        int pos = 0;
        while (pos < s.Length)
        {
            if (s.AsSpan(pos).IsWhiteSpace()) break;
            var m = TokenRx().Match(s, pos);
            if (!m.Success) throw new Bad(L.T("core.filter.bad_token", new { at = pos + 1, part = s.Substring(pos, Math.Min(12, s.Length - pos)).Trim() }));
            string kind = "";
            foreach (var name in new[] { "p", "op", "ip", "id", "n" })
                if (m.Groups[name].Success) { kind = name; break; }
            var g = m.Groups[kind];
            long value = 0;
            if (kind == "n") value = g.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(g.Value[2..], 16) : long.Parse(g.Value);
            if (kind == "n" && value > uint.MaxValue) throw new Bad(L.T("core.filter.bad_token", new { at = g.Index + 1, part = g.Value }));
            if (kind == "ip" && !System.Net.IPAddress.TryParse(g.Value, out _)) throw new Bad(L.T("core.filter.bad_token", new { at = g.Index + 1, part = g.Value }));
            if (kind == "id")
            {
                var low = g.Value.ToLowerInvariant();
                if (low is not ("outbound" or "inbound" or "and" or "or" or "not") && !FieldRx().IsMatch(low))
                    throw new Bad(L.T("core.filter.bad_token", new { at = g.Index + 1, part = g.Value }));
                list.Add(new("id", low, g.Index, g.Length, 0));
            }
            else list.Add(new(kind, g.Value, g.Index, g.Length, value));
            pos = m.Index + m.Length;
        }
        return list;
    }

    static bool Word(List<Token> t, int i, string w) => i < t.Count && t[i].Kind == "id" && t[i].Text == w;

    static Node ParseOr(List<Token> t, ref int i)
    {
        var items = new List<Node> { ParseAnd(t, ref i) };
        while (Word(t, i, "or")) { i++; items.Add(ParseAnd(t, ref i)); }
        return items.Count == 1 ? items[0] : new Bin(false, items);
    }

    static Node ParseAnd(List<Token> t, ref int i)
    {
        var items = new List<Node> { ParseUnary(t, ref i) };
        while (Word(t, i, "and")) { i++; items.Add(ParseUnary(t, ref i)); }
        return items.Count == 1 ? items[0] : new Bin(true, items);
    }

    static Node ParseUnary(List<Token> t, ref int i)
    {
        if (Word(t, i, "not")) { i++; return new Not(ParseUnary(t, ref i)); }
        if (i >= t.Count) throw new Bad(L.T("core.filter.unexpected_end"));
        var tok = t[i];
        if (tok.Kind == "p" && tok.Text == "(")
        {
            i++;
            var n = ParseOr(t, ref i);
            if (i >= t.Count || t[i].Text != ")") throw new Bad(L.T("core.filter.unclosed"));
            i++;
            return n;
        }
        if (tok.Kind != "id" || tok.Text is "and" or "or") throw new Bad(L.T("core.filter.bad_token", new { at = tok.Pos + 1, part = tok.Text }));
        i++;
        if (tok.Text is "outbound" or "inbound") return new Flag(tok.Text);
        // поле op значение
        if (i >= t.Count || t[i].Kind != "op") throw new Bad(L.T("core.filter.need_compare", new { at = tok.Pos + 1, part = tok.Text }));
        var op = t[i++].Text;
        if (i >= t.Count || t[i].Kind is not ("n" or "ip")) throw new Bad(L.T("core.filter.need_value", new { at = tok.Pos + 1, part = tok.Text }));
        var val = t[i++];
        return new Atom(tok.Text, op + "|" + (val.Kind == "n" ? val.Value.ToString() : val.Text));
    }

    static void Walk(Node n, Action<Atom> f)
    {
        switch (n)
        {
            case Atom a: f(a); break;
            case Not x: Walk(x.Inner, f); break;
            case Bin b: foreach (var c in b.Items) Walk(c, f); break;
        }
    }

    /// <summary>Условие целиком из сравнений одного поля DstPort с числом, связанных and/or, без not и !=.</summary>
    static bool PortOnly(Node n, string field) => n switch
    {
        Atom a => a.Field == field && !a.Op.StartsWith("!=") && long.TryParse(a.Op[(a.Op.IndexOf('|') + 1)..], out _),
        Bin b => b.Items.All(c => PortOnly(c, field)),
        _ => false,
    };

    static readonly (int Lo, int Hi)[] Full = [(0, 65535)];

    /// <summary>Порты 0–65535, под которые подходит условие из сравнений DstPort, — диапазонами (без перебора всех портов).</summary>
    static (int Lo, int Hi)[] Ranges(Node n)
    {
        switch (n)
        {
            case Atom a:
                var bar = a.Op.IndexOf('|');
                var op = a.Op[..bar]; long v = long.Parse(a.Op[(bar + 1)..]);
                (long lo, long hi) = op switch { "=" => (v, v), "<" => (0, v - 1), "<=" => (0, v), ">" => (v + 1, 65535), ">=" => (v, 65535), _ => (1, 0) };
                lo = Math.Max(lo, 0); hi = Math.Min(hi, 65535);
                return lo > hi ? [] : [((int)lo, (int)hi)];
            case Bin b:
                var acc = b.IsAnd ? Full : [];
                foreach (var c in b.Items) acc = b.IsAnd ? Intersect(acc, Ranges(c)) : Union(acc, Ranges(c));
                return acc;
            default: return [];
        }
    }

    static (int Lo, int Hi)[] Intersect((int Lo, int Hi)[] x, (int Lo, int Hi)[] y)
    {
        var r = new List<(int, int)>();
        foreach (var a in x)
            foreach (var b in y)
                if (Math.Max(a.Lo, b.Lo) <= Math.Min(a.Hi, b.Hi)) r.Add((Math.Max(a.Lo, b.Lo), Math.Min(a.Hi, b.Hi)));
        return Union([], [.. r]);
    }

    static (int Lo, int Hi)[] Union((int Lo, int Hi)[] x, (int Lo, int Hi)[] y)
    {
        var merged = new List<(int Lo, int Hi)>();
        foreach (var r in x.Concat(y).OrderBy(r => r.Lo))
            if (merged.Count > 0 && r.Lo <= merged[^1].Hi + 1) merged[^1] = (merged[^1].Lo, Math.Max(merged[^1].Hi, r.Hi));
            else merged.Add(r);
        return [.. merged];
    }

    /// <summary>Входят ли все порты строки профиля («443,27015-27030») в диапазоны.</summary>
    public static bool Covers(IReadOnlyList<(int Lo, int Hi)> ranges, string ports) =>
        ports.Split(',', StringSplitOptions.RemoveEmptyEntries).All(part =>
        {
            var e = part.Split('-');
            int lo = int.Parse(e[0]), hi = e.Length == 2 ? int.Parse(e[1]) : lo;
            return ranges.Any(r => r.Lo <= lo && hi <= r.Hi);
        });
}
