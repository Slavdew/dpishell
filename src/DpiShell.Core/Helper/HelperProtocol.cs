using DpiShell.Core.Localization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace DpiShell.Core.Helper;

/// <summary>
/// Протокол канала окно ↔ помощник (docs/dev/helper-protocol.md §3). Одна строка UTF-8 = один JSON-объект, конец строки — '\n'.
/// <list type="bullet">
/// <item>Запрос: <c>{"id":1,"cmd":"enable","args":{"strategy":"alt9"}}</c>; отмена: <c>{"id":2,"cmd":"cancel","target":1}</c>.</item>
/// <item>Ответ: <c>{"id":1,"ok":true,"result":…}</c> или <c>{"id":1,"ok":false,"error":{"code":"invalid","message":"…"}}</c>.</item>
/// <item>Ход долгой операции: <c>{"id":1,"progress":{…}}</c> — сколько угодно раз до финального ответа.</item>
/// <item>После подключения помощник первым шлёт <c>{"hello":{"protocol":1,"api":"1.5","version":"…"}}</c> или отказ <c>denied</c> с id 0.</item>
/// </list>
/// </summary>
public static class HelperProtocol
{
    /// <summary>Имя канала службы: <c>\\.\pipe\DpiShell.Helper</c>.</summary>
    public const string PipeName = "DpiShell.Helper";
    public const string ServiceName = "DpiShellHelper";
    public const int Version = 1;

    /// <summary>
    /// Версия протокола «мажор.минор» (helper-protocol §6): минор — добавления без поломок (команды, поля), мажор — несовместимые
    /// изменения. В hello и status; помощник без поля — «1.0». Сравнение — <see cref="HelperCompat"/>.
    /// </summary>
    public const string Api = "1.22";

    /// <summary>С этой версии: профили программ включаются по отдельности и выбирают стратегию — <c>app_profile_options</c>, <c>app_profiles_separate</c>; у <c>app_profile_set</c> — необязательные <c>tcpLen</c>, <c>tcpHex</c>, <c>udpLen</c>, <c>udpHex</c> (сигнатура пакета), <c>tcpFilter</c>, <c>udpFilter</c>, <c>tcpDesync</c>, <c>udpDesync</c> (свои параметры) и необязательные <c>addresses</c> для протокола с сигнатурой; в <c>AppProfileInfo</c> — те же поля и выбор. Старый помощник лишних полей и команд не принимает.</summary>
    public const string ProfilesApi = "1.22";

    /// <summary>С этой версии <c>select</c>, <c>search</c> и <c>strategy_check</c> принимают необязательное <c>ownOnly</c> — подбор только своих сайтов рядом с роутером (старый помощник лишних полей не принимает).</summary>
    public const string OwnOnlyApi = "1.21";

    /// <summary>С этой версии протокола запрос может нести необязательное поле <c>lang</c> — язык окна для ответа (старый помощник отвергает лишние поля).</summary>
    public const string LangApi = "1.13";

    /// <summary>С этой версии: группы своих сайтов — команда <c>user_groups_set</c>, поле <c>userGroups</c> в status и <c>siteGroups</c> в плане загрузки настроек (старый помощник лишних полей не принимает).</summary>
    public const string GroupsApi = "1.18";

    /// <summary>
    /// С этой версии hello несёт <c>machine</c>: принимает ли помощник от этого подключения действия на весь компьютер (hosts, DNS, прокси машины).
    /// false — клиент не та же сборка DpiShell, что помощник (helper-protocol §5, «Действия на весь компьютер»). Нет поля — проверки нет (помощник до 1.14).
    /// </summary>
    public const string MachineApi = "1.14";

    /// <summary>Предел строки: IP-список (до 4 МБ) с запасом на экранирование JSON. Длиннее — соединение рвётся.</summary>
    public const int MaxLineBytes = 12 * 1024 * 1024;

    /// <summary>Сериализация результатов и хода: camelCase, перечисления строками.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string AppVersion =>
        typeof(HelperProtocol).Assembly.GetName().Version?.ToString() ?? "0";

    /// <param name="machine">Действия на весь компьютер от этого подключения принимаются (<see cref="MachineApi"/>).</param>
    public static string Hello(bool machine = true) => Line(new JsonObject
    {
        ["hello"] = new JsonObject { ["protocol"] = Version, ["api"] = Api, ["version"] = AppVersion, ["machine"] = machine },
    });

    public static string Ok(long id, object? result) => Line(new JsonObject
    {
        ["id"] = id, ["ok"] = true, ["result"] = result is null ? null : JsonSerializer.SerializeToNode(result, result.GetType(), Json),
    });

    public static string Error(long id, string code, string message, object? details = null) => Line(new JsonObject
    {
        ["id"] = id, ["ok"] = false,
        ["error"] = new JsonObject
        {
            ["code"] = code, ["message"] = message,
            ["details"] = details is null ? null : JsonSerializer.SerializeToNode(details, details.GetType(), Json),
        },
    });

    public static string Progress(long id, object progress) => Line(new JsonObject
    {
        ["id"] = id, ["progress"] = JsonSerializer.SerializeToNode(progress, progress.GetType(), Json),
    });

    static string Line(JsonObject o) => o.ToJsonString(Json) + "\n";
}

/// <summary>Коды ошибок канала и их понятные тексты.</summary>
public static class HelperErrors
{
    /// <summary>Команды нет в белом списке, лишние поля, неверные значения, путь или командная строка в аргументах.</summary>
    public const string Invalid = "invalid";
    /// <summary>Уже идёт другая операция (подбор, поиск, включение…).</summary>
    public const string Busy = "busy";
    /// <summary>Мешают другие программы обхода; список — в details.</summary>
    public const string Conflict = "conflict";
    /// <summary>Команда принята, но не выполнилась (ядро повреждено, sc отказал, сеть…).</summary>
    public const string Failed = "failed";
    /// <summary>Клиент не из разрешённых пользователей.</summary>
    public const string Denied = "denied";
    /// <summary>Операцию отменили.</summary>
    public const string Cancelled = "cancelled";
    /// <summary>Окно само не отправило команду: мажор протокола помощника другой — только переустановка (<see cref="HelperCompat"/>).</summary>
    public const string Incompatible = "incompatible";

    public static string Text(string code) => code switch
    {
        Invalid => L.T("core.helper.err.invalid"),
        Busy => L.T("core.helper.err.busy"),
        Conflict => L.T("core.helper.err.conflict"),
        Failed => L.T("core.helper.err.failed"),
        Denied => L.T("core.helper.err.denied"),
        Cancelled => L.T("core.helper.err.cancelled"),
        Incompatible => L.T("core.helper.err.incompatible"),
        _ => L.T("core.helper.err.other"),
    };
}

/// <summary>Помощник ответил ошибкой. <see cref="Code"/> — из <see cref="HelperErrors"/>.</summary>
public class HelperException(string code, string message, JsonElement? details = null) : Exception(message)
{
    public string Code { get; } = code;
    /// <summary>Подробности (для conflict — список мешающих программ).</summary>
    public JsonElement? Details { get; } = details;
}

public enum HelperUnavailableReason
{
    /// <summary>Служба DpiShellHelper не установлена — нужна установка (один запрос прав).</summary>
    NotInstalled,
    /// <summary>Служба есть, но канала нет: не запущена или упала.</summary>
    NotRunning,
    /// <summary>Канал есть, но помощник не ответил вовремя.</summary>
    NotResponding,
    /// <summary>Связь оборвалась во время работы.</summary>
    Disconnected,
}

/// <summary>Помощника нет или он не отвечает — окно показывает «Нужен помощник» или «Помощник не отвечает».</summary>
public sealed class HelperUnavailableException(HelperUnavailableReason reason, Exception? inner = null)
    : Exception(TextFor(reason), inner)
{
    public HelperUnavailableReason Reason { get; } = reason;

    static string TextFor(HelperUnavailableReason r) => r switch
    {
        HelperUnavailableReason.NotInstalled => L.T("core.helper.unavailable.not_installed"),
        HelperUnavailableReason.NotRunning => L.T("core.helper.unavailable.not_running"),
        HelperUnavailableReason.NotResponding => L.T("core.helper.unavailable.not_responding"),
        _ => L.T("core.helper.unavailable.lost"),
    };
}

/// <summary>Чтение строк из канала с пределом длины (чужой клиент не забьёт память бесконечной строкой).</summary>
public sealed class LineReader(Stream stream, int maxBytes = HelperProtocol.MaxLineBytes)
{
    readonly byte[] _buf = new byte[64 * 1024];
    int _pos, _len;
    readonly MemoryStream _line = new();

    /// <summary>Следующая строка без '\n' (и без '\r' в конце); null — собеседник закрыл канал.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        _line.SetLength(0);
        while (true)
        {
            if (_pos == _len)
            {
                _len = await stream.ReadAsync(_buf, ct);
                _pos = 0;
                if (_len == 0) return null;
            }
            int nl = Array.IndexOf(_buf, (byte)'\n', _pos, _len - _pos);
            int end = nl < 0 ? _len : nl;
            if (_line.Length + (end - _pos) > maxBytes) throw new InvalidDataException("Слишком длинное сообщение в канале помощника.");
            _line.Write(_buf, _pos, end - _pos);
            _pos = nl < 0 ? _len : nl + 1;
            if (nl >= 0)
            {
                var bytes = _line.GetBuffer().AsSpan(0, (int)_line.Length);
                if (bytes.Length > 0 && bytes[^1] == '\r') bytes = bytes[..^1];
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
