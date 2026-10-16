using System.Text;
using DpiShell.Core;

Console.OutputEncoding = Encoding.UTF8;
var cmd = args.FirstOrDefault() ?? "help";
var rest = args.Skip(1).ToArray();

try
{
    var shell = new Shell();
    var log = new Progress<string>(Console.WriteLine);
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
            foreach (var s in shell.Catalog.Strategies) Console.WriteLine($"{s.Id,-22} {s.Title}");
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
            var one = await new Selector(shell.Paths, new HttpProbe()).RunOneAsync(Need(shell, rest), shell.SelectedGroups(), default);
            Console.WriteLine($"{one.Strategy.Id}: группы {one.GroupsPassed}/{one.GroupsTotal} {one.Error}");
            foreach (var v in one.Verdicts) Print(v);
            break;
        case "select":
            using (var cts = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
                var progress = new Progress<SelectorProgress>(p => Console.WriteLine($"[{p.Index}/{p.Total}] {p.Message}"));
                var r = await shell.SelectAsync(rest.Contains("--quick"), null, progress, cts.Token);
                if (r.NothingBlocked) { Console.WriteLine("Без обхода всё открывается: блокировок нет или включён VPN."); break; }
                foreach (var s in r.Scores.OrderByDescending(s => s.GroupShare).ThenByDescending(s => s.TargetShare).ThenBy(s => s.AvgSeconds))
                    Console.WriteLine($"{s.Strategy.Id,-22} группы {s.GroupsPassed}/{s.GroupsTotal} цели {s.TargetShare:P0} {s.AvgSeconds:F2}с {s.Error}");
                Console.WriteLine(r.Best is null ? "Ни одна стратегия не помогла." : $"Лучшая: {r.Best.Strategy.Id}");
            }
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
            Console.WriteLine("dpishell prepare | check-core | list | args <id> | dry-run [id] | probe | test <id> | select [--quick] | enable <id> | disable | remove | status");
            break;
    }
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Ошибка: {e.Message}");
    return 1;
}

static Strategy Need(Shell shell, string[] rest) =>
    rest.Length > 0 && shell.Catalog.Find(rest[0]) is { } s ? s : throw new ArgumentException("Укажите id стратегии (dpishell list)");

static void Print(TargetVerdict v) =>
    Console.WriteLine($"  {(v.Ok ? "OK " : "-- ")} {v.Target.Url,-55} {string.Join(" | ", v.Probes.Select(p => $"{p.Tls}: {p.Outcome} {p.Detail}"))}");
