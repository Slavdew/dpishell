using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiShell.Core.Router;

// Ответы сценариев по SSH (router-api.md §5.5) и решения над ними — чистые функции, без сети и SSH.

public sealed record PreflightOpenWrt(string? Release, string? Revision, string? Arch, string? Target);
public sealed record PreflightKmod(bool Installed, bool Available, string? Version, bool MatchesKernel);
/// <summary>Доступность с роутера: feed — витрина DpiShell, fw_feed — фид прошивки, github — github.com.</summary>
public sealed record PreflightNet(bool Feed, bool FwFeed, bool Github);
public sealed record PreflightSpace(long? OverlayFreeKb, long? TmpFreeKb);
/// <summary>kind: package | service | process.</summary>
public sealed record PreflightForeign(string Kind, string Name);
public sealed record PreflightDpiShell(string? Version, ApiVersion? Api);

/// <summary><c>dpishell-preflight.sh</c>, режим check (schema 1).</summary>
public sealed record RouterPreflight(
    int Schema, string Mode, PreflightOpenWrt? Openwrt, string? Pkg, bool Fw4, bool Nft, string? Kernel,
    PreflightKmod? KmodNftQueue, PreflightNet? Net, PreflightSpace? Space, List<PreflightForeign>? Foreign,
    PreflightDpiShell? Dpishell, bool Luci);

public sealed record AfterUninstallLeft(string What, string? Detail);
public sealed record AfterUninstallKept(bool Settings, bool HttpsDnsProxy);
/// <summary><c>dpishell-preflight.sh --after-uninstall</c>: left пуст — ничего не осталось.</summary>
public sealed record RouterAfterUninstall(int Schema, string Mode, List<AfterUninstallLeft> Left, AfterUninstallKept? Kept)
{
    public bool Clean => Left.Count == 0;
}

/// <summary>Итог <c>dpishell-install.sh</c> — последняя строка: { schema, ok, step, message, version, api, core }.</summary>
public sealed record RouterInstallResult(int Schema, bool Ok, string Step, string Message, string? Version, ApiVersion? Api, bool? Core);

/// <summary><c>/tmp/dpishell-uninstall.json</c> — ход и итог удаления: { running, ok, keep_settings, message, at }.</summary>
public sealed record RouterUninstallResult(bool Running, bool? Ok, bool KeepSettings, string? Message, long? At);

public static class RouterScriptJson
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Последняя непустая строка вывода, начинающаяся с «{» — итог сценария (строки «# …» перед ним — ход).</summary>
    public static string? LastJsonLine(string stdout) =>
        stdout.Replace("\r", "").Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{'));

    public static T Parse<T>(string stdout, string what)
    {
        var line = LastJsonLine(stdout) ?? throw new RouterSshException(RouterSshFailure.Protocol, $"{what}: роутер не вернул итог");
        try { return JsonSerializer.Deserialize<T>(line, Json) ?? throw new JsonException("пусто"); }
        catch (JsonException e) { throw new RouterSshException(RouterSshFailure.Protocol, $"{what}: ответ роутера не по договору ({e.Message})", e); }
    }

    public static RouterPreflight ParsePreflight(string stdout)
    {
        var p = Parse<RouterPreflight>(stdout, "Проверка роутера");
        if (p.Schema != 1 || p.Mode != "check") throw new RouterSshException(RouterSshFailure.Protocol, $"Проверка роутера: неизвестный формат (schema {p.Schema}, {p.Mode})");
        return p;
    }

    public static RouterAfterUninstall ParseAfterUninstall(string stdout)
    {
        var p = Parse<RouterAfterUninstall>(stdout, "Проверка после удаления");
        if (p.Schema != 1 || p.Mode != "after-uninstall") throw new RouterSshException(RouterSshFailure.Protocol, "Проверка после удаления: неизвестный формат");
        return p with { Left = p.Left ?? [] };
    }

    public static RouterInstallResult ParseInstall(string stdout) => Parse<RouterInstallResult>(stdout, "Установка");

    /// <summary>Строка хода «# Ключ витрины DpiShell» → «Ключ витрины DpiShell»; не строка хода — null.</summary>
    public static string? ProgressLine(string line) => line.StartsWith("# ", StringComparison.Ordinal) ? line[2..].Trim() : null;
}

// ---------- выпуски для роутера (data/router-releases.json) ----------

public sealed record RouterFeedInfo(string Url, string KeyUrl, string KeySha256);
public sealed record RouterReleaseOpenWrt(string Min, bool Snapshot);
public sealed record RouterRelease(string Version, string Tag, string Api, string MinApp, string Pkg, RouterReleaseOpenWrt Openwrt, List<string> Packages, string ReleaseJson);
public sealed record RouterReleases(int Schema, RouterFeedInfo Feed, List<RouterRelease> Releases)
{
    public static RouterReleases Parse(string json) =>
        JsonSerializer.Deserialize<RouterReleases>(json, RouterScriptJson.Json) ?? throw new InvalidDataException("Пустой router-releases.json");

    public static RouterReleases Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>
    /// Выпуск под роутер: тот же пакетный менеджер, версия OpenWrt не ниже min; основной snapshot («SNAPSHOT») — только где snapshot: true,
    /// ветка «25.12-SNAPSHOT» — как версия 25.12. Из подходящих — самый новый. null — подходящего нет.
    /// </summary>
    public RouterRelease? Choose(string? pkg, string? openwrtRelease)
    {
        if (pkg is null || openwrtRelease is null) return null;
        var mainSnapshot = string.Equals(openwrtRelease.Trim(), "SNAPSHOT", StringComparison.OrdinalIgnoreCase);
        var ver = mainSnapshot ? null : OpenWrtVersion.Parse(openwrtRelease);
        // Сборки на основе OpenWrt пишут в DISTRIB_RELEASE свою версию («1.4.8»), а не версию OpenWrt (год.месяц, 18.06 и новее) —
        // найдено на живом роутере 2026-10-17. Тогда версию основы не знаем и выбираем по пакетному менеджеру: apk — только с OpenWrt 25.
        var ownVersion = !mainSnapshot && (ver is null || ver.Major < 18);
        return Releases
            .Where(r => r.Pkg == pkg)
            .Where(r => ownVersion ? true
                      : mainSnapshot ? r.Openwrt.Snapshot
                      : OpenWrtVersion.Parse(r.Openwrt.Min) is { } min && ver!.CompareTo(min) >= 0)
            .OrderByDescending(r => OpenWrtVersion.Parse(r.Version) ?? new Version(0, 0))
            .FirstOrDefault();
    }
}

public static class OpenWrtVersion
{
    /// <summary>«25.12.0», «25.12.0-rc1», «25.12-SNAPSHOT», «0.2.0» → числовая часть; не число — null.</summary>
    public static Version? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var head = new string(s.Trim().TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray()).TrimEnd('.');
        var parts = head.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Length > 4) return null;
        var n = new int[Math.Max(parts.Length, 2)];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i])) return null;
        return parts.Length switch { 1 or 2 => new Version(n[0], n[1]), 3 => new Version(n[0], n[1], n[2]), _ => new Version(n[0], n[1], n[2], n[3]) };
    }
}

// ---------- годится ли роутер ----------

/// <summary>Причина отказа или предупреждение: code — для логики окна, Text — готовая фраза.</summary>
public sealed record RouterIssue(string Code, string Text);

/// <summary>
/// Итог предпроверки. CanInstall — можно ставить (возможно, через туннель: NeedsTunnel).
/// KmodPending — модуль не проверить без сети к фиду прошивки: решится повторной проверкой через туннель.
/// </summary>
public sealed record RouterVerdict(
    bool CanInstall, IReadOnlyList<RouterIssue> Blockers, IReadOnlyList<RouterIssue> Warnings,
    bool NeedsTunnel, bool KmodPending, RouterRelease? Release, string? InstalledVersion)
{
    public bool AlreadyInstalled => InstalledVersion is not null;
}

public static class RouterSuitability
{
    /// <summary>Место под пакеты (DpiShell, curl, unzip, ucode, kmod) и ядро в /opt/zapret.</summary>
    public const long MinOverlayKb = 3072;
    /// <summary>/tmp (память): архив ядра 8 МБ и распакованная часть.</summary>
    public const long MinTmpKb = 16384;

    /// <summary>Архитектура nfqws по DISTRIB_ARCH — как <c>arch()</c> в core.uc; null — не поддерживается.</summary>
    public static string? CoreArch(string? distribArch)
    {
        if (string.IsNullOrEmpty(distribArch)) return null;
        var a = distribArch;
        if (a.StartsWith("aarch64")) return "arm64";
        if (a.StartsWith("arm")) return "arm";
        if (a.StartsWith("x86_64")) return "x86_64";
        if (a.StartsWith("i386") || a.StartsWith("i686") || a.StartsWith("x86")) return "x86";
        if (a.StartsWith("mipsel") || a.StartsWith("mips64el")) return a.StartsWith("mips64") ? "mips64" : "mipsel";
        if (a.StartsWith("mips64")) return "mips64";
        if (a.StartsWith("mips")) return "mips";
        return null;
    }

    public static RouterVerdict Evaluate(RouterPreflight p, RouterReleases releases, IReadOnlyCollection<string>? coreArches = null)
    {
        var stop = new List<RouterIssue>();
        var warn = new List<RouterIssue>();
        var release = p.Openwrt?.Release;

        if (p.Openwrt is null || string.IsNullOrEmpty(release))
            stop.Add(new("not_openwrt", "Это не OpenWrt: DpiShell ставится только на роутер с прошивкой OpenWrt"));

        bool pkgOk = false;
        switch (p.Pkg)
        {
            case "apk": pkgOk = true; break;
            case "opkg":
                stop.Add(new("opkg", $"OpenWrt {release} с opkg пока не поддерживается — пакет для opkg (OpenWrt 24.10) готовится, ждите обновления. Или обновите роутер до OpenWrt 25.12"));
                break;
            default:
                if (p.Openwrt is not null) stop.Add(new("no_pkg", "На роутере не найден пакетный менеджер (apk) — прошивка урезана, установить пакеты нельзя"));
                break;
        }

        if (!p.Fw4 || !p.Nft)
            stop.Add(new("no_fw4", "Нужен межсетевой экран fw4 (nftables), а на роутере его нет — старая прошивка с iptables или сборка без fw4"));

        if (p.Openwrt?.Arch is { } arch && (CoreArch(arch) is not { } ca || coreArches is not null && !coreArches.Contains(ca)))
            stop.Add(new("arch", $"Процессор роутера ({arch}) не поддерживается ядром обхода"));

        // Сеть: без витрины, GitHub или фида прошивки — ставим через ПК (туннель).
        var net = p.Net ?? new PreflightNet(false, false, false);
        bool needsTunnel = !net.Feed || !net.Github || !net.FwFeed;

        // Модуль ядра: в ядре, установлен или есть в фиде — и под это ядро.
        bool kmodPending = false;
        var k = p.KmodNftQueue;
        if (k is null || !k.MatchesKernel)
        {
            bool unknown = k is null || !k.Installed && !k.Available;
            if (unknown && !net.FwFeed)
                kmodPending = true;   // индекс фида не получить — проверим через туннель (--update)
            else if (unknown)
                stop.Add(new("kmod_missing", "В фиде прошивки нет модуля kmod-nft-queue — без него обход не работает. Обычно его нет в урезанных сборках: поставьте официальную OpenWrt"));
            else
                stop.Add(new("kmod_mismatch",
                    $"Модуль kmod-nft-queue ({k!.Version}) собран не под ядро роутера ({p.Kernel}) — так бывает со snapshot, который обновился после прошивки. Обновите прошивку до свежей сборки и повторите"));
        }

        if (p.Space?.OverlayFreeKb is { } ov)
        {
            if (ov < MinOverlayKb) stop.Add(new("space_overlay", $"Мало места на роутере: свободно {ov / 1024.0:0.#} МБ, нужно около {MinOverlayKb / 1024} МБ"));
        }
        else warn.Add(new("space_unknown", "Не удалось узнать свободное место на роутере"));
        if (p.Space?.TmpFreeKb is { } tmp && tmp < MinTmpKb)
            stop.Add(new("space_tmp", $"Мало временной памяти на роутере: свободно {tmp / 1024.0:0.#} МБ, нужно около {MinTmpKb / 1024} МБ."));

        foreach (var f in p.Foreign ?? [])
        {
            if (f.Kind == "package")
                stop.Add(new("foreign_package", $"Удалите пакет «{f.Name}» (другой обход блокировок): в меню роутера «Система» → «Программное обеспечение», затем повторите."));
            else
                warn.Add(new("foreign_service", $"На роутере работает другой обход ({f.Name}) — после установки DpiShell предложит его остановить"));
        }

        RouterRelease? chosen = null;
        if (pkgOk && !string.IsNullOrEmpty(release))
        {
            chosen = releases.Choose(p.Pkg, release);
            if (chosen is null)
                stop.Add(new("no_release", $"Для OpenWrt {release} выпуска DpiShell пока нет — нужна OpenWrt {MinSupported(releases, p.Pkg)} или новее"));
        }

        if (p.Dpishell is { Version: { } v })
            warn.Add(new("installed", $"DpiShell {v} уже установлен — установка обновит пакеты"));
        if (needsTunnel)
            warn.Add(new("tunnel", "Роутер не достаёт до GitHub или фида пакетов — установка пойдёт через этот компьютер"));

        return new RouterVerdict(stop.Count == 0, stop, warn, needsTunnel, kmodPending, chosen, p.Dpishell?.Version);
    }

    static string MinSupported(RouterReleases r, string? pkg) =>
        r.Releases.Where(x => x.Pkg == pkg).Select(x => x.Openwrt.Min).OrderBy(m => OpenWrtVersion.Parse(m) ?? new Version(99, 0)).FirstOrDefault() ?? "25.12";
}
