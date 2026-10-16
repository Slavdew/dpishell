using System.Text;
using DpiShell.Core;

Console.OutputEncoding = Encoding.UTF8;
var cmd = args.FirstOrDefault() ?? "help";
var rest = args.Skip(1).ToArray();

// Без аргументов (двойной щелчок) — самопроверка: один запрос прав, все шаги в одном процессе, лог рядом с exe.
if (cmd is "help" && args.Length == 0 || cmd == "selftest")
    return await SelfTest();
// Долгие команды пишут лог рядом с exe и ждут Enter: их запускают ярлыком, окно иначе закроется.
if (cmd is "select" or "search" or "detect")
    return await Logged(cmd, () => Run(cmd, rest));
return await Run(cmd, rest);

static async Task<int> Logged(string name, Func<Task<int>> body)
{
    var logFile = Path.Combine(AppContext.BaseDirectory, name + ".log");
    using var file = new StreamWriter(logFile, false, new UTF8Encoding(false)) { AutoFlush = true };
    var console = Console.Out;
    Console.SetOut(new Tee(console, file));
    Console.SetError(Console.Out);
    Console.WriteLine($"DpiShell {name} {DateTime.Now:yyyy-MM-dd HH:mm:ss}, лог: {logFile}");
    var code = await body();
    Console.SetOut(console);
    Pause();
    return code;
}

static async Task<int> SelfTest()
{
    var logFile = Path.Combine(AppContext.BaseDirectory, "live.log");
    using var file = new StreamWriter(logFile, false, new UTF8Encoding(false)) { AutoFlush = true };
    var console = Console.Out;
    Console.SetOut(new Tee(console, file));
    Console.SetError(Console.Out);
    Console.WriteLine($"DpiShell самопроверка {DateTime.Now:yyyy-MM-dd HH:mm:ss}, лог: {logFile}");

    var shell = new Shell();
    var conflicts = Conflicts.Find(shell.Paths);
    if (conflicts.Count > 0)
    {
        Console.WriteLine("Сначала выключите другие программы обхода (Flowseal и т. п.) — с ними проверка бессмысленна:");
        foreach (var c in conflicts) Console.WriteLine($"  {c.Name}: {c.Detail}");
        Pause();
        return 2;
    }

    int failed = 0;
    foreach (var step in new[] { "status", "prepare", "dns", "dry-run", "probe", "test alt11", "select" })
    {
        Console.WriteLine($"=== {step}");
        var parts = step.Split(' ');
        if (await Run(parts[0], parts[1..]) != 0) failed++;
    }
    Console.WriteLine($"=== done, шагов с ошибкой: {failed}");
    Console.SetOut(console);
    Pause();
    return failed == 0 ? 0 : 1;
}

static void Pause()
{
    if (Console.IsInputRedirected) return;
    Console.WriteLine("Готово. Нажмите Enter, чтобы закрыть окно.");
    Console.ReadLine();
}

static async Task<int> Run(string cmd, string[] rest)
{
    try
    {
        var shell = new Shell();
        var log = new SyncProgress<string>(Console.WriteLine);
        switch (cmd)
        {
            case "prepare":
                var c = await shell.PrepareAsync(log);
                Console.WriteLine($"Ядро: {c.State} {string.Join(",", c.BadFiles)} лишние: {string.Join(",", c.ExtraFiles)}");
                break;
            case "check-core":
                var cc = shell.Installer.Check();
                Console.WriteLine($"Ядро: {cc.State} {string.Join(",", cc.BadFiles)}");
                break;
            case "list":
                foreach (var s in shell.AllStrategies) Console.WriteLine($"{s.Id,-22} {s.Title}");
                break;
            case "args":
                Console.WriteLine(WinwsArgs.Join(WinwsArgs.Build(Need(shell, rest), shell.Paths)));
                break;
            case "dry-run":
                foreach (var s in rest.Length > 0 ? [Need(shell, rest)] : shell.Catalog.Strategies)
                {
                    var (ok, o) = await WinwsProcess.DryRunAsync(shell.Paths, WinwsArgs.Build(s, shell.Paths));
                    Console.WriteLine($"{s.Id,-22} {(ok ? "OK" : "ОШИБКА")} {(ok ? "" : o.Trim())}");
                }
                break;
            case "probe":
                foreach (var v in await shell.CheckNowAsync()) Print(v);
                break;
            case "test":
                using (var doh = new DohResolver())
                {
                var one = await new Selector(shell.Paths, Shell.NewProbe(doh)).RunOneAsync(Need(shell, rest), shell.SelectedGroups(), default);
                Console.WriteLine($"{one.Strategy.Id}: группы {one.GroupsPassed}/{one.GroupsTotal} {one.Error}");
                foreach (var v in one.Verdicts) Print(v);
                }
                break;
            case "dns":
                foreach (var d in await shell.CheckDnsAsync())
                    Console.WriteLine($"  {(d.Spoofed ? "ПОДМЕНА" : "ok     ")} {d.Host,-30} система: {(d.System.Length > 0 ? string.Join(",", d.System.Take(2).Select(a => a.ToString())) : d.SystemError ?? "-")}  DoH: {string.Join(",", d.Doh.Take(2).Select(a => a.ToString()))}");
                break;
            case "select":
                using (var cts = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
                    Console.CancelKeyPress += onCancel;
                    try
                    {
                        var progress = new SyncProgress<SelectorProgress>(p => Console.WriteLine($"[{p.Index}/{p.Total}] {p.Message}"));
                        var r = await shell.SelectAsync(rest.Contains("--quick"), null, progress, cts.Token);
                        if (r.NothingBlocked) { Console.WriteLine("Без обхода всё открывается: блокировок нет или включён VPN."); break; }
                        foreach (var s in r.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds))
                            Console.WriteLine($"{s.Strategy.Id,-22} группы {s.GroupsPassed}/{s.GroupsTotal} цели {s.TargetShare:P0} {s.AvgSeconds:F2}с {s.Error}");
                        Console.WriteLine(r.Best is null ? "Ни одна стратегия не помогла." : $"Лучшая: {r.Best.Strategy.Id}");
                    }
                    finally { Console.CancelKeyPress -= onCancel; }
                }
                break;
            case "search":
                using (var cts = new CancellationTokenSource())
                {
                    ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; cts.Cancel(); };
                    Console.CancelKeyPress += onCancel;
                    try
                    {
                        var progress = new SyncProgress<SelectorProgress>(p => Console.WriteLine(p.Total > 0 ? $"[{p.Index}/{p.Total}] {p.Message}" : $"  #{p.Index} {p.Message}"));
                        var r = await shell.SearchAsync(rest.FirstOrDefault(), null, progress, cts.Token);
                        foreach (var (role, list) in r.ByRole)
                        {
                            Console.WriteLine($"Роль {role}: проверено {list.Count}, сработало {list.Count(x => x.Ok > 0)}");
                            foreach (var x in list.OrderByDescending(x => x.Share).ThenBy(x => x.Cost).Take(5))
                                Console.WriteLine($"  {x.Ok}/{x.Total} цена {x.Cost:F1} {x.Seconds:F2}с  {x.Desync}");
                        }
                        if (r.Final is { } f)
                            foreach (var s in f.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds))
                                Console.WriteLine($"{s.Strategy.Id,-22} группы {s.GroupsPassed}/{s.GroupsTotal} цели {s.TargetShare:P0} {s.AvgSeconds:F2}с {s.Error}");
                        Console.WriteLine(r.Stopped is null ? $"Найдено и сохранено: {r.Generated.Count}" : $"Поиск прерван: {r.Stopped}");
                    }
                    finally { Console.CancelKeyPress -= onCancel; }
                }
                break;
            case "detect":
                foreach (var z in ZapretDetector.Find(shell.Paths, shell.AllStrategies))
                    Console.WriteLine($"{z.Kind,-8} {z.Name,-14} {z.OwnerTitle,-22} стратегия: {z.Strategy?.Title ?? "не узнали"}  {z.ExePath}");
                var si = shell.InspectService();
                Console.WriteLine($"Служба DpiShell: {si.State}, наше ядро: {si.OurCore}, стратегия: {si.Strategy?.Title ?? "-"}, файл: {si.ExePath ?? "-"}");
                break;
            case "clear-history":
                Console.WriteLine($"Удалено файлов: {ResultStore.ClearHistory(shell.Paths)}");
                break;
            case "enable":
                shell.Enable(Need(shell, rest).Id);
                Console.WriteLine($"Служба: {shell.Service.State}");
                break;
            case "disable":
                shell.Disable();
                Console.WriteLine($"Служба: {shell.Service.State}");
                break;
            case "remove":
                shell.Service.Remove();
                Console.WriteLine("Служба и драйвер удалены.");
                break;
            case "status":
                Console.WriteLine($"Служба: {shell.Service.State}, стратегия: {shell.State.StrategyId ?? "-"}, админ: {Shell.IsAdmin()}");
                foreach (var x in Conflicts.Find(shell.Paths)) Console.WriteLine($"Мешает: {x.Kind} {x.Name} {x.Detail}");
                foreach (var v in Conflicts.ActiveVpnAdapters()) Console.WriteLine($"VPN: {v}");
                break;
            default:
                Console.WriteLine("dpishell-cli [selftest] | prepare | dns | check-core | list | args <id> | dry-run [id] | probe | test <id> | select [--quick] | search [base-id] | detect | clear-history | enable <id> | disable | remove | status");
                break;
        }
        return 0;
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"Ошибка: {e.Message}");
        return 1;
    }
}

static Strategy Need(Shell shell, string[] rest) =>
    rest.Length > 0 && shell.FindStrategy(rest[0]) is { } s ? s : throw new ArgumentException("Укажите id стратегии (dpishell-cli list)");

static void Print(TargetVerdict v) =>
    Console.WriteLine($"  {(v.Ok ? "OK " : "-- ")} {v.Target.Url,-55} {string.Join(" | ", v.Probes.Select(p => $"{p.Tls}: {p.Outcome} {p.Detail}"))}");

/// <summary>Progress без пересылки в пул потоков: строки лога идут по порядку.</summary>
sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

sealed class Tee(TextWriter a, TextWriter b) : TextWriter
{
    public override Encoding Encoding => a.Encoding;
    public override void Write(char value) { lock (this) { a.Write(value); b.Write(value); } }
    public override void Write(string? value) { lock (this) { a.Write(value); b.Write(value); } }
    public override void WriteLine(string? value) { lock (this) { a.WriteLine(value); b.WriteLine(value); } }
}
