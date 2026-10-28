using System.Text.Json.Nodes;
using DpiShell.Core.Localization;

namespace DpiShell.Core;

/// <summary>
/// Профиль игры одной строкой: <c>dpishell-game:1:</c> + base64url(deflate(JSON)), как у стратегий (<see cref="StrategyExchange"/>).
/// JSON — <c>{title, tcp: {ports, len, hex, filter, desync}, udp: {…}, addresses}</c>; у протокола обязательны только порты.
/// В строку идут порты, сигнатура или свой фильтр, адреса и свои параметры профиля («Свои»); включён ли профиль и какую стратегию ему выбрали — нет:
/// это настройки этого ПК. Разбор строгий: лишнее поле, чужой тип, неразрешённый параметр, фейк не из папки программы, служебная или широкая подсеть —
/// отказ целиком, ничего не «чинится». Итог проверяется <see cref="AppProfiles.Problem"/> — тем же правилом, что у помощника.
/// </summary>
public static class ProfileCode
{
    public const string Family = "dpishell-game";
    public const string Prefix = Family + ":1:";
    /// <summary>Длина строки (знаков): 500 адресов и два своих фильтра сжимаются примерно до 10 КБ.</summary>
    public const int MaxText = 32 * 1024;
    /// <summary>JSON после распаковки — защита от «бомбы».</summary>
    public const int MaxJson = 64 * 1024;

    static readonly string[] ProtoKeys = ["ports", "len", "hex", "filter", "desync"];

    /// <summary>Строка обмена профиля. Своих параметров нет — у протокола нет <c>desync</c> (у получателя профиль пойдёт стратегией его ПК).</summary>
    public static string Encode(AppProfile p)
    {
        var o = new JsonObject { ["title"] = StrategyExchange.CleanTitle(p.Title) };
        foreach (var tcp in new[] { true, false })
        {
            var ports = tcp ? p.TcpPorts : p.UdpPorts;
            if (ports.Length == 0) continue;
            var x = new JsonObject { ["ports"] = ports };
            int len = tcp ? p.TcpPayloadLength : p.UdpPayloadLength;
            string hex = tcp ? p.TcpPayloadHex : p.UdpPayloadHex, filter = tcp ? p.TcpCustomFilter : p.UdpCustomFilter, desync = tcp ? p.TcpDesync : p.UdpDesync;
            if (len > 0) x["len"] = len;
            if (hex.Length > 0) x["hex"] = hex;
            if (filter.Length > 0) x["filter"] = filter;
            if (desync.Length > 0) x["desync"] = desync;
            o[tcp ? "tcp" : "udp"] = x;
        }
        if (p.Addresses.Count > 0) o["addresses"] = new JsonArray([.. p.Addresses.Select(a => (JsonNode)a)]);
        return StrategyExchange.Pack(Prefix, o, MaxText, "core.game_code.too_long");
    }

    /// <summary>Похоже ли на строку профиля (для «Вставить из буфера» в окне «Новый профиль»).</summary>
    public static bool Looks(string? text) => text is not null && text.TrimStart().StartsWith(Family + ":", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Строка → профиль (Id — заглушка, выбор стратегии — «Свои», если параметры есть, иначе «По умолчанию»; включён). Отказ — <see cref="StrategyFormatException"/> с причиной.
    /// <paramref name="fakeExists"/> — есть ли файл фейка в папке программы (имя без {fake}/).
    /// </summary>
    public static AppProfile Decode(string? text, Func<string, bool> fakeExists)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new StrategyFormatException(L.T("core.xchg.clipboard_empty"));
        var t = text.Trim();
        if (t.StartsWith("dpishell:", StringComparison.OrdinalIgnoreCase)) throw new StrategyFormatException(L.T("core.game_code.is_strategy"));
        if (!Looks(t)) throw new StrategyFormatException(L.T("core.game_code.not_code"));
        var o = StrategyExchange.Unpack(t, Family, MaxText, MaxJson, 4);
        foreach (var (k, _) in o)
            if (k is not ("title" or "tcp" or "udp" or "addresses")) throw new StrategyFormatException(L.T("core.xchg.extra_field", new { name = StrategyExchange.Cut(k) }));
        if (o["title"] is not JsonValue tv || !tv.TryGetValue<string>(out var title)) throw new StrategyFormatException(L.T("core.xchg.no_title"));
        var p = new AppProfile { Id = "p00000000", Title = StrategyExchange.CleanTitle(title), Enabled = true };
        foreach (var tcp in new[] { true, false })
        {
            var key = tcp ? "tcp" : "udp";
            if (!o.ContainsKey(key)) continue;
            if (o[key] is not JsonObject x) throw new StrategyFormatException(L.T("core.game_code.bad_field", new { name = key }));
            foreach (var (k, _) in x)
                if (!ProtoKeys.Contains(k)) throw new StrategyFormatException(L.T("core.xchg.extra_field", new { name = key + "." + StrategyExchange.Cut(k) }));
            string Str(string name)
            {
                if (!x.ContainsKey(name)) return "";
                return x[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new StrategyFormatException(L.T("core.game_code.bad_field", new { name = key + "." + name }));
            }
            var ports = Str("ports");
            if (ports.Length == 0) throw new StrategyFormatException(L.T("core.xchg.no_ports", new { proto = key.ToUpperInvariant() }));
            int len = 0;
            if (x.ContainsKey("len") && (x["len"] is not JsonValue lv || !lv.TryGetValue(out len) || len < 1 || len > AppProfiles.MaxPayloadLength))
                throw new StrategyFormatException(L.T("core.game_code.bad_field", new { name = key + ".len" }));
            string hex = Str("hex"), filter = Str("filter"), desync = Str("desync");
            // Фейки — только из папки программы: строка пришла от постороннего, а winws работает с правами администратора.
            if (desync.Length > 0 && WinwsArgs.Problem(tcp ? "game_tcp" : "game_udp", desync, fakeExists) is { } why) throw new StrategyFormatException(why);
            if (tcp) { p.TcpPorts = ports; p.TcpPayloadLength = len; p.TcpPayloadHex = hex; p.TcpCustomFilter = filter; p.TcpDesync = desync; }
            else { p.UdpPorts = ports; p.UdpPayloadLength = len; p.UdpPayloadHex = hex; p.UdpCustomFilter = filter; p.UdpDesync = desync; }
        }
        if (p.TcpPorts.Length == 0 && p.UdpPorts.Length == 0) throw new StrategyFormatException(L.T("core.xchg.nothing"));
        if (o.ContainsKey("addresses"))
        {
            if (o["addresses"] is not JsonArray arr || arr.Count > AppProfiles.MaxAddresses)
                throw new StrategyFormatException(L.T("core.game_code.bad_field", new { name = "addresses" }));
            var lines = new List<string>();
            foreach (var a in arr)
                lines.Add(a is JsonValue av && av.TryGetValue<string>(out var s) ? s : throw new StrategyFormatException(L.T("core.game_code.bad_field", new { name = "addresses" })));
            // Подсети — проверкой IP-списков (служебные сети, маски); отброшенное — отказ, как у ввода из сборки.
            var v = IpsetValidator.Validate(string.Join('\n', lines), IpsetKind.User);
            if (v.Dropped > 0 || v.Entries.Count != lines.Count) throw new StrategyFormatException(L.T("core.xchg.subnets_bad", new { problem = v.Problems.FirstOrDefault() ?? "" }));
            p.Addresses = [.. v.Entries];
        }
        p.Strategy = p.HasOwn ? AppProfiles.Own : AppProfiles.Default;
        if (AppProfiles.Problem(p) is { } problem) throw new StrategyFormatException(problem);
        return p;
    }

    /// <summary>Сигнатура, свой фильтр и свои параметры разобранного профиля — для <c>app_profile_set</c> (параметров нет — null: профиль пойдёт стратегией ПК).</summary>
    public static ProfileSignature ToSignature(AppProfile p) => new(p.TcpPayloadLength, p.TcpPayloadHex, p.UdpPayloadLength, p.UdpPayloadHex,
        p.TcpPorts.Length > 0 && p.TcpDesync.Length > 0 ? p.TcpDesync : null, p.UdpPorts.Length > 0 && p.UdpDesync.Length > 0 ? p.UdpDesync : null,
        p.TcpCustomFilter, p.UdpCustomFilter);
}
