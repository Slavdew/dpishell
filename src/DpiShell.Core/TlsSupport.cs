using System.Security.Authentication;

namespace DpiShell.Core;

/// <summary>
/// Умеет ли эта Windows клиентский TLS 1.3. Проверки идут через Schannel, а он умеет TLS 1.3 клиентом только с Windows 11 / Server 2022
/// (сборка ≥ 20348); на Windows 10 любое соединение TLS 1.3 падает с SEC_E_ALGORITHM_MISMATCH. Без 1.3 все проверки — только по TLS 1.2,
/// иначе подбор бракует все стратегии, а сторож видит вечный провал.
/// Решение — один раз на сеанс: по сборке и по самопроверке (<see cref="NoteMismatch"/>: несовпадение алгоритмов на 1.3 при успехе 1.2).
/// В тестах — <see cref="Assume"/>: своё состояние на поток выполнения, система не спрашивается.
/// </summary>
public static class TlsSupport
{
    /// <summary>Первая сборка с клиентским TLS 1.3 в Schannel (Windows Server 2022; Windows 11 — 22000).</summary>
    public const int FirstTls13Build = 20348;

    /// <summary>SEC_E_ALGORITHM_MISMATCH: «у клиента и сервера разный алгоритм работы».</summary>
    public const int AlgorithmMismatch = unchecked((int)0x80090331);

    /// <summary>Сколько разных сайтов должны дать несовпадение на 1.3 при рабочем 1.2, чтобы признать 1.3 недоступным там, где по сборке он есть.</summary>
    public const int MismatchHosts = 2;

    sealed class State(bool byBuild)
    {
        public readonly bool ByBuild = byBuild;
        public volatile bool Missing = !byBuild;
        public readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase);
    }

    static readonly State Machine = new(BuildHasTls13(Environment.OSVersion.Version));
    static readonly AsyncLocal<State?> Local = new();
    static State Current => Local.Value ?? Machine;

    public static bool BuildHasTls13(Version os) => os.Major > 10 || (os.Major == 10 && os.Build >= FirstTls13Build);

    /// <summary>Проверять ли TLS 1.3.</summary>
    public static bool Tls13Client => !Current.Missing;

    /// <summary>По сборке TLS 1.3 есть, а самопроверка показала, что нет (отключён политикой или реестром).</summary>
    public static bool DisabledHere => Current.ByBuild && Current.Missing;

    /// <summary>Версии TLS для проверок: [1.3, 1.2] или только [1.2].</summary>
    public static SslProtocols[] Versions => Tls13Client ? [SslProtocols.Tls13, SslProtocols.Tls12] : [SslProtocols.Tls12];

    /// <summary>Версия для лёгкой проверки сторожа: 1.3 (строже), без него — 1.2.</summary>
    public static SslProtocols[] Watch => Tls13Client ? [SslProtocols.Tls13] : [SslProtocols.Tls12];

    /// <summary>Что из запрошенного проверять: без 1.3 он выбрасывается; не осталось ничего — 1.2.</summary>
    public static IReadOnlyList<SslProtocols> Effective(IEnumerable<SslProtocols> wanted)
    {
        var list = wanted.Distinct().ToList();
        if (!Tls13Client) list.Remove(SslProtocols.Tls13);
        if (list.Count == 0) list.Add(SslProtocols.Tls12);
        return list;
    }

    /// <summary>Самопроверка: сайт дал несовпадение алгоритмов на 1.3, а по 1.2 открылся. По сборке 1.3 нет — уже учтено; иначе — после <see cref="MismatchHosts"/> разных сайтов.</summary>
    public static void NoteMismatch(string host)
    {
        var s = Current;
        if (s.Missing) return;
        lock (s.Hosts)
        {
            s.Hosts.Add(host);
            if (s.Hosts.Count >= MismatchHosts) s.Missing = true;
        }
    }

    /// <summary>Исключение — несовпадение алгоритмов Schannel (так Windows 10 отвечает на любой TLS 1.3).</summary>
    public static bool IsAlgorithmMismatch(Exception e)
    {
        for (Exception? x = e; x is not null; x = x.InnerException)
            if (x.HResult == AlgorithmMismatch || x is System.ComponentModel.Win32Exception { NativeErrorCode: AlgorithmMismatch })
                return true;
        return false;
    }

    /// <summary>Отчёт подбора проверялся без TLS 1.3: здесь его нет или в проверках есть 1.2, но ни одной 1.3 (самопроверка у помощника).</summary>
    public static bool Tls12Only(SelectionReport r)
    {
        if (!Tls13Client) return true;
        var probes = r.Baseline.Concat(r.Scores.SelectMany(s => s.Verdicts)).SelectMany(v => v.Probes).ToList();
        return probes.Any(p => p.Tls == SslProtocols.Tls12) && probes.All(p => p.Tls != SslProtocols.Tls13);
    }

    /// <summary>Для тестов: своё состояние на этот поток выполнения (и всё, что из него запущено) — как будто сборка <paramref name="tls13"/> умеет или нет.</summary>
    public static IDisposable Assume(bool tls13)
    {
        var prev = Local.Value;
        Local.Value = new State(tls13);
        return new Undo(() => Local.Value = prev);
    }

    sealed class Undo(Action a) : IDisposable { public void Dispose() => a(); }
}
