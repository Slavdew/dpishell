using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Списки доменов и фейк-пакеты в %ProgramData%. Стандартные обновляются из поставки, пользовательские не трогаются.</summary>
public sealed partial class DataStore(AppPaths paths)
{
    public const string UserGeneral = "user-general.txt";
    public const string UserExclude = "user-exclude.txt";

    // winws с пустым hostlist ведёт себя не как «ничего»: кладём заведомо несуществующий домен, как это делает и Flowseal.
    const string Placeholder = "domain.example.abc";

    public void Seed()
    {
        paths.EnsureDirectories();
        // Общий IP-список ставит IpLists: с проверкой, не затирая скачанный более свежий.
        CopyDir(Path.Combine(paths.Seed, "lists"), paths.Lists, skip: IpLists.AllFile);
        CopyDir(Path.Combine(paths.Seed, "fake"), paths.Fake);
        foreach (var f in new[] { UserGeneral, UserExclude })
        {
            var p = Path.Combine(paths.Lists, f);
            if (!File.Exists(p)) File.WriteAllText(p, Placeholder + "\n");
        }
        var ip = new IpLists(paths);
        ip.EnsureFiles();
        ip.SeedFromPackage();
    }

    static void CopyDir(string from, string to, string? skip = null)
    {
        if (!Directory.Exists(from)) throw new DirectoryNotFoundException($"Нет данных поставки: {from}");
        foreach (var f in Directory.GetFiles(from))
            if (!string.Equals(Path.GetFileName(f), skip, StringComparison.OrdinalIgnoreCase))
                File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }

    public IReadOnlyList<string> ReadUserDomains(string file = UserGeneral) =>
        File.Exists(Path.Combine(paths.Lists, file))
            ? File.ReadAllLines(Path.Combine(paths.Lists, file)).Select(l => l.Trim())
                .Where(l => l.Length > 0 && l != Placeholder && !l.StartsWith('#')).ToList()
            : [];

    /// <summary>Записать пользовательский список: только корректные домены, остальное отбрасывается и возвращается.</summary>
    public IReadOnlyList<string> WriteUserDomains(IEnumerable<string> lines, string file = UserGeneral)
    {
        var ok = new List<string>(); var rejected = new List<string>();
        foreach (var raw in lines)
        {
            var d = NormalizeDomain(raw);
            if (d is null) { if (!string.IsNullOrWhiteSpace(raw)) rejected.Add(raw.Trim()); continue; }
            if (!ok.Contains(d)) ok.Add(d);
        }
        File.WriteAllLines(Path.Combine(paths.Lists, file), ok.Count > 0 ? ok : [Placeholder]);
        return rejected;
    }

    /// <summary>«https://Site.com/path» → «site.com». null — если это не домен.</summary>
    public static string? NormalizeDomain(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.Length == 0 || s.StartsWith('#')) return null;
        if (Uri.TryCreate(s.Contains("://") ? s : "https://" + s, UriKind.Absolute, out var u)) s = u.IdnHost;
        s = s.TrimEnd('.');
        if (s.StartsWith("www.")) s = s[4..];
        return DomainRx().IsMatch(s) ? s : null;
    }

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+(xn--[a-z0-9-]{2,59}|[a-z]{2,63})$")]
    private static partial Regex DomainRx();
}
