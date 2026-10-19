using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DpiShell.Core;

/// <summary>Метки TCP в системе; в тестах — подмена в памяти (настоящий netsh тесты не вызывают).</summary>
public interface ITcpTimestampsSystem
{
    bool? IsEnabled();
    bool Set(bool enabled);
}

public sealed class NetshTcpTimestamps : ITcpTimestampsSystem
{
    public bool? IsEnabled() => TcpTimestamps.IsEnabled();
    public bool Set(bool enabled) => TcpTimestamps.Set(enabled);
}

/// <summary>
/// Временные метки TCP (RFC 1323). Без них стратегии с <c>--dpi-desync-fooling=ts</c> молча не работают: winws портит
/// фейк-пакет меткой, которой нет. На чистой Windows они выключены; Flowseal включает их при каждом запуске.
/// Мы включаем автоматически (architecture.md «Временные метки TCP») и запоминаем, что включили сами, — чтобы вернуть.
/// </summary>
public static partial class TcpTimestamps
{
    /// <summary>null — не удалось узнать (netsh недоступен).</summary>
    public static bool? IsEnabled()
    {
        var (code, output) = Netsh("interface", "tcp", "show", "global");
        if (code != 0) return null;
        // Подпись netsh переводит («Метки времени RFC 1323» на русской Windows), а номер RFC и enabled/disabled — нет.
        var m = StateRx().Match(output);
        return m.Success ? m.Groups[1].Value.Equals("enabled", StringComparison.OrdinalIgnoreCase) : null;
    }

    public static bool Set(bool enabled) =>
        Netsh("interface", "tcp", "set", "global", "timestamps=" + (enabled ? "enabled" : "disabled")).Code == 0;

    static (int Code, string Output) Netsh(params string[] args)
    {
        var psi = new ProcessStartInfo("netsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            var o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(10000);
            return (p.ExitCode, o);
        }
        catch (System.ComponentModel.Win32Exception) { return (-1, ""); }
    }

    [GeneratedRegex(@"RFC 1323[^:\r\n]*:\s*(enabled|disabled)", RegexOptions.IgnoreCase)]
    private static partial Regex StateRx();
}
