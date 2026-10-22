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
        return Parse(output);
    }

    /// <summary>
    /// Разбор вывода <c>netsh interface tcp show global</c>. Подпись переводится («Метки времени RFC 1323» на русской Windows), а номер RFC и значение — нет.
    /// Значения: enabled / disabled; на новых Windows и Windows Server ещё allowed («отвечать меткой, только если её прислал собеседник») — сами метки не отправляются,
    /// как выключенные. Регистр не важен; значение не узнали — null.
    /// </summary>
    public static bool? Parse(string output)
    {
        var m = StateRx().Match(output ?? "");
        if (!m.Success) return null;
        var v = m.Groups[1].Value;
        if (v.Equals("enabled", StringComparison.OrdinalIgnoreCase)) return true;
        if (v.Equals("disabled", StringComparison.OrdinalIgnoreCase) || v.Equals("allowed", StringComparison.OrdinalIgnoreCase)) return false;
        return null;
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

    [GeneratedRegex(@"RFC\s*1323[^:\r\n]*:\s*([A-Za-z]+)", RegexOptions.IgnoreCase)]
    private static partial Regex StateRx();
}
