using System.Text;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Имена приманок ПК. <see cref="Reliable"/> = false: часть имён случайная (rndsni, hostfakesplit без host=) — список заменён на все имена фейков из поставки.</summary>
public sealed record DecoyNames(IReadOnlyList<string> Names, bool Reliable, IReadOnlyList<string> Notes);

/// <summary>
/// Имена «приманок» ПК (docs/research/mixed-strategies.md §7, стенд docs/research stand-2026-10-20): роутер берёт имя соединения из ПЕРВОГО ClientHello,
/// который увидел, и после приманки (fake-tls, fake-http, seqovl-pattern, badsum/md5sig/badseq — всё это до него доходит) настоящее имя уже не меняет.
/// Поэтому имена приманок обязаны быть в списке исключений роутера для этого ПК (<c>domains</c> в <c>pc_link_set</c>).
/// Имена берутся из параметров стратегии: TLS ClientHello в файле фейка читается по-настоящему (SNI), HTTP — по Host, QUIC — по имени файла
/// (<c>quic_initial_www_google_com.bin</c>), <c>sni=</c> и <c>host=</c> — из значений параметров, встроенные фейки winws — их зашитые имена.
/// Что угадать нельзя (случайный SNI: <c>rndsni</c>, по умолчанию при фейке без своего файла и без <c>--dpi-desync-fake-tls-mod</c>;
/// <c>hostfakesplit</c> без <c>host=</c>), — список заменяется всеми именами фейков из поставки, и <see cref="DecoyNames.Reliable"/> = false.
/// </summary>
public static partial class JointDecoys
{
    /// <summary>SNI встроенного фейка TLS в nfqws (nfq/desync.c, zapret v72.13).</summary>
    public const string BuiltinTls = "www.microsoft.com";
    /// <summary>Host встроенного фейка HTTP (nfq/desync.c, zapret v72.13).</summary>
    public const string BuiltinHttp = "www.iana.org";

    // Параметры, значение которых — содержимое фейка (файл {fake}/имя.bin или 0x…): из него читается SNI или Host.
    static readonly string[] ContentOptions =
    [
        "--dpi-desync-fake-tls", "--dpi-desync-fake-http", "--dpi-desync-split-seqovl-pattern", "--dpi-desync-fakedsplit-pattern",
        "--dpi-desync-fake-syndata", "--dpi-desync-fake-unknown",
    ];

    /// <param name="desyncs">Роль и параметры десинхронизации (то, что WinwsArgs подставляет в командную строку: general, quic, профили программ, роли строк).</param>
    /// <param name="readFake">Файл из папки фейков по имени (без «{fake}/»); null — файла нет.</param>
    /// <param name="supplyNames">Все имена фейков из поставки — на случай, когда точный список не получить.</param>
    public static DecoyNames Of(IEnumerable<(string Role, string Desync)> desyncs, Func<string, byte[]?> readFake, Func<IReadOnlyList<string>>? supplyNames = null)
    {
        var names = new List<string>();
        var notes = new List<string>();
        bool reliable = true;
        void Add(string? n)
        {
            if (n is null) return;
            n = n.Trim().ToLowerInvariant();
            if (AdvancedSites.IsDomain(n) && !names.Contains(n)) names.Add(n);
        }

        foreach (var (role, desync) in desyncs.Where(d => !string.IsNullOrWhiteSpace(d.Desync)))
        {
            var tokens = desync.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Split('=', 2)).Select(p => (Name: p[0], Value: p.Length > 1 ? p[1] : "")).ToList();
            List<string> All(string name) => [.. tokens.Where(t => t.Name == name).Select(t => t.Value)];

            var modes = All("--dpi-desync").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToHashSet();
            bool plainFake = modes.Contains("fake") || modes.Contains("fakeknown");

            // Файлы и байты фейков: имена читаются из содержимого.
            var customTls = All("--dpi-desync-fake-tls");
            foreach (var opt in ContentOptions)
                foreach (var v in All(opt))
                    Add(NameOfValue(v, readFake));

            if (customTls.Contains("!")) Add(BuiltinTls);                       // '!' — встроенный фейк winws
            if (All("--dpi-desync-fake-http").Contains("!")) Add(BuiltinHttp);

            // Модификации TLS-фейка: последняя заданная заменяет умолчание; без неё при своём фейке — none, без своего — rnd,rndsni,dupsid.
            var mods = All("--dpi-desync-fake-tls-mod").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToList();
            bool rndsni = mods.Count > 0 ? mods.Contains("rndsni") : customTls.Count == 0;
            foreach (var m in mods.Where(m => m.StartsWith("sni=", StringComparison.Ordinal))) Add(m[4..]);

            // TLS и HTTP — только у TCP-ролей; у UDP-ролей (QUIC, игры, голос) фейки другие.
            bool tcpRole = role is not ("quic" or "game_udp" or "discord_udp" or "ipset_udp");
            if (plainFake && tcpRole)
            {
                if (rndsni) { reliable = false; notes.Add("TLS-приманка со случайным именем"); }
                else if (customTls.Count == 0 && !mods.Any(m => m.StartsWith("sni=", StringComparison.Ordinal))) Add(BuiltinTls);
                if (All("--dpi-desync-fake-http").Count == 0) Add(BuiltinHttp);
            }

            // QUIC: роутер расшифровывает Initial и берёт имя из него; наши файлы называются quic_initial_<имя с «_» вместо «.»>.bin.
            var quic = All("--dpi-desync-fake-quic");
            foreach (var v in quic) Add(NameOfValue(v, readFake));
            if (role == "quic" && plainFake && quic.Count == 0)
            {
                reliable = false; notes.Add("QUIC-приманка без файла");
            }

            // hostfakesplit: фейк хоста — host=<имя> (слева добавляется случайный поддомен: поддомены исключения покрывает), иначе имя случайное.
            if (modes.Contains("hostfakesplit"))
            {
                var host = All("--dpi-desync-hostfakesplit-mod").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    .Where(m => m.StartsWith("host=", StringComparison.Ordinal)).Select(m => m[5..]).ToList();
                if (host.Count == 0) { reliable = false; notes.Add("hostfakesplit со случайным именем"); }
                foreach (var h in host) Add(h);
            }
        }

        if (!reliable)
        {
            foreach (var n in supplyNames?.Invoke() ?? [BuiltinTls, BuiltinHttp]) Add(n);
            notes.Add("список — все имена фейков из поставки");
        }
        return new(names, reliable, notes.Distinct().ToList());
    }

    /// <summary>Имя из значения параметра: <c>{fake}/файл.bin</c>, <c>0xHEX</c> или <c>!</c> (встроенный фейк — имя дальше решает вызывающий).</summary>
    static string? NameOfValue(string value, Func<string, byte[]?> readFake)
    {
        if (value.StartsWith("{fake}/", StringComparison.Ordinal))
        {
            var file = value["{fake}/".Length..];
            if (QuicNameOfFile(file) is { } q) return q;
            return readFake(file) is { } bytes ? NameOfBytes(bytes) ?? NameOfFileName(file) : NameOfFileName(file);
        }
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            try { return NameOfBytes(Convert.FromHexString(value[2..])); }
            catch (FormatException) { return null; }
        }
        return null;
    }

    /// <summary>SNI из TLS ClientHello или Host из HTTP-запроса; иное содержимое (STUN, нули) — null.</summary>
    public static string? NameOfBytes(byte[] d)
    {
        if (d.Length > 5 && d[0] == 0x16) return SniOf(d);
        if (d.Length > 16)
        {
            var text = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 2048));
            if (HostRx().Match(text) is { Success: true } m) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }

    [GeneratedRegex(@"(?im)^Host:[ \t]*([A-Za-z0-9.\-]{3,253})[ \t]*\r?$")]
    private static partial Regex HostRx();

    /// <summary>SNI из TLS ClientHello (запись 0x16, рукопожатие 1, расширение server_name). Любое отступление от формата — null.</summary>
    public static string? SniOf(byte[] d)
    {
        int p = 0;
        bool Need(int n) => p + n <= d.Length;
        int U16() { int v = d[p] << 8 | d[p + 1]; p += 2; return v; }
        if (!Need(9) || d[0] != 0x16 || d[5] != 0x01) return null;
        p = 5 + 1 + 3 + 2 + 32;                       // тип записи, версия, длина → тип рукопожатия, длина, версия, random
        if (!Need(1)) return null;
        p += 1 + d[p];                                 // session id
        if (!Need(2)) return null;
        p += 2 + (d[p] << 8 | d[p + 1]);              // шифры
        if (!Need(1)) return null;
        p += 1 + d[p];                                 // сжатие
        if (!Need(2)) return null;
        int extEnd = Math.Min(d.Length, p + 2 + (d[p] << 8 | d[p + 1]));
        p += 2;
        while (p + 4 <= extEnd)
        {
            int type = U16(), len = U16();
            if (p + len > extEnd) return null;
            if (type == 0 && len >= 5)
            {
                int q = p + 2;                         // список имён: длина списка
                if (d[q] != 0) return null;            // тип имени: host_name
                int nl = d[q + 1] << 8 | d[q + 2];
                if (q + 3 + nl > p + len || nl == 0) return null;
                var s = Encoding.ASCII.GetString(d, q + 3, nl).ToLowerInvariant();
                return AdvancedSites.IsDomain(s) ? s : null;
            }
            p += len;
        }
        return null;
    }

    /// <summary>quic_initial_www_google_com.bin → www.google.com; хвосты _kyber_N и _quiche — варианты того же файла. Не QUIC-файл — null.</summary>
    public static string? QuicNameOfFile(string file)
    {
        var m = QuicFileRx().Match(file);
        return m.Success ? m.Groups[1].Value.Replace('_', '.').ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^quic_initial_(.+?)(?:_kyber_\d+|_quiche)?\.bin\z", RegexOptions.IgnoreCase)]
    private static partial Regex QuicFileRx();

    /// <summary>tls_clienthello_4pda_to.bin → 4pda.to (запасной путь, когда файла нет под рукой). Неоднозначное имя — null.</summary>
    static string? NameOfFileName(string file)
    {
        var m = TlsFileRx().Match(file);
        if (!m.Success) return null;
        var n = m.Groups[1].Value.Replace('_', '.').ToLowerInvariant();
        return AdvancedSites.IsDomain(n) ? n : null;
    }

    [GeneratedRegex(@"^tls_clienthello_(.+)\.bin\z", RegexOptions.IgnoreCase)]
    private static partial Regex TlsFileRx();

    /// <summary>Все имена фейков из папки поставки плюс встроенные — консервативный список, когда точные имена получить нельзя.</summary>
    public static IReadOnlyList<string> SupplyNames(string fakeDir)
    {
        var names = new List<string> { BuiltinTls, BuiltinHttp };
        void Add(string? n) { if (n is not null && AdvancedSites.IsDomain(n) && !names.Contains(n)) names.Add(n); }
        try
        {
            if (Directory.Exists(fakeDir))
                foreach (var f in Directory.GetFiles(fakeDir, "*.bin").OrderBy(f => f, StringComparer.Ordinal))
                {
                    var file = Path.GetFileName(f);
                    Add(QuicNameOfFile(file));
                    try { Add(NameOfBytes(File.ReadAllBytes(f))); } catch (IOException) { }
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return names;
    }
}
