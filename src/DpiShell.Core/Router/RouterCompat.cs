namespace DpiShell.Core.Router;

public enum RouterCompatLevel
{
    /// <summary>Всё совместимо.</summary>
    Ok,
    /// <summary>Другой минор: работает, но часть кнопок неактивна (методов нет в status.methods) — Text объясняет.</summary>
    Warning,
    /// <summary>Другой мажор или min_app новее приложения: доступно только ядро договора — состояние, обновление, удаление.</summary>
    Blocked,
}

/// <summary>
/// Совместимость окна и роутера (router-api.md §5.4) — чистая модель над status (api, methods, min_app) и версией приложения.
/// Ядро договора (status, update_install, uninstall) доступно всегда: по нему пользователь выбирается из любой несовместимости.
/// </summary>
public sealed record RouterCompatibility(RouterCompatLevel Level, string? Text, IReadOnlySet<string>? Methods)
{
    public static readonly IReadOnlySet<string> CoreMethods = new HashSet<string>(["status", "update_install", "uninstall"], StringComparer.Ordinal);

    /// <summary>
    /// Можно ли звать метод: ядро — всегда; при Blocked — больше ничего; иначе — если он в status.methods
    /// (поля нет — роутер до 0.2.0, считаем, что умеет всё из API 1.0; не умеет — ответит кодом 4).
    /// </summary>
    public bool CanCall(string method) =>
        CoreMethods.Contains(method) || Level != RouterCompatLevel.Blocked && (Methods is null || Methods.Contains(method));

    public static RouterCompatibility FromStatus(RouterStatus s, Version app, ApiVersion? supported = null) =>
        Evaluate(s.Api, s.Methods, s.MinApp, app, supported);

    public static RouterCompatibility Evaluate(ApiVersion? api, IEnumerable<string>? methods, string? minApp, Version app, ApiVersion? supported = null)
    {
        var sup = supported ?? RouterClient.SupportedApi;
        var set = methods is null ? null : new HashSet<string>(methods, StringComparer.Ordinal);

        if (api is not { } a)
            return new(RouterCompatLevel.Blocked, "DpiShell на роутере слишком старый — обновите его на роутере", set);
        if (a.Major > sup.Major)
            return new(RouterCompatLevel.Blocked, "DpiShell на роутере новее программы — обновите программу. Пока работают только состояние, обновление и удаление", set);
        if (a.Major < sup.Major)
            return new(RouterCompatLevel.Blocked, "DpiShell на роутере устарел — обновите его. Пока доступны состояние, обновление и удаление", set);
        if (ParseVersion(minApp) is { } need && Normalize(app) < need)
            return new(RouterCompatLevel.Blocked, $"DpiShell на роутере нужна программа {minApp} или новее. Пока работают только состояние, обновление и удаление.", set);
        if (a.Minor > sup.Minor)
            return new(RouterCompatLevel.Warning, "DpiShell на роутере новее программы — обновите программу, чтобы получить новые возможности.", set);
        if (a.Minor < sup.Minor)
            return new(RouterCompatLevel.Warning, "DpiShell на роутере старше программы —часть кнопок неактивна, обновите DpiShell на роутере", set);
        return new(RouterCompatLevel.Ok, null, set);
    }

    /// <summary>«1.2.0», «1.2», «1.2.0-beta» → 1.2.0; не версия — null.</summary>
    public static Version? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var head = s.Trim().Split('-', '+')[0];
        return Version.TryParse(head.Contains('.') ? head : head + ".0", out var v) ? Normalize(v) : null;
    }

    static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));
}
