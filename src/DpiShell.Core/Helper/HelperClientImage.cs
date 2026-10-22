using DpiShell.Core.Localization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace DpiShell.Core.Helper;

/// <summary>Кто на том конце канала по образу процесса (API 1.14, helper-protocol §5, «Действия на весь компьютер»).</summary>
public enum ClientBuild
{
    /// <summary>Та же сборка DpiShell, что помощник (совпали хеши), — или проверка выключена.</summary>
    Same,
    /// <summary>DpiShell старее помощника: обновить программу.</summary>
    WindowOlder,
    /// <summary>DpiShell новее помощника: обновить помощника.</summary>
    WindowNewer,
    /// <summary>Другая сборка той же версии, не DpiShell или образ не прочитать.</summary>
    Other,
}

/// <summary>
/// Проверка клиента канала по образу процесса: <c>GetNamedPipeClientProcessId</c> → путь exe → sha256 exe (и сборок DpiShell рядом с ним,
/// если программа собрана не одним файлом) сверяется с копией помощника в закрытой папке — её туда положило окно, установившее помощника.
/// <para>
/// Защищает от простого случая: чужая программа пользователя пишет в канал сама. Не защищает от процесса того же пользователя, который
/// запустит настоящий DpiShell.exe и внедрится в него (отладка, запись в память, перехват ввода): у процессов одного пользователя нет
/// границы прав. Портативная папка пользователя записываема, поэтому доверие — по хешу, а не по пути.
/// </para>
/// </summary>
public sealed class HelperClientImage
{
    readonly string _helperExe;
    readonly string _helperVersion;
    readonly Lazy<string?> _helperPrint;
    readonly ConcurrentDictionary<string, (long Length, DateTime Written, string? Print)> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сборки рядом с exe, без которых программа не та же (сборка не одним файлом — разработка); у single-file их нет.</summary>
    public static readonly IReadOnlyList<string> Companions = ["DpiShell.dll", "DpiShell.Core.dll"];

    /// <param name="helperExe">Exe помощника (закрытая папка).</param>
    /// <param name="helperVersion">Версия программы помощника; null — своя.</param>
    public HelperClientImage(string helperExe, string? helperVersion = null)
    {
        _helperExe = Path.GetFullPath(helperExe);
        _helperVersion = helperVersion ?? HelperProtocol.AppVersion;
        _helperPrint = new(() => Fingerprint(_helperExe, Companions.Where(c => File.Exists(Path.Combine(Path.GetDirectoryName(_helperExe)!, c))).ToList()));
    }

    /// <summary>Посчитать отпечаток помощника заранее (при старте службы), чтобы первое подключение окна не ждало хеша.</summary>
    public void Warm() => _ = _helperPrint.Value;

    /// <summary>Сверить образ клиента. null или не читается — <see cref="ClientBuild.Other"/>.</summary>
    public ClientBuild Check(string? clientExe)
    {
        if (string.IsNullOrEmpty(clientExe) || _helperPrint.Value is not { } own) return ClientBuild.Other;
        string? print;
        try
        {
            var fi = new FileInfo(clientExe);
            if (!fi.Exists) return ClientBuild.Other;
            if (_cache.TryGetValue(fi.FullName, out var hit) && hit.Length == fi.Length && hit.Written == fi.LastWriteTimeUtc) print = hit.Print;
            else
            {
                var companions = Companions.Where(c => File.Exists(Path.Combine(Path.GetDirectoryName(_helperExe)!, c))).ToList();
                print = Fingerprint(fi.FullName, companions);
                _cache[fi.FullName] = (fi.Length, fi.LastWriteTimeUtc, print);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ClientBuild.Other; }
        if (print is not null && print == own) return ClientBuild.Same;
        // Не та сборка: подсказать, что обновлять. Версия — из ресурсов exe; не DpiShell или без версии — «другая».
        var version = VersionOf(clientExe);
        if (version is null || !Version.TryParse(_helperVersion, out var h)) return ClientBuild.Other;
        return version < h ? ClientBuild.WindowOlder : version > h ? ClientBuild.WindowNewer : ClientBuild.Other;
    }

    /// <summary>Фраза отказа для действия на весь компьютер (на языке запроса).</summary>
    public static string Refusal(ClientBuild b) => b switch
    {
        ClientBuild.WindowOlder => L.T("core.helper.machine.window_older"),
        ClientBuild.WindowNewer => L.T("core.helper.machine.window_newer"),
        _ => L.T("core.helper.machine.other"),
    };

    static Version? VersionOf(string exe)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(exe);
            if (!string.Equals(info.ProductName, "DpiShell", StringComparison.Ordinal)) return null;
            return Version.TryParse(info.FileVersion, out var v) ? v : null;
        }
        catch (Exception e) when (e is IOException or ArgumentException) { return null; }
    }

    /// <summary>sha256 exe и сборок-спутников (по имени, в том же каталоге). Нет спутника, который есть у помощника, — отпечаток другой.</summary>
    public static string? Fingerprint(string exe, IReadOnlyList<string> companions)
    {
        try
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(exe))!;
            var sb = new StringBuilder(Hash(exe));
            foreach (var c in companions)
            {
                var f = Path.Combine(dir, c);
                sb.Append('|').Append(c).Append(':').Append(File.Exists(f) ? Hash(f) : "-");
            }
            return sb.ToString();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }

        static string Hash(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(s));
        }
    }

    /// <summary>Путь exe процесса на том конце канала; не узнать — null.</summary>
    public static string? ClientPath(PipeStream pipe)
    {
        try
        {
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid) || pid == 0) return null;
            return ProcessPath(pid);
        }
        catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException) { return null; }
    }

    /// <summary>Путь exe процесса по PID (нужно лишь PROCESS_QUERY_LIMITED_INFORMATION); не узнать — null.</summary>
    public static string? ProcessPath(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var buf = new StringBuilder(1024);
            int size = buf.Capacity;
            return QueryFullProcessImageNameW(h, 0, buf, ref size) ? buf.ToString(0, size) : null;
        }
        finally { CloseHandle(h); }
    }

    const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(IntPtr handle);
}
