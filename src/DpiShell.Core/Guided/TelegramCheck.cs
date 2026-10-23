using System.Net.Sockets;

namespace DpiShell.Core;

public enum TelegramState { Open, Blocked, Unknown }

/// <summary>
/// Строка Telegram в «Диагностике» (docs: telegram-check). Одна TCP-проба к серверу Telegram и к web.telegram.org:443. На подбор не влияет:
/// если соединение не устанавливается вовсе, оптимизация (она меняет пакеты уже внутри соединения) не поможет — нужен прокси.
/// </summary>
public static class TelegramCheck
{
    public static readonly (string Host, int Port)[] Targets = [("149.154.167.51", 443), ("web.telegram.org", 443)];
    /// <summary>Обычные сайты: если не открывается и их TCP, интернета нет — про Telegram сказать нечего.</summary>
    public static readonly (string Host, int Port)[] Control = [("ya.ru", 443), ("vk.com", 443)];

    public delegate Task<bool> Tcp(string host, int port, CancellationToken ct);

    public static async Task<TelegramState> RunAsync(Tcp tcp, CancellationToken ct = default)
    {
        var tg = await Task.WhenAll(Targets.Select(t => tcp(t.Host, t.Port, ct)));
        if (tg.Any(x => x)) return TelegramState.Open;
        var control = await Task.WhenAll(Control.Select(t => tcp(t.Host, t.Port, ct)));
        return control.Any(x => x) ? TelegramState.Blocked : TelegramState.Unknown;
    }

    /// <summary>Настоящее TCP-соединение, 4 с.</summary>
    public static async Task<bool> ConnectAsync(string host, int port, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var c = new TcpClient();
            await c.ConnectAsync(host, port, cts.Token);
            return true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException) { return false; }
    }
}
