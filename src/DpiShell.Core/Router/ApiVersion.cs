using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DpiShell.Core.Router;

/// <summary>
/// Версия договора с роутером: «мажор.минор». Мажор растёт только при несовместимых изменениях, минор — при добавлениях.
/// Роутеры до 0.2.0 отдают число (<c>"api": 1</c> = «1.0»), новые — строку (<c>"api": "1.0"</c>); принимаем оба вида.
/// </summary>
[JsonConverter(typeof(ApiVersionJsonConverter))]
public readonly record struct ApiVersion(int Major, int Minor) : IComparable<ApiVersion>
{
    public static bool TryParse(string? s, out ApiVersion v)
    {
        v = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        var parts = s.Trim().Split('.');
        if (parts.Length is < 1 or > 2) return false;
        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)) return false;
        var minor = 0;
        if (parts.Length == 2 && !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out minor)) return false;
        v = new(major, minor);
        return true;
    }

    public int CompareTo(ApiVersion o) => Major != o.Major ? Major.CompareTo(o.Major) : Minor.CompareTo(o.Minor);

    public override string ToString() => $"{Major}.{Minor}";
}

public sealed class ApiVersionJsonConverter : JsonConverter<ApiVersion>
{
    public override ApiVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetInt32(out var n) && n >= 0 => new ApiVersion(n, 0),
        JsonTokenType.String when ApiVersion.TryParse(reader.GetString(), out var v) => v,
        _ => throw new JsonException("api: ожидается число или строка «мажор.минор»"),
    };

    public override void Write(Utf8JsonWriter writer, ApiVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
}
