using System.Text.Json;
using DpiShell.Core.Localization;

namespace DpiShell.Core.Router;

/// <summary>
/// Фраза роутера «ключом словаря» (router-api.md §9, 2026-10-20): рядом с готовым русским <c>text</c> роутер присылает <c>key</c> и сырые
/// <c>params</c> — время (<c>time</c>, <c>*_at</c>) в unix-секундах, числа числами, имена как есть, вложенная фраза — <c>{ key, params, text }</c>.
/// Окно берёт <c>L.T(key, params)</c>, если знает ключ, иначе <c>text</c>. Где: <c>warnings[]</c>, <c>error</c>, <c>select_progress.message_msg</c>.
/// </summary>
public static class RouterPhrase
{
    /// <summary>Фраза на языке окна: по ключу, если он известен; иначе null (остаётся текст роутера).</summary>
    public static string? ByKey(string? key, IReadOnlyDictionary<string, JsonElement>? parameters) =>
        key is { Length: > 0 } && L.Has(key) ? L.T(key, Args(parameters)) : null;

    /// <summary>Объект-фраза <c>{ key, params, text }</c> → текст на языке окна; ключ неизвестен — его <c>text</c>, иначе <paramref name="fallback"/>.</summary>
    public static string? FromObject(JsonElement? phrase, string? fallback)
    {
        if (phrase is not { ValueKind: JsonValueKind.Object } p) return fallback;
        var key = p.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (ByKey(key, Params(p)) is { } t) return t;
        return p.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString() : fallback;
    }

    static Dictionary<string, JsonElement>? Params(JsonElement phrase) =>
        phrase.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object ? ps.EnumerateObject().ToDictionary(x => x.Name, x => x.Value) : null;

    /// <summary>Подстановки к ключу: строки как есть, числа как есть, время (time, *_at) — «ЧЧ:ММ» по часам ПК, вложенные фразы — готовым текстом.</summary>
    public static Dictionary<string, object?> Args(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        var args = new Dictionary<string, object?>();
        foreach (var (name, v) in parameters ?? new Dictionary<string, JsonElement>())
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                args[name] = IsTime(name) ? L.Time(DateTimeOffset.FromUnixTimeSeconds(n).LocalDateTime) : n;
            else if (v.ValueKind == JsonValueKind.Object) args[name] = FromObject(v, null) ?? "";
            else args[name] = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        }
        return args;
    }

    static bool IsTime(string name) => name is "time" or "at" || name.EndsWith("_at", StringComparison.Ordinal);

    /// <summary>Время из params (unix-секунды, UTC); нет — null.</summary>
    public static DateTime? Time(IReadOnlyDictionary<string, JsonElement>? parameters)
    {
        if (parameters is null) return null;
        foreach (var n in new[] { "time", "at" })
            if (parameters.TryGetValue(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s))
                return DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
        return null;
    }
}
