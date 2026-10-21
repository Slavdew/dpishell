using DpiShell.Core.Localization;
namespace DpiShell.Core.Helper;

public enum HelperCompatLevel
{
    /// <summary>Версии протокола совпадают.</summary>
    Ok,
    /// <summary>Минор другой: работать можно, команды не из <see cref="HelperCompat.Commands"/> — неактивны, предупреждение.</summary>
    MinorDiffers,
    /// <summary>Мажор другой (или версия не читается): команды запрещены, доступна только переустановка помощника.</summary>
    Incompatible,
}

/// <summary>
/// Совместимость окна и помощника (helper-protocol §6): версия протокола «мажор.минор» и список команд из status
/// (источник — белый список помощника), плюс версия программы помощника. Правила — как у договора с роутером (router-api §5.4):
/// мажор другой — команды запрещены, кроме переустановки (<c>--install-helper</c>, один UAC); минор другой — предупреждение,
/// неизвестных помощнику команд кнопки не шлют; программа помощника старее окна — «Обновить помощника» (тот же путь).
/// </summary>
/// <param name="HelperApi">Версия протокола помощника («1.0», если он её не присылает).</param>
/// <param name="Commands">Команды, которые помощник принимает; null — неизвестно (помощник до 1.1) — не ограничиваем.</param>
/// <param name="HelperOutdated">Программа помощника старее окна — предложить «Обновить помощника».</param>
/// <param name="Message">Что показать (null — всё в порядке).</param>
public sealed record HelperCompat(HelperCompatLevel Level, string HelperApi, IReadOnlySet<string>? Commands, bool HelperOutdated, string? Message)
{
    /// <summary>Помощник без поля <c>api</c> (до 1.1) — протокол 1.0.</summary>
    public const string LegacyApi = "1.0";

    /// <summary>Нужна переустановка или обновление помощника: несовместим или старее окна.</summary>
    public bool NeedsReinstall => Level == HelperCompatLevel.Incompatible || HelperOutdated;

    /// <summary>
    /// Можно ли отправить команду. Несовместимый помощник — только status (по нему окно узнаёт состояние и предлагает переустановку).
    /// </summary>
    public bool Allows(string cmd) => Level != HelperCompatLevel.Incompatible || cmd == "status";

    /// <summary>Помощник знает команду (для активности кнопок): список неизвестен — считаем, что знает.</summary>
    public bool Supports(string cmd) => Allows(cmd) && (Commands is null || Commands.Contains(cmd));

    /// <summary>Сравнить с этим окном. Параметры окна по умолчанию — свои (<see cref="HelperProtocol.Api"/>, версия сборки).</summary>
    public static HelperCompat Evaluate(string? helperApi, IReadOnlyList<string>? commands, string? helperVersion,
        string windowApi = HelperProtocol.Api, string? windowVersion = null)
    {
        windowVersion ??= HelperProtocol.AppVersion;
        var api = string.IsNullOrWhiteSpace(helperApi) ? LegacyApi : helperApi.Trim();
        var set = commands is null ? null : new HashSet<string>(commands, StringComparer.Ordinal);
        bool outdated = !string.IsNullOrEmpty(helperVersion) && AppRelease.IsOlder(helperVersion, windowVersion);
        if (Parse(api) is not { } h || Parse(windowApi) is not { } w || h.Major != w.Major)
            return new(HelperCompatLevel.Incompatible, api, set, outdated,
                L.T("core.helper.compat.incompatible"));
        if (h.Minor != w.Minor)
            return new(HelperCompatLevel.MinorDiffers, api, set, outdated,
                outdated || h.Minor < w.Minor
                    ? L.T("core.helper.compat.older")
                    : L.T("core.helper.compat.newer"));
        return new(HelperCompatLevel.Ok, api, set, outdated, outdated ? L.T("core.helper.compat.new_version") : null);
    }

    /// <summary>Версия протокола <paramref name="api"/> не ниже <paramref name="min"/> (оба «мажор.минор»); нечитаемая — нет.</summary>
    public static bool AtLeast(string? api, string min)
    {
        if (Parse(string.IsNullOrWhiteSpace(api) ? LegacyApi : api.Trim()) is not { } a || Parse(min) is not { } m) return false;
        return a.Major > m.Major || (a.Major == m.Major && a.Minor >= m.Minor);
    }

    static (int Major, int Minor)? Parse(string s)
    {
        var p = s.Split('.');
        return p.Length == 2 && int.TryParse(p[0], out var a) && int.TryParse(p[1], out var b) && a >= 0 && b >= 0 ? (a, b) : null;
    }
}
