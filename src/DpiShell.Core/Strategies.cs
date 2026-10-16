using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiShell.Core;

/// <summary>
/// Стратегия — набор параметров десинхронизации по «ролям» (какой трафик обрабатывается).
/// Фильтры (порты, списки) ролям назначает <see cref="WinwsArgs"/>, а не каталог: так каталог
/// нельзя использовать, чтобы направить winws на чужие файлы.
/// </summary>
public sealed record Strategy(string Id, string Title, string Source, Dictionary<string, string> Roles);

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

/// <summary>Сборка командной строки winws. Повторяет устройство general.bat Flowseal без game filter.</summary>
public static class WinwsArgs
{
    public const string WfTcp = "80,443,2053,2083,2087,2096,8443";
    public const string WfUdp = "443,19294-19344,50000-50100";

    // Порядок ролей = порядок профилей: winws берёт первый подходящий профиль.
    public static readonly string[] RoleOrder = ["quic", "discord_udp", "discord_media", "google", "general", "ipset_udp", "ipset_tcp"];

    // Разрешённые параметры десинхронизации: всё, что встречается в проверенном каталоге (docs/03-vetting.md).
    static readonly HashSet<string> AllowedOptions =
    [
        "--dpi-desync", "--dpi-desync-repeats", "--dpi-desync-fooling", "--dpi-desync-split-pos", "--dpi-desync-split-seqovl",
        "--dpi-desync-split-seqovl-pattern", "--dpi-desync-fake-tls", "--dpi-desync-fake-tls-mod", "--dpi-desync-fake-http",
        "--dpi-desync-fake-quic", "--dpi-desync-fake-discord", "--dpi-desync-fake-stun", "--dpi-desync-fake-unknown-udp",
        "--dpi-desync-fakedsplit-pattern", "--dpi-desync-autottl", "--dpi-desync-ttl", "--dpi-desync-badseq-increment",
        "--dpi-desync-any-protocol", "--dpi-desync-cutoff", "--dpi-desync-fake-syndata", "--dpi-desync-hostfakesplit-mod",
        "--dpi-desync-hostfakesplit-midhost", "--dpi-desync-fake-tcp-mod", "--ip-id", "--dup", "--dup-cutoff", "--dup-fooling",
        "--dup-autottl", "--dup-ttl", "--dpi-desync-fakedsplit-mod", "--dpi-desync-start", "--dpi-desync-badack-increment",
    ];

    public static void Validate(Strategy s)
    {
        foreach (var (role, args) in s.Roles)
        {
            if (!RoleOrder.Contains(role)) throw new InvalidDataException($"{s.Id}: неизвестная роль {role}");
            foreach (var tok in args.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var name = tok.Split('=', 2)[0];
                if (!AllowedOptions.Contains(name)) throw new InvalidDataException($"{s.Id}: параметр {name} не в списке разрешённых");
                var val = tok.Contains('=') ? tok.Split('=', 2)[1] : "";
                if (val.Contains('/') && !val.StartsWith("{fake}/"))
                    throw new InvalidDataException($"{s.Id}: путь вне папки фейков: {tok}");
                if (val.Contains("..") || val.Contains('\\') || val.Contains(':') && !val.StartsWith("0x"))
                    throw new InvalidDataException($"{s.Id}: недопустимое значение {tok}");
            }
        }
    }

    public static IReadOnlyList<string> Build(Strategy s, AppPaths p)
    {
        string L(string f) => Path.Combine(p.Lists, f);
        var hostlists = $"--hostlist={L("general.txt")} --hostlist={L(DataStore.UserGeneral)} " +
                        $"--hostlist-exclude={L("exclude.txt")} --hostlist-exclude={L(DataStore.UserExclude)} " +
                        $"--ipset-exclude={L("ipset-exclude.txt")}";
        var ipsetAll = $"--ipset={L("ipset-all.txt")} --hostlist-exclude={L("exclude.txt")} " +
                       $"--hostlist-exclude={L(DataStore.UserExclude)} --ipset-exclude={L("ipset-exclude.txt")}";
        var filters = new Dictionary<string, string>
        {
            ["quic"] = $"--filter-udp=443 {hostlists}",
            ["discord_udp"] = "--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun",
            ["discord_media"] = "--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media",
            ["google"] = $"--filter-tcp=443 --hostlist={L("google.txt")}",
            ["general"] = $"--filter-tcp=80,443 {hostlists}",
            ["ipset_udp"] = $"--filter-udp=443 {ipsetAll}",
            ["ipset_tcp"] = $"--filter-tcp=80,443,8443 {ipsetAll}",
        };

        var args = new List<string> { $"--wf-tcp={WfTcp}", $"--wf-udp={WfUdp}" };
        bool first = true;
        foreach (var role in RoleOrder)
        {
            if (!s.Roles.TryGetValue(role, out var desync)) continue;
            if (!first) args.Add("--new");
            first = false;
            args.AddRange(filters[role].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            args.AddRange(desync.Replace("{fake}/", p.Fake + Path.DirectorySeparatorChar)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
        return args;
    }

    /// <summary>Строка для командной строки/службы. Пути в %ProgramData% без пробелов, но кавычки ставим на всякий случай.</summary>
    public static string Join(IEnumerable<string> args) =>
        string.Join(' ', args.Select(a => a.Contains(' ') ? Quote(a) : a));

    static string Quote(string a)
    {
        var i = a.IndexOf('=');
        return i > 0 ? $"{a[..(i + 1)]}\"{a[(i + 1)..]}\"" : $"\"{a}\"";
    }
}
