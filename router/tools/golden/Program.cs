using System.Globalization;
using System.Text.Json;
using DpiShell.Core;
var json = new JsonSerializerOptions { WriteIndented = true };
// golden search — эталон генератора глубокого поиска (CandidateGenerator) для router/test/search_test.uc.
if (args[0] == "search")
{
    var coarse = CandidateGenerator.Coarse();
    var all = coarse.Concat(coarse.SelectMany(CandidateGenerator.Neighbours)).Distinct().ToList();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        source = "CandidateGenerator",
        coarse,
        neighbours = coarse.ToDictionary(c => c, c => CandidateGenerator.Neighbours(c).ToList()),
        cost = all.ToDictionary(c => c, c => Math.Round(CandidateGenerator.Cost(c), 3)),
    }, json));
    return;
}
// Эталон сборки аргументов winws для сверки с роутерным сборщиком: пути заменены на {lists}/{fake}.
var cat = StrategyCatalog.Load(args[0]);
var paths = new AppPaths(root: "ROOT");
string Norm(string a) => a.Replace(@"ROOT\lists\", "{lists}/").Replace(@"ROOT\fake\", "{fake}/");
var outp = cat.Strategies.ToDictionary(s => s.Id, s => WinwsArgs.Build(s, paths).Select(Norm).ToList());
Console.WriteLine(JsonSerializer.Serialize(new { source = "WinwsArgs.Build", strategies = outp }, json));
